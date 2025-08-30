using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        private static bool _masksInit;

        /// Pre-computes bit masks identifying every boundary voxel for a 16^3 section in the
        /// shared linear layout: li = ((z * 16 + x) * 16) + y (column-major in Y inside each XZ column).
        /// These masks allow us to quickly exclude boundary layer bits during internal face detection
        /// then selectively re‑add only the actually visible boundary faces (after cross‑section /
        /// chunk boundary occlusion tests).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureMasks()
        {
            if (_masksInit) return;

            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int li = ((z * 16 + x) * 16) + y;   // linear index
                        int w  = li >> 6;                  // 64-bit word index (0..63)
                        int b  = li & 63;                  // bit in that word (0..63)
                        ulong bit = 1UL << b;

                        if (x == 0)       _maskX0[w]  |= bit; else if (x == 15) _maskX15[w] |= bit;
                        if (y == 0)       _maskY0[w]  |= bit; else if (y == 15) _maskY15[w] |= bit;
                        if (z == 0)       _maskZ0[w]  |= bit; else if (z == 15) _maskZ15[w] |= bit;
                    }
                }
            }
            _masksInit = true;
        }

        /// Emits face instances for a DenseExpanded section (Kind=3) using a fast bitset based
        /// internal face culling strategy:
        ///  1. Build an occupancy bitset (4096 bits = 64 ulong) of non‑air voxels.
        ///  2. Derive six internal face masks by shifting the occupancy bitset along each axis
        ///     and removing covered internal faces (excluding boundary bits first).
        ///  3. Re‑evaluate boundary layer voxels and integrate only those faces that are actually
        ///     visible (not occluded by neighbor sections nor by outer chunk neighbor plane bitsets).
        ///  4. Iterate the final six face masks and emit one instance per visible face.
        /// 
        /// Returns true indicating the section was handled (so the fallback scanner is skipped).
        private bool EmitDenseExpandedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            if (desc.ExpandedDense == null || desc.NonAirCount == 0)
                return true; // nothing to emit

            EnsureMasks();

            ushort[] dense = desc.ExpandedDense; // direct block ids (length 4096)

            // ---------------------------------------------------------------------------------
            // 1. Build occupancy bitset (set bit => solid voxel)
            // ---------------------------------------------------------------------------------
            Span<ulong> occ = stackalloc ulong[64];
            for (int li = 0; li < 4096; li++)
            {
                if (dense[li] == 0) continue; // air
                occ[li >> 6] |= 1UL << (li & 63);
            }

            // ---------------------------------------------------------------------------------
            // 2. Derive internal (non‑boundary) face masks by shifting occupancy.
            //    We first compute candidate faces, excluding boundary bits so boundary handling
            //    (which must consult neighbors / chunk planes) is centralized later.
            // ---------------------------------------------------------------------------------
            Span<ulong> shift = stackalloc ulong[64];
            Span<ulong> faceNX = stackalloc ulong[64]; // -X
            Span<ulong> facePX = stackalloc ulong[64]; // +X
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y
            Span<ulong> facePY = stackalloc ulong[64]; // +Y
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z

            const int strideX = 16;   // delta in linear index when moving +X (skip one 16‑voxel Y column)
            const int strideY = 1;    // +Y increments by 1 in linear layout
            const int strideZ = 256;  // +Z skips 16*16 voxels

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftLeft(ReadOnlySpan<ulong> src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6;
                int bitShift  = shiftBits & 63;
                for (int i = 63; i >= 0; i--)
                {
                    ulong v = 0;
                    int si = i - wordShift;
                    if (si >= 0)
                    {
                        v = src[si];
                        if (bitShift != 0)
                        {
                            ulong carry = (si - 1 >= 0) ? src[si - 1] : 0;
                            v = (v << bitShift) | (carry >> (64 - bitShift));
                        }
                    }
                    dst[i] = v;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftRight(ReadOnlySpan<ulong> src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6;
                int bitShift  = shiftBits & 63;
                for (int i = 0; i < 64; i++)
                {
                    ulong v = 0;
                    int si = i + wordShift;
                    if (si < 64)
                    {
                        v = src[si];
                        if (bitShift != 0)
                        {
                            ulong carry = (si + 1 < 64) ? src[si + 1] : 0;
                            v = (v >> bitShift) | (carry << (64 - bitShift));
                        }
                    }
                    dst[i] = v;
                }
            }

            // -X internal faces
            ShiftLeft(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX0[i]; // exclude boundary layer at x=0
                faceNX[i] = cand & ~shift[i];      // keep only bits not covered by neighbor solid
            }
            // +X
            ShiftRight(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX15[i];
                facePX[i] = cand & ~shift[i];
            }
            // -Y
            ShiftLeft(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY0[i];
                faceNY[i] = cand & ~shift[i];
            }
            // +Y
            ShiftRight(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY15[i];
                facePY[i] = cand & ~shift[i];
            }
            // -Z
            ShiftLeft(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ0[i];
                faceNZ[i] = cand & ~shift[i];
            }
            // +Z
            ShiftRight(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ15[i];
                facePZ[i] = cand & ~shift[i];
            }

            // ---------------------------------------------------------------------------------
            // 3. Boundary layer integration – for every occupied boundary voxel determine
            //    if its outward face is visible considering:
            //      * world chunk edge neighbor plane bitsets (treat as opaque occupancy)
            //      * adjacent section/voxel presence via GetBlock queries
            // ---------------------------------------------------------------------------------
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            for (int wi = 0; wi < 64; wi++)
            {
                // Reusable local lambda to process one boundary bit & conditionally set target mask
                void ProcessBoundaryWord(ulong bits, ulong[] targetMask, ulong planeMaskNeg, ulong planeMaskPos) { /* placeholder not used – kept for structure */ }

                // -X boundary candidates
                ulong word = occ[wi] & _maskX0[wi];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;
                    int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                    bool hidden = false;
                    if (wx == 0)
                    {
                        if (PlaneBit(planeNegX, wz * maxY + wy)) hidden = true;
                    }
                    else if (GetBlock(wx - 1, wy, wz) != 0) hidden = true;
                    if (!hidden) faceNX[wi] |= 1UL << bit;
                }

                // +X boundary
                word = occ[wi] & _maskX15[wi];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;
                    int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                    bool hidden = false;
                    if (wx == maxX - 1)
                    {
                        if (PlaneBit(planePosX, wz * maxY + wy)) hidden = true;
                    }
                    else if (GetBlock(wx + 1, wy, wz) != 0) hidden = true;
                    if (!hidden) facePX[wi] |= 1UL << bit;
                }

                // -Y boundary
                word = occ[wi] & _maskY0[wi];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;
                    int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                    bool hidden = false;
                    if (wy == 0)
                    {
                        if (PlaneBit(planeNegY, wx * maxZ + wz)) hidden = true;
                    }
                    else if (GetBlock(wx, wy - 1, wz) != 0) hidden = true;
                    if (!hidden) faceNY[wi] |= 1UL << bit;
                }

                // +Y boundary
                word = occ[wi] & _maskY15[wi];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;
                    int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                    bool hidden = false;
                    if (wy == maxY - 1)
                    {
                        if (PlaneBit(planePosY, wx * maxZ + wz)) hidden = true;
                    }
                    else if (GetBlock(wx, wy + 1, wz) != 0) hidden = true;
                    if (!hidden) facePY[wi] |= 1UL << bit;
                }

                // -Z boundary
                word = occ[wi] & _maskZ0[wi];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;
                    int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                    bool hidden = false;
                    if (wz == 0)
                    {
                        if (PlaneBit(planeNegZ, wx * maxY + wy)) hidden = true;
                    }
                    else if (GetBlock(wx, wy, wz - 1) != 0) hidden = true;
                    if (!hidden) faceNZ[wi] |= 1UL << bit;
                }

                // +Z boundary
                word = occ[wi] & _maskZ15[wi];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;
                    int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                    bool hidden = false;
                    if (wz == maxZ - 1)
                    {
                        if (PlaneBit(planePosZ, wx * maxY + wy)) hidden = true;
                    }
                    else if (GetBlock(wx, wy, wz + 1) != 0) hidden = true;
                    if (!hidden) facePZ[wi] |= 1UL << bit;
                }
            }

            // ---------------------------------------------------------------------------------
            // 4. Emit visible faces. For each set bit decode (lx,ly,lz), fetch block id again
            //    from dense array (already in cache) and append an instance.
            // ---------------------------------------------------------------------------------
            void EmitMask(Span<ulong> mask, byte faceDir)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                        ushort block = dense[li];
                        if (block == 0) continue;
                        EmitFaceInstance(block, faceDir, baseX + lx, baseY + ly, baseZ + lz,
                                         offsetList, tileIndexList, faceDirList);
                    }
                }
            }

            EmitMask(faceNX, 0);
            EmitMask(facePX, 1);
            EmitMask(faceNY, 2);
            EmitMask(facePY, 3);
            EmitMask(faceNZ, 4);
            EmitMask(facePZ, 5);

            return true; // handled
        }
    }
}

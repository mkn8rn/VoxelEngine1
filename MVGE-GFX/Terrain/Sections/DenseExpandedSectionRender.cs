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

        /// Ensures shared boundary bit masks (_maskX0, _maskX15, etc.) are initialized.
        /// Dense path reuses the packed path masks. We keep a local flag so legacy callers
        /// that only touch the dense path can still function.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureMasks()
        {
            if (_masksInit) return;
            // Under normal flow PackedSectionRender.EnsureBoundaryMasks builds these.
            if (!_boundaryMasksInit)
            {
                EnsureBoundaryMasks();
            }
            _masksInit = true;
        }

        /// Emits face instances for a DenseExpanded (Kind==3) section using pre-computed
        /// occupancy + boundary plane masks (now generated during section finalization).
        /// Steps:
        ///  1. Produce internal face bitsets by shifting occupancy and removing occluded pairs
        ///     (skipping boundary voxels so boundary handling is centralized).
        ///  2. Re-introduce boundary faces by consulting boundary plane bitsets and external
        ///     chunk neighbor planes / adjacent section voxels.
        ///  3. Iterate each face direction mask and emit an instance for every surviving bit.
        /// Returns true if handled; false signals fallback brute scan.
        private bool EmitDenseExpandedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            // Quick rejection / no-op
            if (desc.NonAirCount == 0 || desc.Kind != 3)
                return true; // nothing to emit (handled)

            // If occupancy is somehow missing (should be rare after refactor) fall back
            if (desc.OccupancyBits == null)
                return false; // ask caller to use brute fallback path

            EnsureMasks();
            EnsureLiDecode(); // decode tables (lx, ly, lz) shared with packed path

            var occ = desc.OccupancyBits;          // 64 ulongs for 4096 voxels
            ushort[] dense = desc.ExpandedDense;   // dense block id array (length 4096)

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            // ---------------------------------------------------------------------------------
            // 1. Internal faces: compute six direction masks ignoring boundary voxels.
            //    We shift the occupancy bitset along each axis and mark faces where a solid
            //    voxel does NOT have a solid neighbor. Boundary layers are excluded here and
            //    processed later with neighbor / chunk plane tests.
            // ---------------------------------------------------------------------------------
            Span<ulong> shift = stackalloc ulong[64]; // temporary shift buffer
            Span<ulong> faceNX = stackalloc ulong[64]; // -X faces
            Span<ulong> facePX = stackalloc ulong[64]; // +X faces
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y faces
            Span<ulong> facePY = stackalloc ulong[64]; // +Y faces
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z faces
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z faces

            const int strideX = 16;   // linear index delta for +X (one 16-voxel Y column)
            const int strideY = 1;    // +Y delta
            const int strideZ = 256;  // +Z delta (16 * 16)

            // Bitset shift helpers ----------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftLeft(ReadOnlySpan<ulong> src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6;
                int bitShift = shiftBits & 63;
                for (int i = 63; i >= 0; i--)
                {
                    ulong v = 0;
                    int si = i - wordShift;
                    if (si >= 0)
                    {
                        v = src[si];
                        if (bitShift != 0)
                        {
                            ulong carry = (si - 1 >= 0) ? src[si - 1] : 0UL;
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
                int bitShift = shiftBits & 63;
                for (int i = 0; i < 64; i++)
                {
                    ulong v = 0;
                    int si = i + wordShift;
                    if (si < 64)
                    {
                        v = src[si];
                        if (bitShift != 0)
                        {
                            ulong carry = (si + 1 < 64) ? src[si + 1] : 0UL;
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
                ulong candidates = occ[i] & ~_maskX0[i];   // ignore boundary layer at x=0
                faceNX[i] = candidates & ~shift[i];         // keep only uncovered faces
            }
            // +X internal faces
            ShiftRight(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskX15[i];  // ignore boundary layer at x=15
                facePX[i] = candidates & ~shift[i];
            }
            // -Y internal faces
            ShiftLeft(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskY0[i];
                faceNY[i] = candidates & ~shift[i];
            }
            // +Y internal faces
            ShiftRight(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskY15[i];
                facePY[i] = candidates & ~shift[i];
            }
            // -Z internal faces
            ShiftLeft(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskZ0[i];
                faceNZ[i] = candidates & ~shift[i];
            }
            // +Z internal faces
            ShiftRight(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskZ15[i];
                facePZ[i] = candidates & ~shift[i];
            }

            // ---------------------------------------------------------------------------------
            // 2. Boundary faces: selectively add only those boundary voxels whose outward face
            //    is not occluded by chunk neighbor plane bits or a directly adjacent voxel.
            // ---------------------------------------------------------------------------------
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // Precomputed per-face boundary voxel presence (256 bits each)
            ulong[] bNegX = desc.FaceNegXBits; ulong[] bPosX = desc.FacePosXBits;
            ulong[] bNegY = desc.FaceNegYBits; ulong[] bPosY = desc.FacePosYBits;
            ulong[] bNegZ = desc.FaceNegZBits; ulong[] bPosZ = desc.FacePosZBits;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6;
                int b = index & 63;
                if (w >= plane.Length) return false;
                return (plane[w] & (1UL << b)) != 0UL;
            }

            // LEFT boundary (x = 0)
            if (bNegX != null)
            {
                int worldX = baseX;
                for (int z = 0; z < 16; z++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int planeIndex = z * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        if ((bNegX[w] & (1UL << b)) == 0) continue; // no voxel here

                        bool hidden = false;
                        if (worldX == 0)
                        {
                            // Outer chunk boundary: consult neighbor chunk plane (-X face of neighbor)
                            if (PlaneBit(planeNegX, (baseZ + z) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (GetBlock(worldX - 1, baseY + y, baseZ + z) != 0)
                        {
                            hidden = true; // internal neighbor solid
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16) + 0) * 16 + y; // linear index of voxel at x=0
                            faceNX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // RIGHT boundary (x = 15)
            if (bPosX != null)
            {
                int worldX = baseX + 15;
                for (int z = 0; z < 16; z++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int planeIndex = z * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        if ((bPosX[w] & (1UL << b)) == 0) continue;

                        bool hidden = false;
                        if (worldX == maxX - 1)
                        {
                            if (PlaneBit(planePosX, (baseZ + z) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (GetBlock(worldX + 1, baseY + y, baseZ + z) != 0)
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16) + 15) * 16 + y;
                            facePX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // BOTTOM boundary (y = 0)
            if (bNegY != null)
            {
                int worldY = baseY;
                for (int x = 0; x < 16; x++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        int planeIndex = x * 16 + z;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        if ((bNegY[w] & (1UL << b)) == 0) continue;

                        bool hidden = false;
                        if (worldY == 0)
                        {
                            if (PlaneBit(planeNegY, (baseX + x) * maxZ + (baseZ + z))) hidden = true;
                        }
                        else if (GetBlock(baseX + x, worldY - 1, baseZ + z) != 0)
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 0; // y=0
                            faceNY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // TOP boundary (y = 15)
            if (bPosY != null)
            {
                int worldY = baseY + 15;
                for (int x = 0; x < 16; x++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        int planeIndex = x * 16 + z;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        if ((bPosY[w] & (1UL << b)) == 0) continue;

                        bool hidden = false;
                        if (worldY == maxY - 1)
                        {
                            if (PlaneBit(planePosY, (baseX + x) * maxZ + (baseZ + z))) hidden = true;
                        }
                        else if (GetBlock(baseX + x, worldY + 1, baseZ + z) != 0)
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 15; // y=15
                            facePY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // BACK boundary (z = 0)
            if (bNegZ != null)
            {
                int worldZ = baseZ;
                for (int x = 0; x < 16; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int planeIndex = x * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        if ((bNegZ[w] & (1UL << b)) == 0) continue;

                        bool hidden = false;
                        if (worldZ == 0)
                        {
                            if (PlaneBit(planeNegZ, (baseX + x) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (GetBlock(baseX + x, baseY + y, worldZ - 1) != 0)
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((0 * 16 + x) * 16) + y; // z=0
                            faceNZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // FRONT boundary (z = 15)
            if (bPosZ != null)
            {
                int worldZ = baseZ + 15;
                for (int x = 0; x < 16; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int planeIndex = x * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        if ((bPosZ[w] & (1UL << b)) == 0) continue;

                        bool hidden = false;
                        if (worldZ == maxZ - 1)
                        {
                            if (PlaneBit(planePosZ, (baseX + x) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (GetBlock(baseX + x, baseY + y, worldZ + 1) != 0)
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((15 * 16 + x) * 16) + y; // z=15
                            facePZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // ---------------------------------------------------------------------------------
            // 3. Emit instances for each face direction mask
            // ---------------------------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitMask(Span<ulong> mask, byte faceDir)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1; // clear bit
                        int li = (wi << 6) + bit;

                        int ly = _lyFromLi[li];
                        int columnIndex = li >> 4; // 0..255 (x,z combined)
                        int lx = columnIndex & 15;
                        int lz = columnIndex >> 4;
                        ushort block = dense?[li] ?? (ushort)0;
                        if (block == 0) continue; // air guard (should not trigger often)

                        EmitFaceInstance(block,
                                          faceDir,
                                          baseX + lx,
                                          baseY + ly,
                                          baseZ + lz,
                                          offsetList,
                                          tileIndexList,
                                          faceDirList);
                    }
                }
            }

            EmitMask(faceNX, 0); // LEFT
            EmitMask(facePX, 1); // RIGHT
            EmitMask(faceNY, 2); // BOTTOM
            EmitMask(facePY, 3); // TOP
            EmitMask(faceNZ, 4); // BACK
            EmitMask(facePZ, 5); // FRONT

            return true;
        }
    }
}

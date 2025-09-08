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
        // Precomputed boundary masks for 16x16x16 section (column-major layout: li = ((z*16 + x)*16)+y )
        private static readonly ulong[] _maskX0 = new ulong[64];
        private static readonly ulong[] _maskX15 = new ulong[64];
        private static readonly ulong[] _maskY0 = new ulong[64];
        private static readonly ulong[] _maskY15 = new ulong[64];
        private static readonly ulong[] _maskZ0 = new ulong[64];
        private static readonly ulong[] _maskZ15 = new ulong[64];
        private static bool _boundaryMasksInit;

        // li -> local coordinate decode tables
        private static byte[] _lxFromLi; // length 4096
        private static byte[] _lyFromLi;
        private static byte[] _lzFromLi;
        private static bool _liDecodeInit;

        // Optional prebuilt vertex patterns (currently unused in this method)
        private static byte[][] _faceVertexBytes; // index by (int)Faces
        private static bool _faceVertexInit;

        private bool EmitSinglePackedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            // Preconditions: must be single‑id packed (BitsPerIndex=1, palette[1] is block id) with occupancy.
            if (desc.OccupancyBits == null || desc.NonAirCount == 0) return false;
            if (desc.Palette == null || desc.Palette.Count < 2 || desc.BitsPerIndex != 1) return false;

            EnsureBoundaryMasks();
            EnsureLiDecode();

            var occ = desc.OccupancyBits;          // 64 ulongs (4096 bits) occupancy
            ushort block = desc.Palette[1];        // single non‑air block id

            uint tileNX = ComputeTileIndex(block, Faces.LEFT);
            uint tilePX = ComputeTileIndex(block, Faces.RIGHT);
            uint tileNY = ComputeTileIndex(block, Faces.BOTTOM);
            uint tilePY = ComputeTileIndex(block, Faces.TOP);
            uint tileNZ = ComputeTileIndex(block, Faces.BACK);
            uint tilePZ = ComputeTileIndex(block, Faces.FRONT);
            bool sameTileAllFaces = tileNX == tilePX && tileNX == tileNY && tileNX == tilePY && tileNX == tileNZ && tileNX == tilePZ;
            uint singleTileIndex = tileNX; // arbitrary representative when all same

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            // Clamp to tight bounds if present
            int lxMin = 0, lxMax = S - 1;
            int lyMin = 0, lyMax = S - 1;
            int lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            // Neighbor section descriptors (only for cross‑section boundary occlusion)
            int sxCount = data.sectionsX; int syCount = data.sectionsY; int szCount = data.sectionsZ;
            SectionPrerenderDesc[] allSecs = data.SectionDescs;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int SecIndex(int sxL, int syL, int szL, int syC, int szC) => ((sxL * syC) + syL) * szC + szL;

            bool hasLeft = sx > 0;                 ref SectionPrerenderDesc leftSec = ref hasLeft ? ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasRight = sx + 1 < sxCount;      ref SectionPrerenderDesc rightSec = ref hasRight ? ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasDown = sy > 0;                 ref SectionPrerenderDesc downSec = ref hasDown ? ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)] : ref desc;
            bool hasUp = sy + 1 < syCount;         ref SectionPrerenderDesc upSec = ref hasUp ? ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)] : ref desc;
            bool hasBack = sz > 0;                 ref SectionPrerenderDesc backSec = ref hasBack ? ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)] : ref desc;
            bool hasFront = sz + 1 < szCount;      ref SectionPrerenderDesc frontSec = ref hasFront ? ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)] : ref desc;

            // Precomputed boundary face occupancy (256 bits each) created during finalization for Packed.
            ulong[] faceNegX = desc.FaceNegXBits;
            ulong[] facePosX = desc.FacePosXBits;
            ulong[] faceNegY = desc.FaceNegYBits;
            ulong[] facePosY = desc.FacePosYBits;
            ulong[] faceNegZ = desc.FaceNegZBits;
            ulong[] facePosZ = desc.FacePosZBits;

            // Face bitsets (internal faces + later added visible boundary faces)
            Span<ulong> shift = stackalloc ulong[64];
            Span<ulong> faceNX = stackalloc ulong[64]; // -X
            Span<ulong> facePX = stackalloc ulong[64]; // +X
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y
            Span<ulong> facePY = stackalloc ulong[64]; // +Y
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z

            const int strideX = 16;   // linear index delta for +X (skip one 16‑voxel Y column)
            const int strideY = 1;    // +Y delta
            const int strideZ = 256;  // +Z delta (16 * 16)

            // --------------------------------------------------
            // 1. Internal faces (exclude boundary layers first)
            // --------------------------------------------------
            // -X internal faces
            BitsetShiftLeft(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskX0[i];
                faceNX[i] = candidates & ~shift[i];
            }
            // +X internal faces
            BitsetShiftRight(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskX15[i];
                facePX[i] = candidates & ~shift[i];
            }
            // -Y internal faces
            BitsetShiftLeft(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskY0[i];
                faceNY[i] = candidates & ~shift[i];
            }
            // +Y internal faces
            BitsetShiftRight(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskY15[i];
                facePY[i] = candidates & ~shift[i];
            }
            // -Z internal faces
            BitsetShiftLeft(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskZ0[i];
                faceNZ[i] = candidates & ~shift[i];
            }
            // +Z internal faces
            BitsetShiftRight(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskZ15[i];
                facePZ[i] = candidates & ~shift[i];
            }

            // --------------------------------------------------
            // 2. Boundary faces: add only visible boundary voxels
            // --------------------------------------------------
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // LEFT boundary (x=0)
            if (lxMin == 0 && faceNegX != null)
            {
                int wx = baseX;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = z * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((faceNegX[w] & (1UL << b)) == 0) continue; // no voxel
                        bool hidden = false;
                        if (wx == 0)
                        {
                            if (PlaneBit(planeNegX, (baseZ + z) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (hasLeft && NeighborBoundarySolid(ref leftSec, 0, 15, y, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16) + 0) * 16 + y; // voxel linear index at x=0
                            faceNX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // RIGHT boundary (x=15)
            if (lxMax == S - 1 && facePosX != null)
            {
                int wxRight = baseX + 15;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = z * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((facePosX[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wxRight == maxX - 1)
                        {
                            if (PlaneBit(planePosX, (baseZ + z) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (hasRight && NeighborBoundarySolid(ref rightSec, 1, 0, y, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16) + 15) * 16 + y; // x=15
                            facePX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // BOTTOM boundary (y=0)
            if (lyMin == 0 && faceNegY != null)
            {
                int wy = baseY;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int idx = x * 16 + z; int w = idx >> 6; int b = idx & 63;
                        if ((faceNegY[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wy == 0)
                        {
                            if (PlaneBit(planeNegY, (baseX + x) * maxZ + (baseZ + z))) hidden = true;
                        }
                        else if (hasDown && NeighborBoundarySolid(ref downSec, 2, x, 15, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 0; // y=0
                            faceNY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // TOP boundary (y=15)
            if (lyMax == S - 1 && facePosY != null)
            {
                int wyTop = baseY + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int idx = x * 16 + z; int w = idx >> 6; int b = idx & 63;
                        if ((facePosY[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wyTop == maxY - 1)
                        {
                            if (PlaneBit(planePosY, (baseX + x) * maxZ + (baseZ + z))) hidden = true;
                        }
                        else if (hasUp && NeighborBoundarySolid(ref upSec, 3, x, 0, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 15; // y=15
                            facePY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // BACK boundary (z=0)
            if (lzMin == 0 && faceNegZ != null)
            {
                int wz = baseZ;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = x * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((faceNegZ[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wz == 0)
                        {
                            if (PlaneBit(planeNegZ, (baseX + x) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (hasBack && NeighborBoundarySolid(ref backSec, 4, x, y, 15)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((0 * 16 + x) * 16) + y; // z=0
                            faceNZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // FRONT boundary (z=15)
            if (lzMax == S - 1 && facePosZ != null)
            {
                int wzFront = baseZ + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = x * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((facePosZ[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wzFront == maxZ - 1)
                        {
                            if (PlaneBit(planePosZ, (baseX + x) * maxY + (baseY + y))) hidden = true;
                        }
                        else if (hasFront && NeighborBoundarySolid(ref frontSec, 5, x, y, 0)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((15 * 16 + x) * 16) + y; // z=15
                            facePZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // --------------------------------------------------
            // 3. Emit faces from masks
            // --------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitMaskSingleTile(Span<ulong> mask, byte faceDir)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int ly = _lyFromLi[li];
                        int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                        offsetList.Add((byte)(baseX + lx));
                        offsetList.Add((byte)(baseY + ly));
                        offsetList.Add((byte)(baseZ + lz));
                        tileIndexList.Add(singleTileIndex);
                        faceDirList.Add(faceDir);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitMaskPerFace(Span<ulong> mask, byte faceDir, uint tileIndexForFace)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int ly = _lyFromLi[li];
                        int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                        offsetList.Add((byte)(baseX + lx));
                        offsetList.Add((byte)(baseY + ly));
                        offsetList.Add((byte)(baseZ + lz));
                        tileIndexList.Add(tileIndexForFace);
                        faceDirList.Add(faceDir);
                    }
                }
            }

            if (sameTileAllFaces)
            {
                EmitMaskSingleTile(faceNX, 0);
                EmitMaskSingleTile(facePX, 1);
                EmitMaskSingleTile(faceNY, 2);
                EmitMaskSingleTile(facePY, 3);
                EmitMaskSingleTile(faceNZ, 4);
                EmitMaskSingleTile(facePZ, 5);
            }
            else
            {
                EmitMaskPerFace(faceNX, 0, tileNX);
                EmitMaskPerFace(facePX, 1, tilePX);
                EmitMaskPerFace(faceNY, 2, tileNY);
                EmitMaskPerFace(facePY, 3, tilePY);
                EmitMaskPerFace(faceNZ, 4, tileNZ);
                EmitMaskPerFace(facePZ, 5, tilePZ);
            }

            return true;
        }
    }
}

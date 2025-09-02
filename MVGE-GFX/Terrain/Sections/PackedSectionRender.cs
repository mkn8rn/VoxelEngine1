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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureBoundaryMasks()
        {
            if (_boundaryMasksInit) return;

            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int li = ((z * 16 + x) * 16) + y;  // linear index
                        int w = li >> 6;                  // word index (0..63)
                        int b = li & 63;                  // bit index inside word
                        ulong bit = 1UL << b;

                        if (x == 0) _maskX0[w] |= bit; else if (x == 15) _maskX15[w] |= bit;
                        if (y == 0) _maskY0[w] |= bit; else if (y == 15) _maskY15[w] |= bit;
                        if (z == 0) _maskZ0[w] |= bit; else if (z == 15) _maskZ15[w] |= bit;
                    }
                }
            }
            _boundaryMasksInit = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureLiDecode()
        {
            if (_liDecodeInit) return;

            _lxFromLi = new byte[4096];
            _lyFromLi = new byte[4096];
            _lzFromLi = new byte[4096];

            for (int li = 0; li < 4096; li++)
            {
                int ly = li & 15;
                int t = li >> 4;
                int lx = t & 15;
                int lz = t >> 4;
                _lxFromLi[li] = (byte)lx;
                _lyFromLi[li] = (byte)ly;
                _lzFromLi[li] = (byte)lz;
            }
            _liDecodeInit = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureFaceVertexBytes()
        {
            if (_faceVertexInit) return;

            _faceVertexBytes = new byte[6][];
            for (int f = 0; f < 6; f++)
            {
                var face = (Faces)f;
                var vtx = RawFaceData.rawVertexData[face];
                var arr = new byte[12];
                for (int i = 0; i < 4; i++)
                {
                    int o = i * 3;
                    arr[o + 0] = (byte)vtx[i].x;
                    arr[o + 1] = (byte)vtx[i].y;
                    arr[o + 2] = (byte)vtx[i].z;
                }
                _faceVertexBytes[f] = arr;
            }
            _faceVertexInit = true;
        }

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

            // Precompute tileIndex (same for all faces of this block).
            var uvFaceSample = atlas.GetBlockUVs(block, Faces.LEFT);
            byte minTileX = 255, minTileY = 255;
            for (int i = 0; i < 4; i++)
            {
                if (uvFaceSample[i].x < minTileX) minTileX = uvFaceSample[i].x;
                if (uvFaceSample[i].y < minTileY) minTileY = uvFaceSample[i].y;
            }
            uint tileIndex = (uint)(minTileY * atlas.tilesX + minTileX);

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

            // Bitset shifting helpers (same as DenseExpanded path)
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
                            ulong carry = (si + 1 < 64) ? src[si + 1] : 0;
                            v = (v >> bitShift) | (carry << (64 - bitShift));
                        }
                    }
                    dst[i] = v;
                }
            }

            const int strideX = 16;   // linear index delta for +X (skip one 16‑voxel Y column)
            const int strideY = 1;    // +Y delta
            const int strideZ = 256;  // +Z delta (16 * 16)

            // --------------------------------------------------
            // 1. Internal faces (exclude boundary layers first)
            // --------------------------------------------------
            // -X internal faces
            ShiftLeft(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskX0[i];
                faceNX[i] = candidates & ~shift[i];
            }
            // +X internal faces
            ShiftRight(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskX15[i];
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

            // --------------------------------------------------
            // 2. Boundary faces: add only visible boundary voxels
            // --------------------------------------------------
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6; int b = index & 63; return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
            }

            // Fallback neighbor voxel test when masks missing. MultiPacked (Kind==5) handled identically to Packed (Kind==4).
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborVoxelSolidFallback(ref SectionPrerenderDesc n, int lx, int ly, int lz)
            {
                if (n.Kind == 0 || n.NonAirCount == 0) return false;
                switch (n.Kind)
                {
                    case 1: return n.UniformBlockId != 0; // Uniform
                    case 2: // Sparse
                        if (n.SparseIndices != null)
                        {
                            int li = ((lz * 16 + lx) * 16) + ly;
                            var arr = n.SparseIndices;
                            for (int i = 0; i < arr.Length; i++) if (arr[i] == li) return true;
                        }
                        return false;
                    case 3: // DenseExpanded
                        if (n.ExpandedDense != null)
                        {
                            int liD = ((lz * 16 + lx) * 16) + ly;
                            return n.ExpandedDense[liD] != 0;
                        }
                        return false;
                    case 4: // Packed single-id or multi-id fallback occupancy check
                    case 5: // MultiPacked
                        if (n.OccupancyBits != null)
                        {
                            int li = ((lz * 16 + lx) * 16) + ly;
                            return (n.OccupancyBits[li >> 6] & (1UL << (li & 63))) != 0UL;
                        }
                        return false;
                    default: return false;
                }
            }

            // Neighbor boundary probe using its precomputed face bitsets (with fallback)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborBoundarySolid(ref SectionPrerenderDesc n, int faceDir, int x, int y, int z)
            {
                ulong[] mask = null; int localIndex = 0; int lx = x, ly = y, lz = z;
                switch (faceDir)
                {
                    case 0: mask = n.FacePosXBits; localIndex = z * 16 + y; lx = 15; break; // neighbor +X face
                    case 1: mask = n.FaceNegXBits; localIndex = z * 16 + y; lx = 0; break;  // neighbor -X face
                    case 2: mask = n.FacePosYBits; localIndex = x * 16 + z; ly = 15; break; // neighbor +Y
                    case 3: mask = n.FaceNegYBits; localIndex = x * 16 + z; ly = 0; break;  // neighbor -Y
                    case 4: mask = n.FacePosZBits; localIndex = x * 16 + y; lz = 15; break; // neighbor +Z
                    case 5: mask = n.FaceNegZBits; localIndex = x * 16 + y; lz = 0; break;  // neighbor -Z
                }
                if (mask != null)
                {
                    int w = localIndex >> 6; int b = localIndex & 63; if ((mask[w] & (1UL << b)) != 0UL) return true;
                }
                // fallback per-voxel check if mask missing or bit not set
                return NeighborVoxelSolidFallback(ref n, lx, ly, lz);
            }

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
                        int ly = _lyFromLi[li];
                        int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                        offsetList.Add((byte)(baseX + lx));
                        offsetList.Add((byte)(baseY + ly));
                        offsetList.Add((byte)(baseZ + lz));
                        tileIndexList.Add(tileIndex);
                        faceDirList.Add(faceDir);
                    }
                }
            }

            EmitMask(faceNX, 0);
            EmitMask(facePX, 1);
            EmitMask(faceNY, 2);
            EmitMask(facePY, 3);
            EmitMask(faceNZ, 4);
            EmitMask(facePZ, 5);

            return true;
        }
    }
}

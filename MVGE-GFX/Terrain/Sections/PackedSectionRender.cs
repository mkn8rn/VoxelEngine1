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

        // li -> local coordinate decode tables (optimization 13)
        private static byte[] _lxFromLi; // length 4096
        private static byte[] _lyFromLi;
        private static byte[] _lzFromLi;
        private static bool _liDecodeInit;

        // Prebuilt relative vertex byte patterns per face: 4 verts * (x,y,z) = 12 bytes (optimization 9)
        // Generated lazily from RawFaceData the first time needed.
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
                        int w = li >> 6;                  // word index
                        int b = li & 63;                  // bit index within word
                        ulong bit = 1UL << b;

                        if (x == 0)
                            _maskX0[w] |= bit;
                        else if (x == 15)
                            _maskX15[w] |= bit;

                        if (y == 0)
                            _maskY0[w] |= bit;
                        else if (y == 15)
                            _maskY15[w] |= bit;

                        if (z == 0)
                            _maskZ0[w] |= bit;
                        else if (z == 15)
                            _maskZ15[w] |= bit;
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

        private bool EmitPackedSectionInstances(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
        List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            // Preconditions: single-id packed (always true for our Packed), occupancy present, some non-air.
            if (desc.OccupancyBits == null || desc.NonAirCount == 0) return false;
            if (desc.Palette == null || desc.Palette.Count < 2 || desc.BitsPerIndex != 1) return false;

            EnsureBoundaryMasks();
            EnsureLiDecode();

            var occ = desc.OccupancyBits; // ulong[64] occupancy bitset
            ushort block = desc.Palette[1]; // constant block id (non-air)

            // Precompute tileIndex once (instead of per-face EmitFaceInstance path).
            // Equivalent logic to EmitFaceInstance choosing min UV tile corner.
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

            // Bounds clamp (still honor tight bounding box)
            int lxMin = 0, lxMax = S - 1;
            int lyMin = 0, lyMax = S - 1;
            int lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            // Allocate face masks on stack (same sequence as DenseExpanded path)
            Span<ulong> shift = stackalloc ulong[64];
            Span<ulong> faceNX = stackalloc ulong[64];
            Span<ulong> facePX = stackalloc ulong[64];
            Span<ulong> faceNY = stackalloc ulong[64];
            Span<ulong> facePY = stackalloc ulong[64];
            Span<ulong> faceNZ = stackalloc ulong[64];
            Span<ulong> facePZ = stackalloc ulong[64];

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

            const int strideX = 16;
            const int strideY = 1;
            const int strideZ = 256;

            // Internal faces (-X, exclude boundary X0)
            ShiftLeft(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX0[i];
                faceNX[i] = cand & ~shift[i];
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

            // Boundary integration (only outward faces on boundary voxels)
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6;
                int b = index & 63;
                return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
            }

            // LEFT boundary (x=0)
            if (lxMin == 0)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskX0[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int ly = _lyFromLi[li];
                        int lz = _lzFromLi[li];
                        if (ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;

                        bool hidden = false;
                        int wx = baseX;
                        int wy = baseY + ly;
                        int wz = baseZ + lz;
                        if (wx == 0)
                        {
                            if (PlaneBit(planeNegX, wz * maxY + wy)) hidden = true;
                        }
                        else
                        {
                            ushort nb = GetBlock(wx - 1, wy, wz);
                            if (nb != 0) hidden = true;
                        }
                        if (!hidden) faceNX[wi] |= 1UL << bit;
                    }
                }
            }
            // RIGHT boundary (x=15)
            if (lxMax == S - 1)
            {
                int wxRight = baseX + (S - 1);
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskX15[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int ly = _lyFromLi[li];
                        int lz = _lzFromLi[li];
                        if (ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;

                        bool hidden = false;
                        int wy = baseY + ly;
                        int wz = baseZ + lz;
                        if (wxRight == maxX - 1)
                        {
                            if (PlaneBit(planePosX, wz * maxY + wy)) hidden = true;
                        }
                        else
                        {
                            ushort nb = GetBlock(wxRight + 1, wy, wz);
                            if (nb != 0) hidden = true;
                        }
                        if (!hidden) facePX[wi] |= 1UL << bit;
                    }
                }
            }
            // BOTTOM (y=0)
            if (lyMin == 0)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskY0[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int lx = _lxFromLi[li];
                        int lz = _lzFromLi[li];
                        if (lx < lxMin || lx > lxMax || lz < lzMin || lz > lzMax) continue;

                        bool hidden = false;
                        int wx = baseX + lx;
                        int wz = baseZ + lz;
                        if (baseY == 0)
                        {
                            if (PlaneBit(planeNegY, wx * maxZ + wz)) hidden = true;
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, baseY - 1, wz);
                            if (nb != 0) hidden = true;
                        }
                        if (!hidden) faceNY[wi] |= 1UL << bit;
                    }
                }
            }
            // TOP (y=15)
            if (lyMax == S - 1)
            {
                int wyTop = baseY + (S - 1);
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskY15[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int lx = _lxFromLi[li];
                        int lz = _lzFromLi[li];
                        if (lx < lxMin || lx > lxMax || lz < lzMin || lz > lzMax) continue;

                        bool hidden = false;
                        int wx = baseX + lx;
                        int wz = baseZ + lz;
                        if (wyTop == maxY - 1)
                        {
                            if (PlaneBit(planePosY, wx * maxZ + wz)) hidden = true;
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, wyTop + 1, wz);
                            if (nb != 0) hidden = true;
                        }
                        if (!hidden) facePY[wi] |= 1UL << bit;
                    }
                }
            }
            // BACK (z=0)
            if (lzMin == 0)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskZ0[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int lx = _lxFromLi[li];
                        int ly = _lyFromLi[li];
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax) continue;

                        bool hidden = false;
                        int wx = baseX + lx;
                        int wy = baseY + ly;
                        if (baseZ == 0)
                        {
                            if (PlaneBit(planeNegZ, wx * maxY + wy)) hidden = true;
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, wy, baseZ - 1);
                            if (nb != 0) hidden = true;
                        }
                        if (!hidden) faceNZ[wi] |= 1UL << bit;
                    }
                }
            }
            // FRONT (z=15)
            if (lzMax == S - 1)
            {
                int wzFront = baseZ + (S - 1);
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskZ15[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (wi << 6) + bit;
                        int lx = _lxFromLi[li];
                        int ly = _lyFromLi[li];
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax) continue;

                        bool hidden = false;
                        int wx = baseX + lx;
                        int wy = baseY + ly;
                        if (wzFront == maxZ - 1)
                        {
                            if (PlaneBit(planePosZ, wx * maxY + wy)) hidden = true;
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, wy, wzFront + 1);
                            if (nb != 0) hidden = true;
                        }
                        if (!hidden) facePZ[wi] |= 1UL << bit;
                    }
                }
            }

            // Emit helper (avoids atlas & block lookups per face).
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

                        int lx = _lxFromLi[li];
                        int ly = _lyFromLi[li];
                        int lz = _lzFromLi[li];
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                            continue;

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

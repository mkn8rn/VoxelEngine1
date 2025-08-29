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
        private void EmitDenseExpandedSection(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            if (desc.ExpandedDense == null || desc.NonAirCount == 0)
                return;

            int S = data.sectionSize;                   // Section dimension (expected 16)
            var dense = desc.ExpandedDense;             // Voxel IDs (ushort[4096])

            // World base coordinates for this section
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

            // Chunk dimensions (world space bounds)
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            // Neighbor chunk boundary plane bitsets (used when at absolute chunk edges)
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // Optional tighter bounds within the section
            int lxMin = 0, lxMax = S - 1;
            int lyMin = 0, lyMax = S - 1;
            int lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            // Ensure static boundary bit masks (x==0, x==15, etc.) are available
            EnsureBoundaryMasks();

            // ----------------------------------------------------------------------------------
            // 1. Build NonAir and Opaque bitsets (4096 bits => 64 ulongs)
            // ----------------------------------------------------------------------------------
            Span<ulong> nonAirBits = stackalloc ulong[64];
            Span<ulong> opaqueBits = stackalloc ulong[64];
            for (int li = 0; li < 4096; li++)
            {
                ushort id = dense[li];
                if (id == 0)
                    continue; // air
                int w = li >> 6;
                int b = li & 63;
                ulong bit = 1UL << b;
                nonAirBits[w] |= bit;
                if (BlockProperties.IsOpaque(id))
                    opaqueBits[w] |= bit;
            }

            // Linear layout strides for li = ((z * S + x) * S) + y
            const int strideY = 1;          // +Y (next voxel in word when within a 16-y segment)
            const int strideX = 16;         // +X (skip 16 y-values)
            const int strideZ = 256;        // +Z (skip 16*16 y-values)

            // Temporary shift buffers and output face masks
            Span<ulong> shiftA = stackalloc ulong[64];
            Span<ulong> shiftB = stackalloc ulong[64];
            Span<ulong> faceNX = stackalloc ulong[64]; // -X faces
            Span<ulong> facePX = stackalloc ulong[64]; // +X faces
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y faces
            Span<ulong> facePY = stackalloc ulong[64]; // +Y faces
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z faces
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z faces

            // Shift helpers (bitwise address translation across linear voxel ordering)
            static void ShiftLeft(ReadOnlySpan<ulong> src, int shiftBits, Span<ulong> dst)
            {
                if (shiftBits == 0)
                {
                    src.CopyTo(dst);
                    return;
                }
                int wordShift = shiftBits >> 6;
                int bitShift = shiftBits & 63;
                for (int i = 63; i >= 0; i--)
                {
                    ulong val = 0;
                    int si = i - wordShift;
                    if (si >= 0)
                    {
                        val = src[si];
                        if (bitShift != 0)
                        {
                            if (si - 1 >= 0)
                                val = (val << bitShift) | (src[si - 1] >> (64 - bitShift));
                            else
                                val <<= bitShift;
                        }
                    }
                    dst[i] = val;
                }
            }
            static void ShiftRight(ReadOnlySpan<ulong> src, int shiftBits, Span<ulong> dst)
            {
                if (shiftBits == 0)
                {
                    src.CopyTo(dst);
                    return;
                }
                int wordShift = shiftBits >> 6;
                int bitShift = shiftBits & 63;
                for (int i = 0; i < 64; i++)
                {
                    ulong val = 0;
                    int si = i + wordShift;
                    if (si < 64)
                    {
                        val = src[si];
                        if (bitShift != 0)
                        {
                            if (si + 1 < 64)
                                val = (val >> bitShift) | (src[si + 1] << (64 - bitShift));
                            else
                                val >>= bitShift;
                        }
                    }
                    dst[i] = val;
                }
            }

            // ----------------------------------------------------------------------------------
            // 2. Build internal face masks: visible = nonAir & ! (selfOpaque & neighborOpaque & neighborPresent)
            //    Boundary-layer bits are masked out here; boundaries are processed separately later.
            // ----------------------------------------------------------------------------------
            // -X faces (compare with neighbor at -X -> shift right by strideX)
            ShiftRight(nonAirBits, strideX, shiftA);
            ShiftRight(opaqueBits, strideX, shiftB);
            for (int i = 0; i < 64; i++)
            {
                ulong selfV = nonAirBits[i] & ~_maskX0[i];
                ulong hidden = selfV & shiftA[i] & opaqueBits[i] & shiftB[i];
                faceNX[i] = selfV & ~hidden;
            }
            // +X faces
            ShiftLeft(nonAirBits, strideX, shiftA);
            ShiftLeft(opaqueBits, strideX, shiftB);
            for (int i = 0; i < 64; i++)
            {
                ulong selfV = nonAirBits[i] & ~_maskX15[i];
                ulong hidden = selfV & shiftA[i] & opaqueBits[i] & shiftB[i];
                facePX[i] = selfV & ~hidden;
            }
            // -Y faces
            ShiftRight(nonAirBits, strideY, shiftA);
            ShiftRight(opaqueBits, strideY, shiftB);
            for (int i = 0; i < 64; i++)
            {
                ulong selfV = nonAirBits[i] & ~_maskY0[i];
                ulong hidden = selfV & shiftA[i] & opaqueBits[i] & shiftB[i];
                faceNY[i] = selfV & ~hidden;
            }
            // +Y faces
            ShiftLeft(nonAirBits, strideY, shiftA);
            ShiftLeft(opaqueBits, strideY, shiftB);
            for (int i = 0; i < 64; i++)
            {
                ulong selfV = nonAirBits[i] & ~_maskY15[i];
                ulong hidden = selfV & shiftA[i] & opaqueBits[i] & shiftB[i];
                facePY[i] = selfV & ~hidden;
            }
            // -Z faces
            ShiftRight(nonAirBits, strideZ, shiftA);
            ShiftRight(opaqueBits, strideZ, shiftB);
            for (int i = 0; i < 64; i++)
            {
                ulong selfV = nonAirBits[i] & ~_maskZ0[i];
                ulong hidden = selfV & shiftA[i] & opaqueBits[i] & shiftB[i];
                faceNZ[i] = selfV & ~hidden;
            }
            // +Z faces
            ShiftLeft(nonAirBits, strideZ, shiftA);
            ShiftLeft(opaqueBits, strideZ, shiftB);
            for (int i = 0; i < 64; i++)
            {
                ulong selfV = nonAirBits[i] & ~_maskZ15[i];
                ulong hidden = selfV & shiftA[i] & opaqueBits[i] & shiftB[i];
                facePZ[i] = selfV & ~hidden;
            }

            // ----------------------------------------------------------------------------------
            // 3. Emit internal faces from masks
            // ----------------------------------------------------------------------------------
            var uvCache = new Dictionary<ushort, List<ByteVector2>[]?>();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            List<ByteVector2> GetUV(ushort block, Faces face)
            {
                if (!uvCache.TryGetValue(block, out var arr) || arr == null)
                {
                    arr = new List<ByteVector2>[6];
                    uvCache[block] = arr;
                }
                int fi = (int)face;
                return arr[fi] ??= atlas.GetBlockUVs(block, face);
            }

            uint vb = vertBase;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitFace(Faces face, int wx, int wy, int wz, ushort block)
            {
                var vtx = RawFaceData.rawVertexData[face];
                for (int i = 0; i < 4; i++)
                {
                    vertList.Add((byte)(vtx[i].x + wx));
                    vertList.Add((byte)(vtx[i].y + wy));
                    vertList.Add((byte)(vtx[i].z + wz));
                }
                var uvFace = GetUV(block, face);
                for (int i = 0; i < 4; i++)
                {
                    uvList.Add(uvFace[i].x);
                    uvList.Add(uvFace[i].y);
                }
                idxList.Add(vb + 0); idxList.Add(vb + 1); idxList.Add(vb + 2);
                idxList.Add(vb + 2); idxList.Add(vb + 3); idxList.Add(vb + 0);
                vb += 4;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool InBounds(int lx, int ly, int lz)
                => lx >= lxMin && lx <= lxMax && ly >= lyMin && ly <= lyMax && lz >= lzMin && lz <= lzMax;

            void EmitInternal(Span<ulong> mask, Faces face)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;
                        int ly = li & 15;
                        int t = li >> 4;
                        int lx = t & 15;
                        int lz = t >> 4;
                        if (!InBounds(lx, ly, lz))
                            continue;
                        EmitFace(face, baseX + lx, baseY + ly, baseZ + lz, dense[li]);
                    }
                }
            }

            EmitInternal(faceNX, Faces.LEFT);
            EmitInternal(facePX, Faces.RIGHT);
            EmitInternal(faceNY, Faces.BOTTOM);
            EmitInternal(facePY, Faces.TOP);
            EmitInternal(faceNZ, Faces.BACK);
            EmitInternal(facePZ, Faces.FRONT);

            // ----------------------------------------------------------------------------------
            // 4. Emit boundary faces (section edges) with neighbor / chunk boundary tests
            // ----------------------------------------------------------------------------------
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6; int b = index & 63;
                return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
            }

            // Copy to arrays to avoid ref/stack-span capture issues in subsequent loops
            ulong[] nonAirArr = nonAirBits.ToArray();
            ulong[] opaqueArr = opaqueBits.ToArray();

            void EmitBoundaryFace(ulong[] boundaryMask, Faces face)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = boundaryMask[wi] & nonAirArr[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;
                        int ly = li & 15;
                        int t = li >> 4;
                        int lx = t & 15;
                        int lz = t >> 4;
                        if (!InBounds(lx, ly, lz)) continue;

                        ushort block = dense[li];
                        bool selfOpaque = (opaqueArr[wi] & (1UL << bit)) != 0UL;
                        bool hide = false;

                        int wx = baseX + lx;
                        int wy = baseY + ly;
                        int wz = baseZ + lz;

                        switch (face)
                        {
                            case Faces.LEFT:
                                hide = lx > 0
                                    ? selfOpaque && ((opaqueArr[(li - strideX) >> 6] & (1UL << ((li - strideX) & 63))) != 0UL)
                                    : (wx == 0
                                        ? (selfOpaque && PlaneBit(planeNegX, wz * maxY + wy))
                                        : selfOpaque && BlockProperties.IsOpaque(GetBlock(wx - 1, wy, wz)));
                                break;
                            case Faces.RIGHT:
                                hide = lx < S - 1
                                    ? selfOpaque && ((opaqueArr[(li + strideX) >> 6] & (1UL << ((li + strideX) & 63))) != 0UL)
                                    : (wx == maxX - 1
                                        ? (selfOpaque && PlaneBit(planePosX, wz * maxY + wy))
                                        : selfOpaque && BlockProperties.IsOpaque(GetBlock(wx + 1, wy, wz)));
                                break;
                            case Faces.BOTTOM:
                                hide = ly > 0
                                    ? selfOpaque && ((opaqueArr[(li - strideY) >> 6] & (1UL << ((li - strideY) & 63))) != 0UL)
                                    : (wy == 0
                                        ? (selfOpaque && PlaneBit(planeNegY, wx * maxZ + wz))
                                        : selfOpaque && BlockProperties.IsOpaque(GetBlock(wx, wy - 1, wz)));
                                break;
                            case Faces.TOP:
                                hide = ly < S - 1
                                    ? selfOpaque && ((opaqueArr[(li + strideY) >> 6] & (1UL << ((li + strideY) & 63))) != 0UL)
                                    : (wy == maxY - 1
                                        ? (selfOpaque && PlaneBit(planePosY, wx * maxZ + wz))
                                        : selfOpaque && BlockProperties.IsOpaque(GetBlock(wx, wy + 1, wz)));
                                break;
                            case Faces.BACK:
                                hide = lz > 0
                                    ? selfOpaque && ((opaqueArr[(li - strideZ) >> 6] & (1UL << ((li - strideZ) & 63))) != 0UL)
                                    : (wz == 0
                                        ? (selfOpaque && PlaneBit(planeNegZ, wx * maxY + wy))
                                        : selfOpaque && BlockProperties.IsOpaque(GetBlock(wx, wy, wz - 1)));
                                break;
                            case Faces.FRONT:
                                hide = lz < S - 1
                                    ? selfOpaque && ((opaqueArr[(li + strideZ) >> 6] & (1UL << ((li + strideZ) & 63))) != 0UL)
                                    : (wz == maxZ - 1
                                        ? (selfOpaque && PlaneBit(planePosZ, wx * maxY + wy))
                                        : selfOpaque && BlockProperties.IsOpaque(GetBlock(wx, wy, wz + 1)));
                                break;
                        }

                        if (!hide)
                            EmitFace(face, wx, wy, wz, block);
                    }
                }
            }

            EmitBoundaryFace(_maskX0, Faces.LEFT);
            EmitBoundaryFace(_maskX15, Faces.RIGHT);
            EmitBoundaryFace(_maskY0, Faces.BOTTOM);
            EmitBoundaryFace(_maskY15, Faces.TOP);
            EmitBoundaryFace(_maskZ0, Faces.BACK);
            EmitBoundaryFace(_maskZ15, Faces.FRONT);

            // Write back updated vertex base
            vertBase = vb;
        }
    }
}

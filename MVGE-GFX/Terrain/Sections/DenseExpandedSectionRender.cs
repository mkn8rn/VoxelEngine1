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
                if (shiftBits == 0) { src.CopyTo(dst); return; }
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
                if (shiftBits == 0) { src.CopyTo(dst); return; }
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
            // 2. Build internal face masks (excludes boundary layer bits)
            // ----------------------------------------------------------------------------------
            // -X faces
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
            // 3. Integrate boundary faces directly into masks (unified mask generation)
            // ----------------------------------------------------------------------------------
            // Helper to test chunk plane bit (treat plane occupancy as opaque for occlusion)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6; int b = index & 63;
                return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
            }

            // Generic block accessor for a section descriptor (local coords)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort GetLocalBlock(ref SectionPrerenderDesc d, int lx, int ly, int lz)
            {
                switch (d.Kind)
                {
                    case 0: return 0;
                    case 1: return d.UniformBlockId;
                    case 2:
                        if (d.SparseIndices == null || d.SparseBlocks == null) return 0;
                        // linear index ((z * 16 + x) * 16) + y
                        int li = ((lz * 16 + lx) * 16) + ly;
                        var idxs = d.SparseIndices; var blks = d.SparseBlocks;
                        for (int i = 0; i < idxs.Length; i++) if (idxs[i] == li) return blks[i];
                        return 0;
                    case 3:
                        return d.ExpandedDense == null ? (ushort)0 : d.ExpandedDense[((lz * 16 + lx) * 16) + ly];
                    case 4: // Packed
                        if (d.PackedBitData == null || d.Palette == null || d.BitsPerIndex <= 0) return 0;
                        {
                            int li2 = ((lz * 16 + lx) * 16) + ly;
                            long bitPos = (long)li2 * d.BitsPerIndex;
                            int word = (int)(bitPos >> 5);
                            int bitOffset = (int)(bitPos & 31);
                            uint value = d.PackedBitData[word] >> bitOffset;
                            int rem = 32 - bitOffset;
                            if (rem < d.BitsPerIndex)
                                value |= d.PackedBitData[word + 1] << rem;
                            int mask = (1 << d.BitsPerIndex) - 1;
                            int pi = (int)(value & mask);
                            if (pi < 0 || pi >= d.Palette.Count) return 0;
                            return d.Palette[pi];
                        }
                    default: return 0;
                }
            }

            SectionPrerenderDesc[] allSecs = data.SectionDescs;
            int sxCount = data.sectionsX, syCount = data.sectionsY, szCount = data.sectionsZ;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int SecIndex(int sxL, int syL, int szL, int syC, int szC) => ((sxL * syC) + syL) * szC + szL;

            // Boundary processing function: iterates bits of a boundary mask, determines visibility and sets face bit in provided target mask span.
            void ProcessBoundary(ReadOnlySpan<ulong> boundaryMask, Span<ulong> targetMask, Faces face, ReadOnlySpan<ulong> nonAirBitsLocal, ReadOnlySpan<ulong> opaqueBitsLocal)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = boundaryMask[wi] & nonAirBitsLocal[wi];
                    if (word == 0) continue;
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;
                        int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;

                        bool selfOpaque = (opaqueBitsLocal[wi] & (1UL << bit)) != 0UL;
                        bool hide = false;
                        int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                        switch (face)
                        {
                            case Faces.LEFT:
                                if (lx == 0)
                                {
                                    if (wx == 0) { int planeIdx = wz * maxY + wy; bool covered = PlaneBit(planeNegX, planeIdx); hide = covered && selfOpaque; }
                                    else if (sx > 0) { ref var nd = ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)]; if (nd.NonAirCount > 0) { ushort nb = GetLocalBlock(ref nd, 15, ly, lz); if (nb != 0 && selfOpaque && BlockProperties.IsOpaque(nb)) hide = true; } }
                                }
                                break;
                            case Faces.RIGHT:
                                if (lx == S - 1)
                                {
                                    if (wx == maxX - 1) { int planeIdx = wz * maxY + wy; bool covered = PlaneBit(planePosX, planeIdx); hide = covered && selfOpaque; }
                                    else if (sx + 1 < sxCount) { ref var nd = ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)]; if (nd.NonAirCount > 0) { ushort nb = GetLocalBlock(ref nd, 0, ly, lz); if (nb != 0 && selfOpaque && BlockProperties.IsOpaque(nb)) hide = true; } }
                                }
                                break;
                            case Faces.BOTTOM:
                                if (ly == 0)
                                {
                                    if (wy == 0) { int planeIdx = wx * maxZ + wz; bool covered = PlaneBit(planeNegY, planeIdx); hide = covered && selfOpaque; }
                                    else if (sy > 0) { ref var nd = ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)]; if (nd.NonAirCount > 0) { ushort nb = GetLocalBlock(ref nd, lx, 15, lz); if (nb != 0 && selfOpaque && BlockProperties.IsOpaque(nb)) hide = true; } }
                                }
                                break;
                            case Faces.TOP:
                                if (ly == S - 1)
                                {
                                    if (wy == maxY - 1) { int planeIdx = wx * maxZ + wz; bool covered = PlaneBit(planePosY, planeIdx); hide = covered && selfOpaque; }
                                    else if (sy + 1 < syCount) { ref var nd = ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)]; if (nd.NonAirCount > 0) { ushort nb = GetLocalBlock(ref nd, lx, 0, lz); if (nb != 0 && selfOpaque && BlockProperties.IsOpaque(nb)) hide = true; } }
                                }
                                break;
                            case Faces.BACK:
                                if (lz == 0)
                                {
                                    if (wz == 0) { int planeIdx = wx * maxY + wy; bool covered = PlaneBit(planeNegZ, planeIdx); hide = covered && selfOpaque; }
                                    else if (sz > 0) { ref var nd = ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)]; if (nd.NonAirCount > 0) { ushort nb = GetLocalBlock(ref nd, lx, ly, 15); if (nb != 0 && selfOpaque && BlockProperties.IsOpaque(nb)) hide = true; } }
                                }
                                break;
                            case Faces.FRONT:
                                if (lz == S - 1)
                                {
                                    if (wz == maxZ - 1) { int planeIdx = wx * maxY + wy; bool covered = PlaneBit(planePosZ, planeIdx); hide = covered && selfOpaque; }
                                    else if (sz + 1 < szCount) { ref var nd = ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)]; if (nd.NonAirCount > 0) { ushort nb = GetLocalBlock(ref nd, lx, ly, 0); if (nb != 0 && selfOpaque && BlockProperties.IsOpaque(nb)) hide = true; } }
                                }
                                break;
                        }
                        if (!hide) { int tw = li >> 6; int tb = li & 63; targetMask[tw] |= 1UL << tb; }
                    }
                }
            }

            ProcessBoundary(_maskX0, faceNX, Faces.LEFT, nonAirBits, opaqueBits);
            ProcessBoundary(_maskX15, facePX, Faces.RIGHT, nonAirBits, opaqueBits);
            ProcessBoundary(_maskY0, faceNY, Faces.BOTTOM, nonAirBits, opaqueBits);
            ProcessBoundary(_maskY15, facePY, Faces.TOP, nonAirBits, opaqueBits);
            ProcessBoundary(_maskZ0, faceNZ, Faces.BACK, nonAirBits, opaqueBits);
            ProcessBoundary(_maskZ15, facePZ, Faces.FRONT, nonAirBits, opaqueBits);

            // ----------------------------------------------------------------------------------
            // 4. Emit faces from final masks (internal + boundary unified)
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
                idxList.Add(vb + 0); idxList.Add(vb + 1); idxList.Add(vb + 2); idxList.Add(vb + 2); idxList.Add(vb + 3); idxList.Add(vb + 0);
                vb += 4;
            }

            void EmitMask(Span<ulong> mask, Faces face)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;
                        int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                        EmitFace(face, baseX + lx, baseY + ly, baseZ + lz, dense[li]);
                    }
                }
            }

            EmitMask(faceNX, Faces.LEFT);
            EmitMask(facePX, Faces.RIGHT);
            EmitMask(faceNY, Faces.BOTTOM);
            EmitMask(facePY, Faces.TOP);
            EmitMask(faceNZ, Faces.BACK);
            EmitMask(facePZ, Faces.FRONT);

            vertBase = vb; // write back
        }
    }
}

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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureBoundaryMasks()
        {
            if (_boundaryMasksInit) return;
            // Build once (thread-race benign: idempotent content)
            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int li = ((z * 16 + x) * 16) + y;
                        int w = li >> 6; int b = li & 63; ulong bit = 1UL << b;
                        if (x == 0) _maskX0[w] |= bit; else if (x == 15) _maskX15[w] |= bit;
                        if (y == 0) _maskY0[w] |= bit; else if (y == 15) _maskY15[w] |= bit;
                        if (z == 0) _maskZ0[w] |= bit; else if (z == 15) _maskZ15[w] |= bit;
                    }
                }
            }
            _boundaryMasksInit = true;
        }

        // Specialized emission for Packed
        private void EmitPackedSection(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            if (desc.PackedBitData == null || desc.Palette == null || desc.BitsPerIndex <= 0 || desc.NonAirCount == 0)
                return;

            // Fast path: current generator only produces single-id packed sections (BitsPerIndex==1, palette: [air, id])
            // with an OccupancyBits bitset. Leverage that to avoid per-voxel Decode & full 3D scan.
            if (desc.BitsPerIndex == 1 &&
                desc.Palette.Count == 2 &&
                desc.OccupancyBits != null)
            {
                EmitPackedSingleIdFast(ref desc, sx, sy, sz, vertList, uvList, idxList, ref vertBase);
                return;
            }

            // ---- Generic (unchanged) path for multi-bit packed sections ----
            int S = data.sectionSize; // 16
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            var neighborNegX = data.NeighborPlaneNegX;
            var neighborPosX = data.NeighborPlanePosX;
            var neighborNegY = data.NeighborPlaneNegY;
            var neighborPosY = data.NeighborPlanePosY;
            var neighborNegZ = data.NeighborPlaneNegZ;
            var neighborPosZ = data.NeighborPlanePosZ;

            // UV cache per block id
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

            int bpi = desc.BitsPerIndex;
            uint[] bitData = desc.PackedBitData;
            var palette = desc.Palette;
            int mask = (1 << bpi) - 1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ushort Decode(int lx, int ly, int lz)
            {
                int li = ((lz * S + lx) * S) + ly;
                long bitPos = (long)li * bpi;
                int word = (int)(bitPos >> 5);
                int bitOffset = (int)(bitPos & 31);
                uint val = bitData[word] >> bitOffset;
                int rem = 32 - bitOffset;
                if (rem < bpi)
                    val |= bitData[word + 1] << rem;
                int pi = (int)(val & mask);
                if ((uint)pi >= (uint)palette.Count) return 0;
                return palette[pi];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IsOpaque(ushort id) => id != 0 && BlockProperties.IsOpaque(id);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6; int b = index & 63;
                return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
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
                idxList.Add(vb + 0);
                idxList.Add(vb + 1);
                idxList.Add(vb + 2);
                idxList.Add(vb + 2);
                idxList.Add(vb + 3);
                idxList.Add(vb + 0);
                vb += 4;
            }

            int lxMin = 0, lxMax = S - 1, lyMin = 0, lyMax = S - 1, lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            for (int lz = lzMin; lz <= lzMax; lz++)
            {
                int gz = baseZ + lz;
                for (int lx = lxMin; lx <= lxMax; lx++)
                {
                    int gx = baseX + lx;
                    for (int ly = lyMin; ly <= lyMax; ly++)
                    {
                        int gy = baseY + ly;
                        ushort block = Decode(lx, ly, lz);
                        if (block == 0) continue;
                        bool thisOpaque = IsOpaque(block);

                        // LEFT
                        if (lx == 0)
                        {
                            if (gx == 0)
                            {
                                bool covered = PlaneBit(neighborNegX, gz * maxY + gy);
                                if (!covered || !thisOpaque) EmitFace(Faces.LEFT, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx - 1, gy, gz);
                                if (!(thisOpaque && IsOpaque(nb))) EmitFace(Faces.LEFT, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = Decode(lx - 1, ly, lz);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.LEFT, gx, gy, gz, block);
                        }

                        // RIGHT
                        if (lx == S - 1)
                        {
                            if (gx == maxX - 1)
                            {
                                bool covered = PlaneBit(neighborPosX, gz * maxY + gy);
                                if (!covered || !thisOpaque) EmitFace(Faces.RIGHT, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx + 1, gy, gz);
                                if (!(thisOpaque && IsOpaque(nb))) EmitFace(Faces.RIGHT, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = Decode(lx + 1, ly, lz);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.RIGHT, gx, gy, gz, block);
                        }

                        // BOTTOM
                        if (ly == 0)
                        {
                            if (gy == 0)
                            {
                                bool covered = PlaneBit(neighborNegY, gx * maxZ + gz);
                                if (!covered || !thisOpaque) EmitFace(Faces.BOTTOM, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy - 1, gz);
                                if (!(thisOpaque && IsOpaque(nb))) EmitFace(Faces.BOTTOM, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = Decode(lx, ly - 1, lz);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.BOTTOM, gx, gy, gz, block);
                        }

                        // TOP
                        if (ly == S - 1)
                        {
                            if (gy == maxY - 1)
                            {
                                bool covered = PlaneBit(neighborPosY, gx * maxZ + gz);
                                if (!covered || !thisOpaque) EmitFace(Faces.TOP, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy + 1, gz);
                                if (!(thisOpaque && IsOpaque(nb))) EmitFace(Faces.TOP, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = Decode(lx, ly + 1, lz);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.TOP, gx, gy, gz, block);
                        }

                        // BACK
                        if (lz == 0)
                        {
                            if (gz == 0)
                            {
                                bool covered = PlaneBit(neighborNegZ, gx * maxY + gy);
                                if (!covered || !thisOpaque) EmitFace(Faces.BACK, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy, gz - 1);
                                if (!(thisOpaque && IsOpaque(nb))) EmitFace(Faces.BACK, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = Decode(lx, ly, lz - 1);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.BACK, gx, gy, gz, block);
                        }

                        // FRONT
                        if (lz == S - 1)
                        {
                            if (gz == maxZ - 1)
                            {
                                bool covered = PlaneBit(neighborPosZ, gx * maxY + gy);
                                if (!covered || !thisOpaque) EmitFace(Faces.FRONT, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy, gz + 1);
                                if (!(thisOpaque && IsOpaque(nb))) EmitFace(Faces.FRONT, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = Decode(lx, ly, lz + 1);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.FRONT, gx, gy, gz, block);
                        }
                    }
                }
            }

            vertBase = vb; // write back
        }

        // Fast path for single-id packed (BitsPerIndex=1) with face bitset derivation.
        private void EmitPackedSingleIdFast(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            int S = data.sectionSize; // 16
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            var occ = desc.OccupancyBits;
            if (occ == null) return; // defensive
            EnsureBoundaryMasks();

            ushort block = desc.Palette[1]; // palette[0]=air, [1]=the single id
            bool opaque = BlockProperties.IsOpaque(block);

            // Neighbor chunk face plane bitsets (may be null)
            var planeNegX = data.NeighborPlaneNegX;
            var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY;
            var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ;
            var planePosZ = data.NeighborPlanePosZ;

            int lxMin = 0, lxMax = S - 1, lyMin = 0, lyMax = S - 1, lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            var faceUV = new List<ByteVector2>[6];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            List<ByteVector2> GetFaceUV(Faces f)
            {
                int i = (int)f;
                return faceUV[i] ??= atlas.GetBlockUVs(block, f);
            }

            uint vb = vertBase;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void Emit(int wx, int wy, int wz, Faces face)
            {
                var vtx = RawFaceData.rawVertexData[face];
                for (int i = 0; i < 4; i++)
                {
                    vertList.Add((byte)(vtx[i].x + wx));
                    vertList.Add((byte)(vtx[i].y + wy));
                    vertList.Add((byte)(vtx[i].z + wz));
                }
                var uvFace = GetFaceUV(face);
                for (int i = 0; i < 4; i++) { uvList.Add(uvFace[i].x); uvList.Add(uvFace[i].y); }
                idxList.Add(vb + 0); idxList.Add(vb + 1); idxList.Add(vb + 2); idxList.Add(vb + 2); idxList.Add(vb + 3); idxList.Add(vb + 0); vb += 4;
            }

            // Neighbor edge caching (per-direction 16x16 plane bits) for internal section boundaries
            ulong[] edgePosXFromNegX = null, edgeNegXFromPosX = null; // yz planes from neighbor sections
            ulong[] edgePosYFromNegY = null, edgeNegYFromPosY = null; // xz planes
            ulong[] edgePosZFromNegZ = null, edgeNegZFromPosZ = null; // xy planes
            bool edgePosXOpaque = false, edgeNegXOpaque = false, edgePosYOpaque = false, edgeNegYOpaque = false, edgePosZOpaque = false, edgeNegZOpaque = false;

            SectionPrerenderDesc[] allSecs = data.SectionDescs;
            int sxCount = data.sectionsX, syCount = data.sectionsY, szCount = data.sectionsZ;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int SecIndex(int sxL, int syL, int szL, int syC, int szC) => ((sxL * syC) + syL) * szC + szL;

            void BuildNeighborFace(ref ulong[] target, ref bool opaqueFlag, int nsx, int nsy, int nsz, bool positiveFace, char axis)
            {
                if ((uint)nsx >= (uint)sxCount || (uint)nsy >= (uint)syCount || (uint)nsz >= (uint)szCount) return;
                ref var nd = ref allSecs[SecIndex(nsx, nsy, nsz, syCount, szCount)];
                if (nd.NonAirCount == 0) return;
                ushort nUniform = nd.UniformBlockId;
                bool nUniformValid = nd.Kind == 1 && nUniform != 0 && BlockProperties.IsOpaque(nUniform);
                bool nPackedSingle = nd.Kind == 4 && nd.BitsPerIndex == 1 && nd.Palette != null && nd.Palette.Count == 2 && nd.OccupancyBits != null && BlockProperties.IsOpaque(nd.Palette[1]);
                if (!nUniformValid && !nPackedSingle) return; // not an occluding neighbor we can quickly test
                target = new ulong[4]; // 256 bits plane
                opaqueFlag = true; // only filled when opaque
                if (nUniformValid)
                {
                    // All bits set (every voxel solid & opaque)
                    target[0] = target[1] = target[2] = target[3] = ulong.MaxValue;
                    return;
                }
                // Extract edge from occupancy
                var nOcc = nd.OccupancyBits;
                if (axis == 'X')
                {
                    int nx = positiveFace ? 15 : 0;
                    for (int nz = 0; nz < 16; nz++)
                    {
                        for (int ny = 0; ny < 16; ny++)
                        {
                            int nli = ((nz * 16 + nx) * 16) + ny;
                            if ((nOcc[nli >> 6] & (1UL << (nli & 63))) == 0) continue;
                            int yz = nz * 16 + ny; target[yz >> 6] |= 1UL << (yz & 63);
                        }
                    }
                }
                else if (axis == 'Y')
                {
                    int ny = positiveFace ? 15 : 0;
                    for (int nx = 0; nx < 16; nx++)
                    {
                        for (int nz = 0; nz < 16; nz++)
                        {
                            int nli = ((nz * 16 + nx) * 16) + ny;
                            if ((nOcc[nli >> 6] & (1UL << (nli & 63))) == 0) continue;
                            int xz = nx * 16 + nz; target[xz >> 6] |= 1UL << (xz & 63);
                        }
                    }
                }
                else // Z
                {
                    int nz = positiveFace ? 15 : 0;
                    for (int nx = 0; nx < 16; nx++)
                    {
                        for (int ny = 0; ny < 16; ny++)
                        {
                            int nli = ((nz * 16 + nx) * 16) + ny;
                            if ((nOcc[nli >> 6] & (1UL << (nli & 63))) == 0) continue;
                            int xy = nx * 16 + ny; target[xy >> 6] |= 1UL << (xy & 63);
                        }
                    }
                }
            }

            // Build needed neighbor faces (only if internal section boundaries exist)
            if (sx > 0)
                BuildNeighborFace(ref edgePosXFromNegX, ref edgePosXOpaque, sx - 1, sy, sz, true, 'X');
            if (sx + 1 < sxCount)
                BuildNeighborFace(ref edgeNegXFromPosX, ref edgeNegXOpaque, sx + 1, sy, sz, false, 'X');

            if (sy > 0)
                BuildNeighborFace(ref edgePosYFromNegY, ref edgePosYOpaque, sx, sy - 1, sz, true, 'Y');
            if (sy + 1 < syCount)
                BuildNeighborFace(ref edgeNegYFromPosY, ref edgeNegYOpaque, sx, sy + 1, sz, false, 'Y');

            if (sz > 0)
                BuildNeighborFace(ref edgePosZFromNegZ, ref edgePosZOpaque, sx, sy, sz - 1, true, 'Z');
            if (sz + 1 < szCount)
                BuildNeighborFace(ref edgeNegZFromPosZ, ref edgeNegZOpaque, sx, sy, sz + 1, false, 'Z');

            // Working buffers
            Span<ulong> shiftPos = stackalloc ulong[64];
            Span<ulong> faceNegX = stackalloc ulong[64];
            Span<ulong> facePosX = stackalloc ulong[64];
            Span<ulong> faceNegY = stackalloc ulong[64];
            Span<ulong> facePosY = stackalloc ulong[64];
            Span<ulong> faceNegZ = stackalloc ulong[64];
            Span<ulong> facePosZ = stackalloc ulong[64];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftLeft(ulong[] src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6;
                int bitShift = shiftBits & 63;

                for (int i = 63; i >= 0; i--)
                {
                    ulong val = 0;
                    int si = i - wordShift;

                    if (si >= 0)
                    {
                        val = src[si];
                        if (bitShift != 0 && si - 1 >= 0)
                            val = (val << bitShift) | (src[si - 1] >> (64 - bitShift));
                        else if (bitShift != 0)
                            val <<= bitShift;
                    }

                    dst[i] = val;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftRight(ulong[] src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6;
                int bitShift = shiftBits & 63;

                for (int i = 0; i < 64; i++)
                {
                    ulong val = 0;
                    int si = i + wordShift;

                    if (si < 64)
                    {
                        val = src[si];
                        if (bitShift != 0 && si + 1 < 64)
                            val = (val >> bitShift) | (src[si + 1] << (64 - bitShift));
                        else if (bitShift != 0)
                            val >>= bitShift;
                    }

                    dst[i] = val;
                }
            }

            const int strideX = 16, strideY = 1, strideZ = 256;

            // Compute face masks
            ShiftLeft(occ, strideX, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX0[i];
                faceNegX[i] = cand & ~shiftPos[i];
            }

            ShiftRight(occ, strideX, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX15[i];
                facePosX[i] = cand & ~shiftPos[i];
            }

            ShiftLeft(occ, strideY, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY0[i];
                faceNegY[i] = cand & ~shiftPos[i];
            }

            ShiftRight(occ, strideY, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY15[i];
                facePosY[i] = cand & ~shiftPos[i];
            }

            ShiftLeft(occ, strideZ, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ0[i];
                faceNegZ[i] = cand & ~shiftPos[i];
            }

            ShiftRight(occ, strideZ, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ15[i];
                facePosZ[i] = cand & ~shiftPos[i];
            }

            // Emit visible faces
            void EmitMask(Span<ulong> mask, Faces face)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    if (word == 0) continue;

                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;

                        int ly = li & 15;
                        int t = li >> 4;
                        int lx = t & 15;
                        int lz = t >> 4;

                        if (lx < lxMin || lx > lxMax ||
                            ly < lyMin || ly > lyMax ||
                            lz < lzMin || lz > lzMax)
                            continue;

                        Emit(baseX + lx, baseY + ly, baseZ + lz, face);
                    }
                }
            }

            EmitMask(faceNegX, Faces.LEFT);
            EmitMask(facePosX, Faces.RIGHT);
            EmitMask(faceNegY, Faces.BOTTOM);
            EmitMask(facePosY, Faces.TOP);
            EmitMask(faceNegZ, Faces.BACK);
            EmitMask(facePosZ, Faces.FRONT);

            // ---- Word classification for boundary processing (readable form) ----
            Span<int> boundaryWordTmp = stackalloc int[64];
            int boundaryCount = 0;
            for (int wi = 0; wi < 64; wi++)
            {
                ulong boundaryBitsInWord = (_maskX0[wi] | _maskX15[wi] | _maskY0[wi] | _maskY15[wi] | _maskZ0[wi] | _maskZ15[wi]) & occ[wi];
                if (boundaryBitsInWord != 0)
                    boundaryWordTmp[boundaryCount++] = wi;
            }
            int[] boundaryIdxArr = null;
            if (boundaryCount > 0)
            {
                boundaryIdxArr = System.Buffers.ArrayPool<int>.Shared.Rent(boundaryCount);
                for (int i = 0; i < boundaryCount; i++) boundaryIdxArr[i] = boundaryWordTmp[i];
            }

            void EmitBoundary(ulong[] boundaryMask, Faces face, Action<int,int,int> testAndEmit)
            {
                if (boundaryCount == 0) return;
                var idxArr = boundaryIdxArr;
                for (int bi = 0; bi < boundaryCount; bi++)
                {
                    int wi = idxArr[bi];
                    ulong word = occ[wi] & boundaryMask[wi];
                    if (word == 0) continue;
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;
                        int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                        testAndEmit(lx, ly, lz);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)] bool EdgeBit(ulong[] plane, int idx) => plane != null && (plane[idx >> 6] & (1UL << (idx & 63))) != 0UL;

            // LEFT boundary (x==0)
            EmitBoundary(_maskX0, Faces.LEFT, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wx == 0)
                {
                    int planeIdx = wz * maxY + wy; bool covered = EdgeBit(planeNegX, planeIdx);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.LEFT);
                }
                else if (edgePosXFromNegX != null && edgePosXOpaque)
                {
                    int yz = lz * 16 + ly; if (!(opaque && (edgePosXFromNegX[yz >> 6] & (1UL << (yz & 63))) != 0UL)) Emit(wx, wy, wz, Faces.LEFT);
                }
                else
                {
                    ushort nb = GetBlock(wx - 1, wy, wz); if (!(opaque && BlockProperties.IsOpaque(nb))) Emit(wx, wy, wz, Faces.LEFT);
                }
            });
            // RIGHT boundary (x==15)
            EmitBoundary(_maskX15, Faces.RIGHT, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wx == maxX - 1)
                {
                    int planeIdx = wz * maxY + wy; bool covered = EdgeBit(planePosX, planeIdx);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.RIGHT);
                }
                else if (edgeNegXFromPosX != null && edgeNegXOpaque)
                {
                    int yz = lz * 16 + ly; if (!(opaque && (edgeNegXFromPosX[yz >> 6] & (1UL << (yz & 63))) != 0UL)) Emit(wx, wy, wz, Faces.RIGHT);
                }
                else
                {
                    ushort nb = GetBlock(wx + 1, wy, wz); if (!(opaque && BlockProperties.IsOpaque(nb))) Emit(wx, wy, wz, Faces.RIGHT);
                }
            });
            // BOTTOM boundary (y==0)
            EmitBoundary(_maskY0, Faces.BOTTOM, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wy == 0)
                {
                    int planeIdx = wx * maxZ + wz; bool covered = EdgeBit(planeNegY, planeIdx);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.BOTTOM);
                }
                else if (edgePosYFromNegY != null && edgePosYOpaque)
                {
                    int xz = lx * 16 + lz; if (!(opaque && (edgePosYFromNegY[xz >> 6] & (1UL << (xz & 63))) != 0UL)) Emit(wx, wy, wz, Faces.BOTTOM);
                }
                else
                {
                    ushort nb = GetBlock(wx, wy - 1, wz); if (!(opaque && BlockProperties.IsOpaque(nb))) Emit(wx, wy, wz, Faces.BOTTOM);
                }
            });
            // TOP boundary (y==15)
            EmitBoundary(_maskY15, Faces.TOP, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wy == maxY - 1)
                {
                    int planeIdx = wx * maxZ + wz; bool covered = EdgeBit(planePosY, planeIdx);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.TOP);
                }
                else if (edgeNegYFromPosY != null && edgeNegYOpaque)
                {
                    int xz = lx * 16 + lz; if (!(opaque && (edgeNegYFromPosY[xz >> 6] & (1UL << (xz & 63))) != 0UL)) Emit(wx, wy, wz, Faces.TOP);
                }
                else
                {
                    ushort nb = GetBlock(wx, wy + 1, wz); if (!(opaque && BlockProperties.IsOpaque(nb))) Emit(wx, wy, wz, Faces.TOP);
                }
            });
            // BACK boundary (z==0)
            EmitBoundary(_maskZ0, Faces.BACK, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wz == 0)
                {
                    int planeIdx = wx * maxY + wy; bool covered = EdgeBit(planeNegZ, planeIdx);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.BACK);
                }
                else if (edgePosZFromNegZ != null && edgePosZOpaque)
                {
                    int xy = lx * 16 + ly; if (!(opaque && (edgePosZFromNegZ[xy >> 6] & (1UL << (xy & 63))) != 0UL)) Emit(wx, wy, wz, Faces.BACK);
                }
                else
                {
                    ushort nb = GetBlock(wx, wy, wz - 1); if (!(opaque && BlockProperties.IsOpaque(nb))) Emit(wx, wy, wz, Faces.BACK);
                }
            });
            // FRONT boundary (z==15)
            EmitBoundary(_maskZ15, Faces.FRONT, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wz == maxZ - 1)
                {
                    int planeIdx = wx * maxY + wy; bool covered = EdgeBit(planePosZ, planeIdx);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.FRONT);
                }
                else if (edgeNegZFromPosZ != null && edgeNegZOpaque)
                {
                    int xy = lx * 16 + ly; if (!(opaque && (edgeNegZFromPosZ[xy >> 6] & (1UL << (xy & 63))) != 0UL)) Emit(wx, wy, wz, Faces.FRONT);
                }
                else
                {
                    ushort nb = GetBlock(wx, wy, wz + 1); if (!(opaque && BlockProperties.IsOpaque(nb))) Emit(wx, wy, wz, Faces.FRONT);
                }
            });

            if (boundaryIdxArr != null) System.Buffers.ArrayPool<int>.Shared.Return(boundaryIdxArr, clearArray: false);
            vertBase = vb;
        }
    }
}

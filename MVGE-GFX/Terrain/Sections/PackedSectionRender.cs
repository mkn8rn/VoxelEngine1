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

            // Helpers for big bitset shifts (4096 bits)
            Span<ulong> tmpShift = stackalloc ulong[64];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftLeft(ulong[] src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6; int bitShift = shiftBits & 63; int len = 64;
                for (int i = len - 1; i >= 0; i--)
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
                int wordShift = shiftBits >> 6; int bitShift = shiftBits & 63; int len = 64;
                for (int i = 0; i < len; i++)
                {
                    ulong val = 0;
                    int si = i + wordShift;
                    if (si < len)
                    {
                        val = src[si];
                        if (bitShift != 0 && si + 1 < len)
                            val = (val >> bitShift) | (src[si + 1] << (64 - bitShift));
                        else if (bitShift != 0)
                            val >>= bitShift;
                    }
                    dst[i] = val;
                }
            }

            // Build internal face masks (exclude boundary layers)
            // Candidate masks respecting bounds: we will AND with bounds later when emitting.
            const int strideX = 16; const int strideY = 1; const int strideZ = 256;
            Span<ulong> shiftPos = stackalloc ulong[64];
            Span<ulong> faceNegX = stackalloc ulong[64];
            Span<ulong> facePosX = stackalloc ulong[64];
            Span<ulong> faceNegY = stackalloc ulong[64];
            Span<ulong> facePosY = stackalloc ulong[64];
            Span<ulong> faceNegZ = stackalloc ulong[64];
            Span<ulong> facePosZ = stackalloc ulong[64];

            // X-
            ShiftLeft(occ, strideX, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX0[i];
                faceNegX[i] = cand & ~shiftPos[i];
            }
            // X+
            ShiftRight(occ, strideX, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX15[i];
                facePosX[i] = cand & ~shiftPos[i];
            }
            // Y-
            ShiftLeft(occ, strideY, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY0[i];
                faceNegY[i] = cand & ~shiftPos[i];
            }
            // Y+
            ShiftRight(occ, strideY, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY15[i];
                facePosY[i] = cand & ~shiftPos[i];
            }
            // Z-
            ShiftLeft(occ, strideZ, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ0[i];
                faceNegZ[i] = cand & ~shiftPos[i];
            }
            // Z+
            ShiftRight(occ, strideZ, shiftPos);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ15[i];
                facePosZ[i] = cand & ~shiftPos[i];
            }

            // Emit internal faces (respect bounds)
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
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
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

            // Boundary faces (six sides) - need cross-section or chunk neighbor tests.
            // Helper to iterate boundary occupancy bits with mask M.
            void EmitBoundary(ulong[] boundaryMask, Faces face, Action<int,int,int> testAndEmit)
            {
                for (int wi = 0; wi < 64; wi++)
                {
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool NeighborOpaqueWorld(int wx, int wy, int wz)
            {
                ushort nb = GetBlock(wx, wy, wz);
                return opaque && BlockProperties.IsOpaque(nb);
            }

            // LEFT boundary (x==0)
            EmitBoundary(_maskX0, Faces.LEFT, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wx == 0)
                {
                    int planeIdx = wz * maxY + wy;
                    bool covered = planeNegX != null && planeIdx >= 0 && ((planeNegX[planeIdx >> 6] & (1UL << (planeIdx & 63))) != 0UL);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.LEFT);
                }
                else
                {
                    if (!NeighborOpaqueWorld(wx - 1, wy, wz)) Emit(wx, wy, wz, Faces.LEFT);
                }
            });
            // RIGHT boundary (x==15)
            EmitBoundary(_maskX15, Faces.RIGHT, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wx == maxX - 1)
                {
                    int planeIdx = wz * maxY + wy;
                    bool covered = planePosX != null && ((planePosX[planeIdx >> 6] & (1UL << (planeIdx & 63))) != 0UL);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.RIGHT);
                }
                else
                {
                    if (!NeighborOpaqueWorld(wx + 1, wy, wz)) Emit(wx, wy, wz, Faces.RIGHT);
                }
            });
            // BOTTOM boundary (y==0)
            EmitBoundary(_maskY0, Faces.BOTTOM, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wy == 0)
                {
                    int planeIdx = wx * maxZ + wz;
                    bool covered = planeNegY != null && ((planeNegY[planeIdx >> 6] & (1UL << (planeIdx & 63))) != 0UL);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.BOTTOM);
                }
                else
                {
                    if (!NeighborOpaqueWorld(wx, wy - 1, wz)) Emit(wx, wy, wz, Faces.BOTTOM);
                }
            });
            // TOP boundary (y==15)
            EmitBoundary(_maskY15, Faces.TOP, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wy == maxY - 1)
                {
                    int planeIdx = wx * maxZ + wz;
                    bool covered = planePosY != null && ((planePosY[planeIdx >> 6] & (1UL << (planeIdx & 63))) != 0UL);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.TOP);
                }
                else
                {
                    if (!NeighborOpaqueWorld(wx, wy + 1, wz)) Emit(wx, wy, wz, Faces.TOP);
                }
            });
            // BACK boundary (z==0)
            EmitBoundary(_maskZ0, Faces.BACK, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wz == 0)
                {
                    int planeIdx = wx * maxY + wy;
                    bool covered = planeNegZ != null && ((planeNegZ[planeIdx >> 6] & (1UL << (planeIdx & 63))) != 0UL);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.BACK);
                }
                else
                {
                    if (!NeighborOpaqueWorld(wx, wy, wz - 1)) Emit(wx, wy, wz, Faces.BACK);
                }
            });
            // FRONT boundary (z==15)
            EmitBoundary(_maskZ15, Faces.FRONT, (lx, ly, lz) =>
            {
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                if (wz == maxZ - 1)
                {
                    int planeIdx = wx * maxY + wy;
                    bool covered = planePosZ != null && ((planePosZ[planeIdx >> 6] & (1UL << (planeIdx & 63))) != 0UL);
                    if (!(covered && opaque)) Emit(wx, wy, wz, Faces.FRONT);
                }
                else
                {
                    if (!NeighborOpaqueWorld(wx, wy, wz + 1)) Emit(wx, wy, wz, Faces.FRONT);
                }
            });

            vertBase = vb;
        }
    }
}

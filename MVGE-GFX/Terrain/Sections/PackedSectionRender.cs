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
            var uvCache = new Dictionary<ushort, List<ByteVector2>[]>();
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

            // local copy of vertBase
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

        // Fast path for single-id packed (BitsPerIndex=1). Uses OccupancyBits to iterate only set voxels.
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

            ushort block = desc.Palette[1]; // palette[0]=air, [1]=the single id
            bool thisOpaque = BlockProperties.IsOpaque(block);

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
            void EmitFace(Faces face, int wx, int wy, int wz)
            {
                var vtx = RawFaceData.rawVertexData[face];
                for (int i = 0; i < 4; i++)
                {
                    vertList.Add((byte)(vtx[i].x + wx));
                    vertList.Add((byte)(vtx[i].y + wy));
                    vertList.Add((byte)(vtx[i].z + wz));
                }
                var uvFace = GetFaceUV(face);
                for (int i = 0; i < 4; i++)
                {
                    uvList.Add(uvFace[i].x);
                    uvList.Add(uvFace[i].y);
                }
                idxList.Add(vb + 0); idxList.Add(vb + 1); idxList.Add(vb + 2);
                idxList.Add(vb + 2); idxList.Add(vb + 3); idxList.Add(vb + 0);
                vb += 4;
            }

            // Separate helpers so plane (nullable) and occupancy (non-null) both safe & fast.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool OccBit(ulong[] occBits, int li)
            {
                int w = li >> 6;
                int b = li & 63;
                return (occBits[w] & (1UL << b)) != 0UL;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBitSafe(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6;
                if (w >= plane.Length) return false;
                return (plane[w] & (1UL << (index & 63))) != 0UL;
            }

            const int strideY = 1;
            int strideX = S;     // 16
            int strideZ = S * S; // 256

            for (int w = 0; w < occ.Length; w++)
            {
                ulong word = occ[w];
                if (word == 0) continue;
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    int li = (w << 6) + bit;
                    word &= word - 1;

                    int ly = li & (S - 1);
                    int t = li >> 4;
                    int lx = t & (S - 1);
                    int lz = t >> 4;

                    if (lx < lxMin || lx > lxMax ||
                        ly < lyMin || ly > lyMax ||
                        lz < lzMin || lz > lzMax)
                        continue;

                    int wx = baseX + lx;
                    int wy = baseY + ly;
                    int wz = baseZ + lz;

                    // LEFT (-X)
                    if (lx == 0)
                    {
                        if (wx == 0)
                        {
                            bool covered = PlaneBitSafe(planeNegX, wz * maxY + wy);
                            if (!covered || !thisOpaque) EmitFace(Faces.LEFT, wx, wy, wz);
                        }
                        else
                        {
                            ushort nb = GetBlock(wx - 1, wy, wz);
                            if (!(thisOpaque && BlockProperties.IsOpaque(nb))) EmitFace(Faces.LEFT, wx, wy, wz);
                        }
                    }
                    else
                    {
                        if (!OccBit(occ, li - strideX) || !thisOpaque) EmitFace(Faces.LEFT, wx, wy, wz);
                    }

                    // RIGHT (+X)
                    if (lx == S - 1)
                    {
                        if (wx == maxX - 1)
                        {
                            bool covered = PlaneBitSafe(planePosX, wz * maxY + wy);
                            if (!covered || !thisOpaque) EmitFace(Faces.RIGHT, wx, wy, wz);
                        }
                        else
                        {
                            ushort nb = GetBlock(wx + 1, wy, wz);
                            if (!(thisOpaque && BlockProperties.IsOpaque(nb))) EmitFace(Faces.RIGHT, wx, wy, wz);
                        }
                    }
                    else
                    {
                        if (!OccBit(occ, li + strideX) || !thisOpaque) EmitFace(Faces.RIGHT, wx, wy, wz);
                    }

                    // BOTTOM (-Y)
                    if (ly == 0)
                    {
                        if (wy == 0)
                        {
                            bool covered = PlaneBitSafe(planeNegY, wx * maxZ + wz);
                            if (!covered || !thisOpaque) EmitFace(Faces.BOTTOM, wx, wy, wz);
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, wy - 1, wz);
                            if (!(thisOpaque && BlockProperties.IsOpaque(nb))) EmitFace(Faces.BOTTOM, wx, wy, wz);
                        }
                    }
                    else
                    {
                        if (!OccBit(occ, li - strideY) || !thisOpaque) EmitFace(Faces.BOTTOM, wx, wy, wz);
                    }

                    // TOP (+Y)
                    if (ly == S - 1)
                    {
                        if (wy == maxY - 1)
                        {
                            bool covered = PlaneBitSafe(planePosY, wx * maxZ + wz);
                            if (!covered || !thisOpaque) EmitFace(Faces.TOP, wx, wy, wz);
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, wy + 1, wz);
                            if (!(thisOpaque && BlockProperties.IsOpaque(nb))) EmitFace(Faces.TOP, wx, wy, wz);
                        }
                    }
                    else
                    {
                        if (!OccBit(occ, li + strideY) || !thisOpaque) EmitFace(Faces.TOP, wx, wy, wz);
                    }

                    // BACK (-Z)
                    if (lz == 0)
                    {
                        if (wz == 0)
                        {
                            bool covered = PlaneBitSafe(planeNegZ, wx * maxY + wy);
                            if (!covered || !thisOpaque) EmitFace(Faces.BACK, wx, wy, wz);
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, wy, wz - 1);
                            if (!(thisOpaque && BlockProperties.IsOpaque(nb))) EmitFace(Faces.BACK, wx, wy, wz);
                        }
                    }
                    else
                    {
                        if (!OccBit(occ, li - strideZ) || !thisOpaque) EmitFace(Faces.BACK, wx, wy, wz);
                    }

                    // FRONT (+Z)
                    if (lz == S - 1)
                    {
                        if (wz == maxZ - 1)
                        {
                            bool covered = PlaneBitSafe(planePosZ, wx * maxY + wy);
                            if (!covered || !thisOpaque) EmitFace(Faces.FRONT, wx, wy, wz);
                        }
                        else
                        {
                            ushort nb = GetBlock(wx, wy, wz + 1);
                            if (!(thisOpaque && BlockProperties.IsOpaque(nb))) EmitFace(Faces.FRONT, wx, wy, wz);
                        }
                    }
                    else
                    {
                        if (!OccBit(occ, li + strideZ) || !thisOpaque) EmitFace(Faces.FRONT, wx, wy, wz);
                    }
                }
            }

            vertBase = vb;
        }
    }
}

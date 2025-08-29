using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Specialized emission for DenseExpanded
        private void EmitDenseExpandedSection(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            if (desc.ExpandedDense == null || desc.NonAirCount == 0) return;

            int S = data.sectionSize; // 16
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            var dense = desc.ExpandedDense;
            var neighborNegX = data.NeighborPlaneNegX;
            var neighborPosX = data.NeighborPlanePosX;
            var neighborNegY = data.NeighborPlaneNegY;
            var neighborPosY = data.NeighborPlanePosY;
            var neighborNegZ = data.NeighborPlaneNegZ;
            var neighborPosZ = data.NeighborPlanePosZ;

            // UV cache per block id -> per-face UV arrays
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort DenseAt(ushort[] arr, int lx, int ly, int lz, int SLocal)
                => arr[((lz * SLocal + lx) * SLocal) + ly];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IsOpaque(ushort id) => id != 0 && BlockProperties.IsOpaque(id);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6; int b = index & 63;
                return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
            }

            // local copy to avoid capturing ref parameter inside local function
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

            // Iterate only bound sub-box
            for (int lz = lzMin; lz <= lzMax; lz++)
            {
                int gz = baseZ + lz;
                for (int lx = lxMin; lx <= lxMax; lx++)
                {
                    int gx = baseX + lx;
                    for (int ly = lyMin; ly <= lyMax; ly++)
                    {
                        int gy = baseY + ly;
                        ushort block = DenseAt(dense, lx, ly, lz, S);
                        if (block == 0) continue;
                        bool thisOpaque = IsOpaque(block);

                        // LEFT (-X)
                        if (lx == 0)
                        {
                            if (gx == 0)
                            {
                                // Chunk boundary: use neighbor plane
                                bool covered = PlaneBit(neighborNegX, gz * maxY + gy);
                                if (!covered || !thisOpaque) EmitFace(Faces.LEFT, gx, gy, gz, block);
                            }
                            else
                            {
                                // Adjacent section inside chunk
                                ushort nb = GetBlock(gx - 1, gy, gz);
                                if (!(thisOpaque && IsOpaque(nb))) EmitFace(Faces.LEFT, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = DenseAt(dense, lx - 1, ly, lz, S);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.LEFT, gx, gy, gz, block);
                        }

                        // RIGHT (+X)
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
                            ushort nb = DenseAt(dense, lx + 1, ly, lz, S);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.RIGHT, gx, gy, gz, block);
                        }

                        // BOTTOM (-Y)
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
                            ushort nb = DenseAt(dense, lx, ly - 1, lz, S);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.BOTTOM, gx, gy, gz, block);
                        }

                        // TOP (+Y)
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
                            ushort nb = DenseAt(dense, lx, ly + 1, lz, S);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.TOP, gx, gy, gz, block);
                        }

                        // BACK (-Z)
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
                            ushort nb = DenseAt(dense, lx, ly, lz - 1, S);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.BACK, gx, gy, gz, block);
                        }

                        // FRONT (+Z)
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
                            ushort nb = DenseAt(dense, lx, ly, lz + 1, S);
                            if (nb == 0 || !(thisOpaque && IsOpaque(nb))) EmitFace(Faces.FRONT, gx, gy, gz, block);
                        }
                    }
                }
            }

            // write back updated vertex base
            vertBase = vb;
        }
    }
}

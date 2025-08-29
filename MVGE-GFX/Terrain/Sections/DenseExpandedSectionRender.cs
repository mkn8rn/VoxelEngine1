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
            var dense = desc.ExpandedDense;

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            // Neighbor planes (chunk boundaries)
            var nNegX = data.NeighborPlaneNegX;
            var nPosX = data.NeighborPlanePosX;
            var nNegY = data.NeighborPlaneNegY;
            var nPosY = data.NeighborPlanePosY;
            var nNegZ = data.NeighborPlaneNegZ;
            var nPosZ = data.NeighborPlanePosZ;

            // Bounds (tight sub-box)
            int lxMin = 0, lxMax = S - 1, lyMin = 0, lyMax = S - 1, lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            // UV + opacity caches (per-section)
            var uvCache = new Dictionary<ushort, List<ByteVector2>[]>();
            var opaqueCache = new Dictionary<ushort, bool>();

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
            bool IsOpaqueFast(ushort id)
            {
                if (id == 0) return false;
                if (!opaqueCache.TryGetValue(id, out bool op))
                {
                    op = BlockProperties.IsOpaque(id);
                    opaqueCache[id] = op;
                }
                return op;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6;
                int b = index & 63;
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
                idxList.Add(vb); idxList.Add(vb + 1); idxList.Add(vb + 2);
                idxList.Add(vb + 2); idxList.Add(vb + 3); idxList.Add(vb);
                vb += 4;
            }

            // Strides for linear index (li = ((z*S + x) * S) + y)
            int strideX = S;      // moving +X changes (z*S + x) by +1 -> li + S
            int strideZ = S * S;  // moving +Z changes z by +1 -> li + S*S
            int strideY = 1;      // moving +Y changes +1

            for (int lz = lzMin; lz <= lzMax; lz++)
            {
                int gz = baseZ + lz;
                bool zAt0 = (lz == 0);
                bool zAtMax = (lz == S - 1);
                for (int lx = lxMin; lx <= lxMax; lx++)
                {
                    int gx = baseX + lx;
                    bool xAt0 = (lx == 0);
                    bool xAtMax = (lx == S - 1);
                    int stripBase = ((lz * S) + lx) * S; // start of this (z,x) column over y

                    for (int ly = lyMin; ly <= lyMax; ly++)
                    {
                        int li = stripBase + ly;
                        ushort block = dense[li];
                        if (block == 0) continue;

                        int gy = baseY + ly;
                        bool yAt0 = (ly == 0);
                        bool yAtMax = (ly == S - 1);
                        bool selfOpaque = IsOpaqueFast(block);

                        // Interior fully opaque skip (all 6 neighbors inside section & opaque)
                        if (!xAt0 && !xAtMax && !yAt0 && !yAtMax && !zAt0 && !zAtMax && selfOpaque)
                        {
                            ushort nxm = dense[li - strideX];
                            if (!IsOpaqueFast(nxm)) goto processFaces; // early exit of skip check
                            ushort nxp = dense[li + strideX]; if (!IsOpaqueFast(nxp)) goto processFaces;
                            ushort nym = dense[li - strideY]; if (!IsOpaqueFast(nym)) goto processFaces;
                            ushort nyp = dense[li + strideY]; if (!IsOpaqueFast(nyp)) goto processFaces;
                            ushort nzm = dense[li - strideZ]; if (!IsOpaqueFast(nzm)) goto processFaces;
                            ushort nzp = dense[li + strideZ]; if (!IsOpaqueFast(nzp)) goto processFaces;
                            continue; // fully occluded interior voxel
                        }

                    processFaces:
                        // LEFT (-X)
                        if (xAt0)
                        {
                            if (gx == 0)
                            {
                                int planeIdx = gz * maxY + gy;
                                if (!(PlaneBit(nNegX, planeIdx) && selfOpaque))
                                    EmitFace(Faces.LEFT, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx - 1, gy, gz);
                                if (!(selfOpaque && IsOpaqueFast(nb)))
                                    EmitFace(Faces.LEFT, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = dense[li - strideX];
                            if (!(selfOpaque && IsOpaqueFast(nb)))
                                EmitFace(Faces.LEFT, gx, gy, gz, block);
                        }

                        // RIGHT (+X)
                        if (xAtMax)
                        {
                            if (gx == maxX - 1)
                            {
                                int planeIdx = gz * maxY + gy;
                                if (!(PlaneBit(nPosX, planeIdx) && selfOpaque))
                                    EmitFace(Faces.RIGHT, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx + 1, gy, gz);
                                if (!(selfOpaque && IsOpaqueFast(nb)))
                                    EmitFace(Faces.RIGHT, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = dense[li + strideX];
                            if (!(selfOpaque && IsOpaqueFast(nb)))
                                EmitFace(Faces.RIGHT, gx, gy, gz, block);
                        }

                        // BOTTOM (-Y)
                        if (yAt0)
                        {
                            if (gy == 0)
                            {
                                int planeIdx = gx * maxZ + gz;
                                if (!(PlaneBit(nNegY, planeIdx) && selfOpaque))
                                    EmitFace(Faces.BOTTOM, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy - 1, gz);
                                if (!(selfOpaque && IsOpaqueFast(nb)))
                                    EmitFace(Faces.BOTTOM, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = dense[li - strideY];
                            if (!(selfOpaque && IsOpaqueFast(nb)))
                                EmitFace(Faces.BOTTOM, gx, gy, gz, block);
                        }

                        // TOP (+Y)
                        if (yAtMax)
                        {
                            if (gy == maxY - 1)
                            {
                                int planeIdx = gx * maxZ + gz;
                                if (!(PlaneBit(nPosY, planeIdx) && selfOpaque))
                                    EmitFace(Faces.TOP, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy + 1, gz);
                                if (!(selfOpaque && IsOpaqueFast(nb)))
                                    EmitFace(Faces.TOP, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = dense[li + strideY];
                            if (!(selfOpaque && IsOpaqueFast(nb)))
                                EmitFace(Faces.TOP, gx, gy, gz, block);
                        }

                        // BACK (-Z)
                        if (zAt0)
                        {
                            if (gz == 0)
                            {
                                int planeIdx = gx * maxY + gy;
                                if (!(PlaneBit(nNegZ, planeIdx) && selfOpaque))
                                    EmitFace(Faces.BACK, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy, gz - 1);
                                if (!(selfOpaque && IsOpaqueFast(nb)))
                                    EmitFace(Faces.BACK, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = dense[li - strideZ];
                            if (!(selfOpaque && IsOpaqueFast(nb)))
                                EmitFace(Faces.BACK, gx, gy, gz, block);
                        }

                        // FRONT (+Z)
                        if (zAtMax)
                        {
                            if (gz == maxZ - 1)
                            {
                                int planeIdx = gx * maxY + gy;
                                if (!(PlaneBit(nPosZ, planeIdx) && selfOpaque))
                                    EmitFace(Faces.FRONT, gx, gy, gz, block);
                            }
                            else
                            {
                                ushort nb = GetBlock(gx, gy, gz + 1);
                                if (!(selfOpaque && IsOpaqueFast(nb)))
                                    EmitFace(Faces.FRONT, gx, gy, gz, block);
                            }
                        }
                        else
                        {
                            ushort nb = dense[li + strideZ];
                            if (!(selfOpaque && IsOpaqueFast(nb)))
                                EmitFace(Faces.FRONT, gx, gy, gz, block);
                        }
                    }
                }
            }

            // write back updated vertex base
            vertBase = vb; 
        }
    }
}

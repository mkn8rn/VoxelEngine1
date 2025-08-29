using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Specialized emission for Sparse sections (Kind=2)
        // SparseIndices contain linear indices in column-major layout: li = ( (z * S + x) * S ) + y
        private void EmitSparseSection(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            if (desc.SparseIndices == null || desc.SparseBlocks == null || desc.SparseIndices.Length == 0)
                return;

            int S = data.sectionSize; // expected 16
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // Mapping from linear voxel index within section to (x,y,z)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void DecodeIndex(int li, int SLocal, out int lx, out int ly, out int lz)
            {
                ly = li & (SLocal - 1); // low 4 bits (S=16)
                int rest = li >> 4;     // divide by S
                lx = rest % SLocal;
                lz = rest / SLocal;
            }

            // Quick dictionary for sparse neighbor lookup inside same section (only needed if number of voxels is small; linear scan would also work but we keep O(1)).
            // Build only once.
            var localMap = new Dictionary<int, ushort>(desc.SparseIndices.Length * 2);
            var indices = desc.SparseIndices;
            var blocks = desc.SparseBlocks;
            for (int i = 0; i < indices.Length; i++)
            {
                localMap[indices[i]] = blocks[i];
            }

            // Local helpers
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool Opaque(ushort id) => id != 0 && BlockProperties.IsOpaque(id);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false; int w = index >> 6; int b = index & 63; return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
            }

            // Cache UVs per block id/face
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

            uint vb = vertBase; // local copy
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

            // Iterate each sparse voxel and test 6 neighbors.
            for (int i = 0; i < indices.Length; i++)
            {
                int li = indices[i];
                ushort block = blocks[i];
                if (block == 0) continue;

                DecodeIndex(li, S, out int lx, out int ly, out int lz);
                int wx = baseX + lx;
                int wy = baseY + ly;
                int wz = baseZ + lz;
                bool opaqueSelf = Opaque(block);

                // Helper to get neighbor inside current section (sparse map) else 0.
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                ushort LocalNeighbor(int nlx, int nly, int nlz)
                {
                    if ((uint)nlx >= (uint)S || (uint)nly >= (uint)S || (uint)nlz >= (uint)S) return 0;
                    int nli = ((nlz * S + nlx) * S) + nly;
                    return localMap.TryGetValue(nli, out var bid) ? bid : (ushort)0;
                }

                // -X
                if (lx == 0)
                {
                    if (wx == 0)
                    {
                        bool covered = PlaneBit(planeNegX, wz * maxY + wy);
                        if (!covered || !opaqueSelf) EmitFace(Faces.LEFT, wx, wy, wz, block);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx - 1, wy, wz);
                        if (!(opaqueSelf && Opaque(nb))) EmitFace(Faces.LEFT, wx, wy, wz, block);
                    }
                }
                else
                {
                    ushort nb = LocalNeighbor(lx - 1, ly, lz);
                    if (nb == 0 || !(opaqueSelf && Opaque(nb))) EmitFace(Faces.LEFT, wx, wy, wz, block);
                }

                // +X
                if (lx == S - 1)
                {
                    if (wx == maxX - 1)
                    {
                        bool covered = PlaneBit(planePosX, wz * maxY + wy);
                        if (!covered || !opaqueSelf) EmitFace(Faces.RIGHT, wx, wy, wz, block);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx + 1, wy, wz);
                        if (!(opaqueSelf && Opaque(nb))) EmitFace(Faces.RIGHT, wx, wy, wz, block);
                    }
                }
                else
                {
                    ushort nb = LocalNeighbor(lx + 1, ly, lz);
                    if (nb == 0 || !(opaqueSelf && Opaque(nb))) EmitFace(Faces.RIGHT, wx, wy, wz, block);
                }

                // -Y
                if (ly == 0)
                {
                    if (wy == 0)
                    {
                        bool covered = PlaneBit(planeNegY, wx * maxZ + wz);
                        if (!covered || !opaqueSelf) EmitFace(Faces.BOTTOM, wx, wy, wz, block);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy - 1, wz);
                        if (!(opaqueSelf && Opaque(nb))) EmitFace(Faces.BOTTOM, wx, wy, wz, block);
                    }
                }
                else
                {
                    ushort nb = LocalNeighbor(lx, ly - 1, lz);
                    if (nb == 0 || !(opaqueSelf && Opaque(nb))) EmitFace(Faces.BOTTOM, wx, wy, wz, block);
                }

                // +Y
                if (ly == S - 1)
                {
                    if (wy == maxY - 1)
                    {
                        bool covered = PlaneBit(planePosY, wx * maxZ + wz);
                        if (!covered || !opaqueSelf) EmitFace(Faces.TOP, wx, wy, wz, block);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy + 1, wz);
                        if (!(opaqueSelf && Opaque(nb))) EmitFace(Faces.TOP, wx, wy, wz, block);
                    }
                }
                else
                {
                    ushort nb = LocalNeighbor(lx, ly + 1, lz);
                    if (nb == 0 || !(opaqueSelf && Opaque(nb))) EmitFace(Faces.TOP, wx, wy, wz, block);
                }

                // -Z
                if (lz == 0)
                {
                    if (wz == 0)
                    {
                        bool covered = PlaneBit(planeNegZ, wx * maxY + wy);
                        if (!covered || !opaqueSelf) EmitFace(Faces.BACK, wx, wy, wz, block);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy, wz - 1);
                        if (!(opaqueSelf && Opaque(nb))) EmitFace(Faces.BACK, wx, wy, wz, block);
                    }
                }
                else
                {
                    ushort nb = LocalNeighbor(lx, ly, lz - 1);
                    if (nb == 0 || !(opaqueSelf && Opaque(nb))) EmitFace(Faces.BACK, wx, wy, wz, block);
                }

                // +Z
                if (lz == S - 1)
                {
                    if (wz == maxZ - 1)
                    {
                        bool covered = PlaneBit(planePosZ, wx * maxY + wy);
                        if (!covered || !opaqueSelf) EmitFace(Faces.FRONT, wx, wy, wz, block);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy, wz + 1);
                        if (!(opaqueSelf && Opaque(nb))) EmitFace(Faces.FRONT, wx, wy, wz, block);
                    }
                }
                else
                {
                    ushort nb = LocalNeighbor(lx, ly, lz + 1);
                    if (nb == 0 || !(opaqueSelf && Opaque(nb))) EmitFace(Faces.FRONT, wx, wy, wz, block);
                }
            }

            vertBase = vb; // write back
        }
    }
}

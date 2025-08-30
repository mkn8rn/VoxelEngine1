using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Instanced emission for sparse sections (Kind=2). Returns true when handled.
        private bool EmitSparseSectionInstances(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
            List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            if (desc.SparseIndices == null || desc.SparseBlocks == null || desc.SparseIndices.Length == 0)
                return true; // nothing to emit

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // Map linear index -> block id for quick local neighbor queries
            var localMap = new Dictionary<int, ushort>(desc.SparseIndices.Length * 2);
            var indices = desc.SparseIndices;
            var blocks = desc.SparseBlocks;
            for (int i = 0; i < indices.Length; i++)
                localMap[indices[i]] = blocks[i];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void DecodeIndex(int li, out int lx, out int ly, out int lz)
            { ly = li & 15; int rest = li >> 4; lx = rest & 15; lz = rest >> 4; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ushort Local(int lx, int ly, int lz, Dictionary<int, ushort> map)
            {
                if ((uint)lx > 15 || (uint)ly > 15 || (uint)lz > 15) return 0;
                int li = ((lz * 16 + lx) * 16) + ly;
                return map.TryGetValue(li, out var v) ? v : (ushort)0;
            }

            for (int i = 0; i < indices.Length; i++)
            {
                int li = indices[i]; ushort block = blocks[i]; if (block == 0) continue;
                DecodeIndex(li, out int lx, out int ly, out int lz);
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                // LEFT (-X)
                if (lx == 0)
                {
                    if (wx == 0)
                    {
                        if (!PlaneBit(planeNegX, wz * maxY + wy))
                            EmitFaceInstance(block, 0, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                    else if (GetBlock(wx - 1, wy, wz) == 0)
                        EmitFaceInstance(block, 0, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                }
                else if (Local(lx - 1, ly, lz, localMap) == 0)
                    EmitFaceInstance(block, 0, wx, wy, wz, offsetList, tileIndexList, faceDirList);

                // RIGHT (+X)
                if (lx == 15)
                {
                    if (wx == maxX - 1)
                    {
                        if (!PlaneBit(planePosX, wz * maxY + wy))
                            EmitFaceInstance(block, 1, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                    else if (GetBlock(wx + 1, wy, wz) == 0)
                        EmitFaceInstance(block, 1, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                }
                else if (Local(lx + 1, ly, lz, localMap) == 0)
                    EmitFaceInstance(block, 1, wx, wy, wz, offsetList, tileIndexList, faceDirList);

                // BOTTOM (-Y)
                if (ly == 0)
                {
                    if (wy == 0)
                    {
                        if (!PlaneBit(planeNegY, wx * maxZ + wz))
                            EmitFaceInstance(block, 2, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                    else if (GetBlock(wx, wy - 1, wz) == 0)
                        EmitFaceInstance(block, 2, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                }
                else if (Local(lx, ly - 1, lz, localMap) == 0)
                    EmitFaceInstance(block, 2, wx, wy, wz, offsetList, tileIndexList, faceDirList);

                // TOP (+Y)
                if (ly == 15)
                {
                    if (wy == maxY - 1)
                    {
                        if (!PlaneBit(planePosY, wx * maxZ + wz))
                            EmitFaceInstance(block, 3, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                    else if (GetBlock(wx, wy + 1, wz) == 0)
                        EmitFaceInstance(block, 3, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                }
                else if (Local(lx, ly + 1, lz, localMap) == 0)
                    EmitFaceInstance(block, 3, wx, wy, wz, offsetList, tileIndexList, faceDirList);

                // BACK (-Z)
                if (lz == 0)
                {
                    if (wz == 0)
                    {
                        if (!PlaneBit(planeNegZ, wx * maxY + wy))
                            EmitFaceInstance(block, 4, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                    else if (GetBlock(wx, wy, wz - 1) == 0)
                        EmitFaceInstance(block, 4, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                }
                else if (Local(lx, ly, lz - 1, localMap) == 0)
                    EmitFaceInstance(block, 4, wx, wy, wz, offsetList, tileIndexList, faceDirList);

                // FRONT (+Z)
                if (lz == 15)
                {
                    if (wz == maxZ - 1)
                    {
                        if (!PlaneBit(planePosZ, wx * maxY + wy))
                            EmitFaceInstance(block, 5, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                    else if (GetBlock(wx, wy, wz + 1) == 0)
                        EmitFaceInstance(block, 5, wx, wy, wz, offsetList, tileIndexList, faceDirList);
                }
                else if (Local(lx, ly, lz + 1, localMap) == 0)
                    EmitFaceInstance(block, 5, wx, wy, wz, offsetList, tileIndexList, faceDirList);
            }
            return true;
        }
    }
}

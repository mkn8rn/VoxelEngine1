using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_INF.Loaders;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        /// Emits face instances for a Sparse section (Kind==2) using an explicit list of occupied voxel indices.
        /// Preconditions:
        ///   - desc.SparseIndices and desc.SparseBlocks contain matching non‑empty entries
        /// Steps:
        ///  1. Build a local dictionary (li -> block) for O(1) sparse neighbor existence checks within the section.
        ///  2. For each sparse voxel:
        ///       a. Decode linear index to (lx,ly,lz) (DecodeIndex) and compute world (wx,wy,wz).
        ///       b. For each of the 6 directions, test exposure:
        ///          - Boundary to world: consult corresponding world neighbor plane (PlaneBit) to suppress hidden faces.
        ///          - Interior neighbor: check local sparse map (Local) else fall back to GetBlock for cross‑section neighbor.
        ///       c. When neighbor is air / absent, emit that face using cached tile index (TileIndexCache + EmitOneInstance).
        ///  3. Repeat until all sparse voxels processed.
        /// Transparent (non‑opaque) blocks are skipped; transparent neighbors are treated as non‑occluding (same as air).
        /// Returns true (always handled) unless sparse arrays are null/empty (early no‑op true).
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

            // Local fast lookup inside 16^3 section bounds.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ushort Local(int lx, int ly, int lz, Dictionary<int, ushort> map)
            {
                if ((uint)lx > 15 || (uint)ly > 15 || (uint)lz > 15) return 0;
                int li = ((lz * 16 + lx) * 16) + ly;
                return map.TryGetValue(li, out var v) ? v : (ushort)0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool IsAirOrTransparent(ushort bid) => bid == 0 || !TerrainLoader.IsOpaque(bid);

            // Tile index cache (shared helper) to avoid recomputing atlas lookups for repeated block ids per face direction.
            var tileCache = new TileIndexCache();

            for (int i = 0; i < indices.Length; i++)
            {
                int li = indices[i]; ushort block = blocks[i];
                if (block == 0) continue; // skip air
                if (!TerrainLoader.IsOpaque(block)) continue; // skip transparent (non-opaque) blocks entirely

                DecodeIndex(li, out int lx, out int ly, out int lz); // shared helper from common
                int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                // LEFT (-X)
                if (lx == 0)
                {
                    if (wx == 0)
                    {
                        if (!PlaneBit(planeNegX, wz * maxY + wy))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 0), 0, offsetList, tileIndexList, faceDirList);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx - 1, wy, wz);
                        if (IsAirOrTransparent(nb))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 0), 0, offsetList, tileIndexList, faceDirList);
                    }
                }
                else if (IsAirOrTransparent(Local(lx - 1, ly, lz, localMap)))
                    EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 0), 0, offsetList, tileIndexList, faceDirList);

                // RIGHT (+X)
                if (lx == 15)
                {
                    if (wx == maxX - 1)
                    {
                        if (!PlaneBit(planePosX, wz * maxY + wy))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 1), 1, offsetList, tileIndexList, faceDirList);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx + 1, wy, wz);
                        if (IsAirOrTransparent(nb))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 1), 1, offsetList, tileIndexList, faceDirList);
                    }
                }
                else if (IsAirOrTransparent(Local(lx + 1, ly, lz, localMap)))
                    EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 1), 1, offsetList, tileIndexList, faceDirList);

                // BOTTOM (-Y)
                if (ly == 0)
                {
                    if (wy == 0)
                    {
                        if (!PlaneBit(planeNegY, wx * maxZ + wz))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 2), 2, offsetList, tileIndexList, faceDirList);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy - 1, wz);
                        if (IsAirOrTransparent(nb))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 2), 2, offsetList, tileIndexList, faceDirList);
                    }
                }
                else if (IsAirOrTransparent(Local(lx, ly - 1, lz, localMap)))
                    EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 2), 2, offsetList, tileIndexList, faceDirList);

                // TOP (+Y)
                if (ly == 15)
                {
                    if (wy == maxY - 1)
                    {
                        if (!PlaneBit(planePosY, wx * maxZ + wz))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 3), 3, offsetList, tileIndexList, faceDirList);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy + 1, wz);
                        if (IsAirOrTransparent(nb))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 3), 3, offsetList, tileIndexList, faceDirList);
                    }
                }
                else if (IsAirOrTransparent(Local(lx, ly + 1, lz, localMap)))
                    EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 3), 3, offsetList, tileIndexList, faceDirList);

                // BACK (-Z)
                if (lz == 0)
                {
                    if (wz == 0)
                    {
                        if (!PlaneBit(planeNegZ, wx * maxY + wy))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 4), 4, offsetList, tileIndexList, faceDirList);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy, wz - 1);
                        if (IsAirOrTransparent(nb))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 4), 4, offsetList, tileIndexList, faceDirList);
                    }
                }
                else if (IsAirOrTransparent(Local(lx, ly, lz - 1, localMap)))
                    EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 4), 4, offsetList, tileIndexList, faceDirList);

                // FRONT (+Z)
                if (lz == 15)
                {
                    if (wz == maxZ - 1)
                    {
                        if (!PlaneBit(planePosZ, wx * maxY + wy))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 5), 5, offsetList, tileIndexList, faceDirList);
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy, wz + 1);
                        if (IsAirOrTransparent(nb))
                            EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 5), 5, offsetList, tileIndexList, faceDirList);
                    }
                }
                else if (IsAirOrTransparent(Local(lx, ly, lz + 1, localMap)))
                    EmitOneInstance(wx, wy, wz, tileCache.Get(atlas, block, 5), 5, offsetList, tileIndexList, faceDirList);
            }
            return true;
        }
    }
}

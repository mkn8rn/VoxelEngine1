using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GFX.Models;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        /// Emits boundary face instances for a Uniform section (Kind==1) containing a single non‑air block id.
        /// Only faces on the outer surface of the section that are exposed to air or world boundary are emitted.
        /// Steps:
        ///  1. Compute per‑face tile indices (PrecomputePerFaceTiles); enable single‑tile fast path if all equal.
        ///  2. For each of the 6 faces:
        ///       a. If chunk face lies on world border, consult the corresponding world neighbor plane bitset to skip fully occluded spans.
        ///       b. Otherwise fetch neighbor section descriptor; if neighbor is empty or air‑uniform, emit whole plane quickly.
        ///       c. If neighbor potentially partial, attempt mask‑based visibility using neighbor face bitsets; fall back to per‑voxel
        ///          occupancy tests (NeighborVoxelSolid) when bitsets absent.
        ///  3. Emit every visible boundary voxel using EmitOneInstance (plane loops or mask-driven sparse loops).
        /// Returns true (uniform path always handled; empty uniform returns true with no output).
        private bool EmitUniformSectionInstances(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
            List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            ushort block = desc.UniformBlockId; if (block == 0) return true; // treat as empty
            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // Precompute clamped end ranges to eliminate per-iteration bounds checks in loops.
            int endX = baseX + S; if (endX > maxX) endX = maxX;
            int endY = baseY + S; if (endY > maxY) endY = maxY;
            int endZ = baseZ + S; if (endZ > maxZ) endZ = maxZ;

            // Precompute per-face tile indices once for this uniform block; keep a fast path when all 6 faces match.
            // Replaces local ComputeTileIndex with shared PrecomputePerFaceTiles helper.
            Span<uint> faceTiles = stackalloc uint[6];
            PrecomputePerFaceTiles(block, out bool sameTileAllFaces, out uint singleTileIndex, faceTiles);
            uint rTileNX = sameTileAllFaces ? singleTileIndex : faceTiles[0];
            uint rTilePX = sameTileAllFaces ? singleTileIndex : faceTiles[1];
            uint rTileNY = sameTileAllFaces ? singleTileIndex : faceTiles[2];
            uint rTilePY = sameTileAllFaces ? singleTileIndex : faceTiles[3];
            uint rTileNZ = sameTileAllFaces ? singleTileIndex : faceTiles[4];
            uint rTilePZ = sameTileAllFaces ? singleTileIndex : faceTiles[5];

            // Helper to emit entire face plane of SxS blocks using shared EmitOneInstance to reduce code duplication
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitPlaneSingleTile(int faceDir, int wxFixed, int wyFixed, int wzFixed, uint tileIndex)
            {
                // axis: derived from faceDir (0/1: x fixed, varying y,z) (2/3: y fixed, varying x,z) (4/5: z fixed, varying x,y)
                if (faceDir == 0 || faceDir == 1)
                {
                    for (int y = baseY; y < endY; y++)
                        for (int z = baseZ; z < endZ; z++)
                            EmitOneInstance(wxFixed, y, z, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                }
                else if (faceDir == 2 || faceDir == 3)
                {
                    for (int x = baseX; x < endX; x++)
                        for (int z = baseZ; z < endZ; z++)
                            EmitOneInstance(x, wyFixed, z, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                }
                else
                {
                    for (int x = baseX; x < endX; x++)
                        for (int y = baseY; y < endY; y++)
                            EmitOneInstance(x, y, wzFixed, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                }
            }

            // Neighbor section queries for whole-face occlusion
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ref SectionPrerenderDesc Neighbor(int nsx, int nsy, int nsz)
            {
                int idx = ((nsx * data.sectionsY) + nsy) * data.sectionsZ + nsz;
                return ref data.SectionDescs[idx];
            }

            // Helper: treat neighbor as fully solid if it is uniform non-air OR a single-id packed (Kind 4) fully filled OR a multi-packed (Kind 5) with palette indicating full occupancy (NonAirCount==4096).
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborFullySolid(ref SectionPrerenderDesc n)
            {
                if (n.Kind == 1 && n.UniformBlockId != 0) return true; // uniform solid
                if ((n.Kind == 4 || n.Kind == 5) && n.NonAirCount == 4096) return true;
                return false;
            }

            // Replace NeighborFaceBit with FaceBitIsSet helper (selection via faceDir & planeIndex)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborFaceBit(ref SectionPrerenderDesc n, int faceDir, int planeIndex)
                => FaceBitIsSet(ref n, faceDir, planeIndex);

            // Replace NeighborVoxelOccupied with NeighborVoxelSolid (generic occupancy test for neighbor section types)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborVoxelOccupied(ref SectionPrerenderDesc n, int lx, int ly, int lz)
                => NeighborVoxelSolid(ref n, lx, ly, lz);

            // Bitset-driven emission helpers for partial neighbor occlusion (mask present). Visible bits are mask==0.
            // Updated to use EmitOneInstance instead of local EmitOne.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitVisibleByMask_XFace(int faceDir, ulong[] neighborMask, uint tileIndex, int xFixed)
            {
                if (neighborMask == null) return;
                // plane index mapping: idx = z*16 + y
                for (int wi = 0; wi < neighborMask.Length; wi++)
                {
                    ulong visible = ~neighborMask[wi];
                    while (visible != 0)
                    {
                        int bit = System.Numerics.BitOperations.TrailingZeroCount(visible);
                        visible &= visible - 1;
                        int idx = (wi << 6) + bit; if (idx >= 256) continue; // guard if longer
                        int z = idx >> 4; int y = idx & 15;
                        EmitOneInstance(xFixed, baseY + y, baseZ + z, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitVisibleByMask_YFace(int faceDir, ulong[] neighborMask, uint tileIndex, int yFixed)
            {
                if (neighborMask == null) return;
                // plane index mapping: idx = x*16 + z
                for (int wi = 0; wi < neighborMask.Length; wi++)
                {
                    ulong visible = ~neighborMask[wi];
                    while (visible != 0)
                    {
                        int bit = System.Numerics.BitOperations.TrailingZeroCount(visible);
                        visible &= visible - 1;
                        int idx = (wi << 6) + bit; if (idx >= 256) continue;
                        int x = idx >> 4; int z = idx & 15;
                        EmitOneInstance(baseX + x, yFixed, baseZ + z, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitVisibleByMask_ZFace(int faceDir, ulong[] neighborMask, uint tileIndex, int zFixed)
            {
                if (neighborMask == null) return;
                // plane index mapping: idx = x*16 + y
                for (int wi = 0; wi < neighborMask.Length; wi++)
                {
                    ulong visible = ~neighborMask[wi];
                    while (visible != 0)
                    {
                        int bit = System.Numerics.BitOperations.TrailingZeroCount(visible);
                        visible &= visible - 1;
                        int idx = (wi << 6) + bit; if (idx >= 256) continue;
                        int x = idx >> 4; int y = idx & 15;
                        EmitOneInstance(baseX + x, baseY + y, zFixed, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                    }
                }
            }

            // Helper for world-edge plane quick skip if fully occluded in the SxS window.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool IsWorldPlaneFullySet(ulong[] plane, int startA, int startB, int countA, int countB, int strideB)
            {
                if (plane == null) return false;
                for (int a = 0; a < countA; a++)
                {
                    int baseIndex = (startA + a) * strideB + startB;
                    int remaining = countB;
                    int idx = baseIndex;
                    while (remaining-- > 0)
                    {
                        int w = idx >> 6; int b = idx & 63; if (w >= plane.Length) return false;
                        if ((plane[w] & (1UL << b)) == 0UL) return false; // found a hole
                        idx++;
                    }
                }
                return true;
            }

            // LEFT
            if (sx == 0)
            {
                if (baseX == 0 && IsWorldPlaneFullySet(data.NeighborPlaneNegX, baseZ, baseY, endZ - baseZ, endY - baseY, maxY))
                {
                    // fully hidden
                }
                else
                {
                    for (int z = baseZ; z < endZ; z++)
                        for (int y = baseY; y < endY; y++)
                            if (!(baseX == 0 && PlaneBit(data.NeighborPlaneNegX, z * maxY + y)))
                                EmitOneInstance(baseX, y, z, rTileNX, 0, offsetList, tileIndexList, faceDirList);
                }
            }
            else
            {
                ref var n = ref Neighbor(sx - 1, sy, sz);
                if (n.Kind == 0 || (n.Kind == 1 && n.UniformBlockId == 0))
                {
                    EmitPlaneSingleTile(0, baseX, 0, 0, rTileNX);
                }
                else
                {
                    bool fullOcclude = NeighborFullySolid(ref n);
                    if (!fullOcclude)
                    {
                        if (n.FacePosXBits != null)
                        {
                            EmitVisibleByMask_XFace(0, n.FacePosXBits, rTileNX, baseX);
                        }
                        else
                        {
                            for (int z = 0; z < S; z++)
                                for (int y = 0; y < S; y++)
                                    if (!NeighborVoxelOccupied(ref n, 15, y, z)) EmitOneInstance(baseX, baseY + y, baseZ + z, rTileNX, 0, offsetList, tileIndexList, faceDirList);
                        }
                    }
                }
            }
            // RIGHT
            int wxRight = baseX + S - 1;
            if (sx == data.sectionsX - 1)
            {
                if (wxRight == maxX - 1 && IsWorldPlaneFullySet(data.NeighborPlanePosX, baseZ, baseY, endZ - baseZ, endY - baseY, maxY))
                {
                    // fully hidden
                }
                else
                {
                    for (int z = baseZ; z < endZ; z++)
                        for (int y = baseY; y < endY; y++)
                            if (!(wxRight == maxX - 1 && PlaneBit(data.NeighborPlanePosX, z * maxY + y)))
                                EmitOneInstance(wxRight, y, z, rTilePX, 1, offsetList, tileIndexList, faceDirList);
                }
            }
            else
            {
                ref var n = ref Neighbor(sx + 1, sy, sz);
                if (n.Kind == 0 || (n.Kind == 1 && n.UniformBlockId == 0))
                {
                    EmitPlaneSingleTile(1, wxRight, 0, 0, rTilePX);
                }
                else
                {
                    bool fullOcclude = NeighborFullySolid(ref n);
                    if (!fullOcclude)
                    {
                        if (n.FaceNegXBits != null)
                        {
                            EmitVisibleByMask_XFace(1, n.FaceNegXBits, rTilePX, wxRight);
                        }
                        else
                        {
                            for (int z = 0; z < S; z++)
                                for (int y = 0; y < S; y++)
                                    if (!NeighborVoxelOccupied(ref n, 0, y, z)) EmitOneInstance(wxRight, baseY + y, baseZ + z, rTilePX, 1, offsetList, tileIndexList, faceDirList);
                        }
                    }
                }
            }
            // BOTTOM
            if (sy == 0)
            {
                if (baseY == 0 && IsWorldPlaneFullySet(data.NeighborPlaneNegY, baseX, baseZ, endX - baseX, endZ - baseZ, maxZ))
                {
                    // fully hidden
                }
                else
                {
                    for (int x = baseX; x < endX; x++)
                        for (int z = baseZ; z < endZ; z++)
                            if (!(baseY == 0 && PlaneBit(data.NeighborPlaneNegY, x * maxZ + z)))
                                EmitOneInstance(x, baseY, z, rTileNY, 2, offsetList, tileIndexList, faceDirList);
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy - 1, sz);
                bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    if (n.FacePosYBits != null)
                    {
                        EmitVisibleByMask_YFace(2, n.FacePosYBits, rTileNY, baseY);
                    }
                    else
                    {
                        for (int x = 0; x < S; x++)
                            for (int z = 0; z < S; z++)
                                if (!NeighborVoxelOccupied(ref n, x, 15, z)) EmitOneInstance(baseX + x, baseY, baseZ + z, rTileNY, 2, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            // TOP
            int wyTop = baseY + S - 1;
            if (sy == data.sectionsY - 1)
            {
                if (wyTop == maxY - 1 && IsWorldPlaneFullySet(data.NeighborPlanePosY, baseX, baseZ, endX - baseX, endZ - baseZ, maxZ))
                {
                    // fully hidden
                }
                else
                {
                    for (int x = baseX; x < endX; x++)
                        for (int z = baseZ; z < endZ; z++)
                            if (!(wyTop == maxY - 1 && PlaneBit(data.NeighborPlanePosY, x * maxZ + z)))
                                EmitOneInstance(x, wyTop, z, rTilePY, 3, offsetList, tileIndexList, faceDirList);
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy + 1, sz);
                bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    if (n.FaceNegYBits != null)
                    {
                        EmitVisibleByMask_YFace(3, n.FaceNegYBits, rTilePY, wyTop);
                    }
                    else
                    {
                        for (int x = 0; x < S; x++)
                            for (int z = 0; z < S; z++)
                                if (!NeighborVoxelOccupied(ref n, x, 0, z)) EmitOneInstance(baseX + x, wyTop, baseZ + z, rTilePY, 3, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            // BACK
            if (sz == 0)
            {
                if (baseZ == 0 && IsWorldPlaneFullySet(data.NeighborPlaneNegZ, baseX, baseY, endX - baseX, endY - baseY, maxY))
                {
                    // fully hidden
                }
                else
                {
                    for (int x = baseX; x < endX; x++)
                        for (int y = baseY; y < endY; y++)
                            if (!(baseZ == 0 && PlaneBit(data.NeighborPlaneNegZ, x * maxY + y)))
                                EmitOneInstance(x, y, baseZ, rTileNZ, 4, offsetList, tileIndexList, faceDirList);
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy, sz - 1); bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    if (n.FacePosZBits != null)
                    {
                        EmitVisibleByMask_ZFace(4, n.FacePosZBits, rTileNZ, baseZ);
                    }
                    else
                    {
                        for (int x = 0; x < S; x++)
                            for (int y = 0; y < S; y++)
                                if (!NeighborVoxelOccupied(ref n, x, y, 15)) EmitOneInstance(baseX + x, baseY + y, baseZ, rTileNZ, 4, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            // FRONT
            int wzFront = baseZ + S - 1;
            if (sz == data.sectionsZ - 1)
            {
                if (wzFront == maxZ - 1 && IsWorldPlaneFullySet(data.NeighborPlanePosZ, baseX, baseY, endX - baseX, endY - baseY, maxY))
                {
                    // fully hidden
                }
                else
                {
                    for (int x = baseX; x < endX; x++)
                        for (int y = baseY; y < endY; y++)
                            if (!(wzFront == maxZ - 1 && PlaneBit(data.NeighborPlanePosZ, x * maxY + y)))
                                EmitOneInstance(x, y, wzFront, rTilePZ, 5, offsetList, tileIndexList, faceDirList);
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy, sz + 1); bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    if (n.FaceNegZBits != null)
                    {
                        EmitVisibleByMask_ZFace(5, n.FaceNegZBits, rTilePZ, wzFront);
                    }
                    else
                    {
                        for (int x = 0; x < S; x++)
                            for (int y = 0; y < S; y++)
                                if (!NeighborVoxelOccupied(ref n, x, y, 0)) EmitOneInstance(baseX + x, baseY + y, wzFront, rTilePZ, 5, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            return true;
        }
    }
}

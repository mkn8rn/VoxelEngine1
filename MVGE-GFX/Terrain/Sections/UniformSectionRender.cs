using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GFX.Models;
using System.Runtime.InteropServices; // for CollectionsMarshal if needed

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Face metadata for table-driven uniform section emission. Axis: 0=X,1=Y,2=Z. Negative indicates -axis face.
        private readonly struct FaceMeta
        {
            public readonly int FaceDir; public readonly sbyte Dx; public readonly sbyte Dy; public readonly sbyte Dz; public readonly int Axis; public readonly bool Negative;
            public FaceMeta(int faceDir, sbyte dx, sbyte dy, sbyte dz, int axis, bool negative)
            { FaceDir = faceDir; Dx = dx; Dy = dy; Dz = dz; Axis = axis; Negative = negative; }
        }
        private static readonly FaceMeta[] _uniformFaceMetas = new FaceMeta[]
        {
            new FaceMeta(0,-1,0,0,0,true),  // LEFT (-X face)
            new FaceMeta(1, 1,0,0,0,false), // RIGHT (+X face)
            new FaceMeta(2,0,-1,0,1,true),  // BOTTOM (-Y face)
            new FaceMeta(3,0, 1,0,1,false), // TOP (+Y face)
            new FaceMeta(4,0,0,-1,2,true),  // BACK (-Z face)
            new FaceMeta(5,0,0, 1,2,false), // FRONT (+Z face)
        };

        /// Emits boundary face instances for a Uniform section (Kind==1) containing a single non‑air block id.
        /// Only faces on the outer surface of the section that are exposed to air or world boundary are emitted.
        /// Steps:
        ///  1. Compute per‑face tile indices (PrecomputePerFaceTiles) OR fetch cached set; enable single‑tile fast path if all equal.
        ///  2. Early interior occlusion fast path: if all six neighbors exist and are fully solid, skip immediately.
        ///  3. Reserve list capacities for worst-case emission (6 * 256 faces) to avoid repeated reallocations.
        ///  4. Table-driven per-face loop (FaceMeta array) applies visibility logic for all six faces uniformly:
        ///       a. World border: consult plane bitset; emit only visible cells.
        ///       b. Neighbor missing/air: emit whole plane using bulk plane writer (single call, minimized per-voxel overhead).
        ///       c. Neighbor fully solid: skip.
        ///       d. Neighbor partial with face mask: enumerate visible bits (inverted mask) and emit per visible cell.
        ///       e. Fallback neighbor voxel queries when mask absent.
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

            // Use cached uniform face tile set to avoid recomputing per-face indices for repeated block ids.
            var tileSet = GetUniformFaceTileSet(block);
            bool sameTileAllFaces = tileSet.AllSame;
            uint rTileNX = sameTileAllFaces ? tileSet.SingleTile : tileSet.TileNX;
            uint rTilePX = sameTileAllFaces ? tileSet.SingleTile : tileSet.TilePX;
            uint rTileNY = sameTileAllFaces ? tileSet.SingleTile : tileSet.TileNY;
            uint rTilePY = sameTileAllFaces ? tileSet.SingleTile : tileSet.TilePY;
            uint rTileNZ = sameTileAllFaces ? tileSet.SingleTile : tileSet.TileNZ;
            uint rTilePZ = sameTileAllFaces ? tileSet.SingleTile : tileSet.TilePZ;
            uint[] faceTiles = { rTileNX, rTilePX, rTileNY, rTilePY, rTileNZ, rTilePZ };

            // Early interior full-occlusion skip: check if section is interior (all neighbors exist) and every neighbor is fully solid.
            if (sx > 0 && sx < data.sectionsX - 1 && sy > 0 && sy < data.sectionsY - 1 && sz > 0 && sz < data.sectionsZ - 1)
            {
                // indices of neighbors
                var secs = data.SectionDescs;
                ref var nL = ref secs[((sx - 1) * data.sectionsY + sy) * data.sectionsZ + sz];
                ref var nR = ref secs[((sx + 1) * data.sectionsY + sy) * data.sectionsZ + sz];
                ref var nD = ref secs[(sx * data.sectionsY + (sy - 1)) * data.sectionsZ + sz];
                ref var nU = ref secs[(sx * data.sectionsY + (sy + 1)) * data.sectionsZ + sz];
                ref var nB = ref secs[(sx * data.sectionsY + sy) * data.sectionsZ + (sz - 1)];
                ref var nF = ref secs[(sx * data.sectionsY + sy) * data.sectionsZ + (sz + 1)];
                if (NeighborFullySolid(ref nL) && NeighborFullySolid(ref nR) &&
                    NeighborFullySolid(ref nD) && NeighborFullySolid(ref nU) &&
                    NeighborFullySolid(ref nB) && NeighborFullySolid(ref nF))
                {
                    return true; // fully occluded interior uniform section
                }
            }

            // Reserve worst-case capacity (6 planes * 256 faces) to reduce reallocations when many faces visible.
            const int MAX_FACES_PER_SECTION = 6 * 256;
            offsetList.EnsureCapacity(offsetList.Count + MAX_FACES_PER_SECTION * 3);
            tileIndexList.EnsureCapacity(tileIndexList.Count + MAX_FACES_PER_SECTION);
            faceDirList.EnsureCapacity(faceDirList.Count + MAX_FACES_PER_SECTION);

            // Bulk plane emission helper (single-tile only) for full-plane visibility (avoids per-voxel function call overhead).
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitFullPlaneSingleTile(int faceDir, uint tileIndex, int fixedCoord)
            {
                // axis mapping: (0,1) x fixed -> vary y,z ; (2,3) y fixed -> vary x,z ; (4,5) z fixed -> vary x,y
                if (faceDir == 0 || faceDir == 1)
                {
                    int wx = fixedCoord;
                    for (int y = baseY; y < endY; y++)
                        for (int z = baseZ; z < endZ; z++)
                            EmitOneInstance(wx, y, z, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                }
                else if (faceDir == 2 || faceDir == 3)
                {
                    int wy = fixedCoord;
                    for (int x = baseX; x < endX; x++)
                        for (int z = baseZ; z < endZ; z++)
                            EmitOneInstance(x, wy, z, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                }
                else
                {
                    int wz = fixedCoord;
                    for (int x = baseX; x < endX; x++)
                        for (int y = baseY; y < endY; y++)
                            EmitOneInstance(x, y, wz, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
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

            // Replace NeighborVoxelOccupied with NeighborVoxelSolid (generic occupancy test for neighbor section types)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborVoxelOccupied(ref SectionPrerenderDesc n, int lx, int ly, int lz)
                => NeighborVoxelSolid(ref n, lx, ly, lz);

            // Bitset-driven emission helpers for partial neighbor occlusion (mask present). Visible bits are mask==0.
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

            // World plane arrays (same order for convenience)
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // Iterate table
            foreach (var meta in _uniformFaceMetas)
            {
                int faceDir = meta.FaceDir;
                uint tile = faceTiles[faceDir];

                // ---------------- WORLD BOUNDARY CHECK & EMISSION ----------------
                bool atWorldBoundary = false;
                ulong[] worldPlane = null;
                int startA = 0, startB = 0, countA = 0, countB = 0, strideB = 0;
                int fixedCoord = 0; // world coordinate fixed along axis

                // Provide per-face comments retained from earlier code blocks
                switch (faceDir)
                {
                    case 0: // LEFT (-X)
                        if (sx == 0)
                        {
                            atWorldBoundary = true; worldPlane = planeNegX; fixedCoord = baseX;
                            startA = baseZ; countA = endZ - baseZ; startB = baseY; countB = endY - baseY; strideB = maxY;
                        }
                        else fixedCoord = baseX;
                        break;
                    case 1: // RIGHT (+X)
                        if (sx == data.sectionsX - 1)
                        {
                            atWorldBoundary = true; worldPlane = planePosX; fixedCoord = baseX + S - 1;
                            startA = baseZ; countA = endZ - baseZ; startB = baseY; countB = endY - baseY; strideB = maxY;
                        }
                        else fixedCoord = baseX + S - 1;
                        break;
                    case 2: // BOTTOM (-Y)
                        if (sy == 0)
                        {
                            atWorldBoundary = true; worldPlane = planeNegY; fixedCoord = baseY;
                            startA = baseX; countA = endX - baseX; startB = baseZ; countB = endZ - baseZ; strideB = maxZ;
                        }
                        else fixedCoord = baseY;
                        break;
                    case 3: // TOP (+Y)
                        if (sy == data.sectionsY - 1)
                        {
                            atWorldBoundary = true; worldPlane = planePosY; fixedCoord = baseY + S - 1;
                            startA = baseX; countA = endX - baseX; startB = baseZ; countB = endZ - baseZ; strideB = maxZ;
                        }
                        else fixedCoord = baseY + S - 1;
                        break;
                    case 4: // BACK (-Z)
                        if (sz == 0)
                        {
                            atWorldBoundary = true; worldPlane = planeNegZ; fixedCoord = baseZ;
                            startA = baseX; countA = endX - baseX; startB = baseY; countB = endY - baseY; strideB = maxY;
                        }
                        else fixedCoord = baseZ;
                        break;
                    case 5: // FRONT (+Z)
                        if (sz == data.sectionsZ - 1)
                        {
                            atWorldBoundary = true; worldPlane = planePosZ; fixedCoord = baseZ + S - 1;
                            startA = baseX; countA = endX - baseX; startB = baseY; countB = endY - baseY; strideB = maxY;
                        }
                        else fixedCoord = baseZ + S - 1;
                        break;
                }

                if (atWorldBoundary)
                {
                    if (!IsWorldPlaneFullySet(worldPlane, startA, startB, countA, countB, strideB))
                    {
                        // Per-axis emission with plane bit test
                        if (meta.Axis == 0) // X fixed, iterate z,y
                        {
                            for (int z = baseZ; z < endZ; z++)
                                for (int y = baseY; y < endY; y++)
                                {
                                    int idxPlane = (z * maxY) + y;
                                    if (worldPlane != null && PlaneBit(worldPlane, idxPlane)) continue;
                                    EmitOneInstance(fixedCoord, y, z, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                                }
                        }
                        else if (meta.Axis == 1) // Y fixed, iterate x,z
                        {
                            for (int xw = baseX; xw < endX; xw++)
                                for (int z = baseZ; z < endZ; z++)
                                {
                                    int idxPlane = (xw * maxZ) + z;
                                    if (worldPlane != null && PlaneBit(worldPlane, idxPlane)) continue;
                                    EmitOneInstance(xw, fixedCoord, z, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                                }
                        }
                        else // Z fixed, iterate x,y
                        {
                            for (int xw = baseX; xw < endX; xw++)
                                for (int y = baseY; y < endY; y++)
                                {
                                    int idxPlane = (xw * maxY) + y;
                                    if (worldPlane != null && PlaneBit(worldPlane, idxPlane)) continue;
                                    EmitOneInstance(xw, y, fixedCoord, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                                }
                        }
                    }
                    continue; // world boundary faces handled; proceed to next face
                }

                // ---------------- NEIGHBOR HANDLING ----------------
                int nsx = sx + meta.Dx; int nsy = sy + meta.Dy; int nsz = sz + meta.Dz;
                ref var nDesc = ref Neighbor(nsx, nsy, nsz);

                // Neighbor empty or air-uniform -> emit whole plane
                if (nDesc.Kind == 0 || (nDesc.Kind == 1 && nDesc.UniformBlockId == 0))
                {
                    EmitFullPlaneSingleTile(faceDir, tile, fixedCoord);
                    continue;
                }

                // Neighbor fully solid -> skip
                if (NeighborFullySolid(ref nDesc)) continue;

                // Neighbor partial: attempt mask-driven visibility
                ulong[] neighborMask = faceDir switch
                {
                    0 => nDesc.FacePosXBits,
                    1 => nDesc.FaceNegXBits,
                    2 => nDesc.FacePosYBits,
                    3 => nDesc.FaceNegYBits,
                    4 => nDesc.FacePosZBits,
                    5 => nDesc.FaceNegZBits,
                    _ => null
                };

                if (neighborMask != null)
                {
                    if (meta.Axis == 0) EmitVisibleByMask_XFace(faceDir, neighborMask, tile, fixedCoord);
                    else if (meta.Axis == 1) EmitVisibleByMask_YFace(faceDir, neighborMask, tile, fixedCoord);
                    else EmitVisibleByMask_ZFace(faceDir, neighborMask, tile, fixedCoord);
                    continue;
                }

                // Fallback per-voxel neighbor occupancy test (no mask available)
                if (meta.Axis == 0) // X fixed
                {
                    int lxNeighbor = meta.Negative ? 15 : 0; // sample neighbor boundary layer
                    for (int z = 0; z < S; z++)
                        for (int y = 0; y < S; y++)
                            if (!NeighborVoxelOccupied(ref nDesc, lxNeighbor, y, z))
                                EmitOneInstance(fixedCoord, baseY + y, baseZ + z, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                }
                else if (meta.Axis == 1) // Y fixed
                {
                    int lyNeighbor = meta.Negative ? 15 : 0;
                    for (int xLocal = 0; xLocal < S; xLocal++)
                        for (int z = 0; z < S; z++)
                            if (!NeighborVoxelOccupied(ref nDesc, xLocal, lyNeighbor, z))
                                EmitOneInstance(baseX + xLocal, fixedCoord, baseZ + z, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                }
                else // Z fixed
                {
                    int lzNeighbor = meta.Negative ? 15 : 0;
                    for (int xLocal = 0; xLocal < S; xLocal++)
                        for (int yLocal = 0; yLocal < S; yLocal++)
                            if (!NeighborVoxelOccupied(ref nDesc, xLocal, yLocal, lzNeighbor))
                                EmitOneInstance(baseX + xLocal, baseY + yLocal, fixedCoord, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                    return true;
                }
            }
            return true;
        }
    }
}
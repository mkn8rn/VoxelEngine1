using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GFX.Models;
using System.Runtime.InteropServices;
using System;
using System.Numerics;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Face classification state (fast-path stratification pre-pass)
        private enum FaceState : byte
        {
            WorldBoundary = 0,    // World boundary (needs world plane mask evaluation)
            NeighborMissing = 1,  // Neighbor empty / air -> full plane emission
            NeighborFullSolid = 2,// Neighbor fully solid -> skip
            NeighborMask = 3,     // Neighbor has per-face mask -> partial emission via mask inversion
            NeighborFallback = 4  // Neighbor requires per-voxel occupancy fallback test
        }

        /// Emits boundary face instances for a Uniform section (Kind==1) containing a single non‑air block id.
        /// Only faces on the outer surface of the section that are exposed to air or world boundary are emitted.
        /// Steps:
        ///  1. Compute per‑face tile indices (PrecomputePerFaceTiles) OR fetch cached set; enable single‑tile fast path if all equal.
        ///  2. Early interior occlusion fast path: if all six neighbors exist and are fully solid, skip immediately.
        ///  3. Fast-path stratification pre-pass: classify each of the six faces into FaceState (WorldBoundary, NeighborMissing, NeighborFullSolid, NeighborMask, NeighborFallback) and compute predicted visible face count (capacity reservation uses this tighter bound instead of worst-case 6*256).
        ///  4. Emit all guaranteed full-plane faces first (NeighborMissing) in a tight loop (reduces branching inside the main per-face loop). These are never world boundary faces with potential plane holes.
        ///  5. Process remaining faces (WorldBoundary, NeighborMask, NeighborFallback) applying boundary plane filtering, mask-driven emission, fallback voxel checks. NeighborFullSolid faces are skipped.
        ///  6. Return true (uniform path always handled; empty uniform returns true with no output). Existing comments retained and updated to describe functionality.
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
                        int bit = BitOperations.TrailingZeroCount(visible);
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
                        int bit = BitOperations.TrailingZeroCount(visible);
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
                        int bit = BitOperations.TrailingZeroCount(visible);
                        visible &= visible - 1;
                        int idx = (wi << 6) + bit; if (idx >= 256) continue;
                        int x = idx >> 4; int y = idx & 15;
                        EmitOneInstance(baseX + x, baseY + y, zFixed, tileIndex, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                    }
                }
            }

            // World plane arrays (same order for convenience)
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // ---------------- FAST-PATH STRATIFICATION PRE-PASS ----------------
            Span<FaceState> faceStates = stackalloc FaceState[6];
            int[] fixedCoords = new int[6];
            ulong[][] worldPlanes = new ulong[6][]; // only valid for WorldBoundary states
            // World boundary indexing parameters per face
            int[] wb_startA = new int[6]; int[] wb_startB = new int[6]; int[] wb_countA = new int[6]; int[] wb_countB = new int[6]; int[] wb_strideB = new int[6];
            // Cache neighbor masks (for NeighborMask state) to avoid recomputing switch later
            ulong[][] neighborMasks = new ulong[6][];

            // Helper local for face plane cell counts
            int PlaneCellCount(int faceDir)
            {
                // Axis 0 faces vary Y,Z; axis 1 faces vary X,Z; axis 2 faces vary X,Y
                return faceDir switch
                {
                    0 or 1 => (endY - baseY) * (endZ - baseZ),
                    2 or 3 => (endX - baseX) * (endZ - baseZ),
                    _ => (endX - baseX) * (endY - baseY)
                };
            }

            // Classification loop (mirrors original per-face switch but only assigns metadata; emission happens later)
            foreach (var meta in _faces)
            {
                int faceDir = meta.FaceDir;
                bool atWorldBoundary = false;
                ulong[] worldPlane = null;
                int startA = 0, startB = 0, countA = 0, countB = 0, strideB = 0;
                int fixedCoord = 0;
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

                fixedCoords[faceDir] = fixedCoord;
                if (atWorldBoundary)
                {
                    faceStates[faceDir] = FaceState.WorldBoundary;
                    worldPlanes[faceDir] = worldPlane;
                    wb_startA[faceDir] = startA; wb_startB[faceDir] = startB;
                    wb_countA[faceDir] = countA; wb_countB[faceDir] = countB; wb_strideB[faceDir] = strideB;
                    continue;
                }

                // Neighbor classification (non-boundary only)
                int nsx = sx + meta.Dx; int nsy = sy + meta.Dy; int nsz = sz + meta.Dz;
                ref var nDesc = ref Neighbor(nsx, nsy, nsz);

                if (nDesc.Kind == 0 || (nDesc.Kind == 1 && nDesc.UniformBlockId == 0))
                {
                    faceStates[faceDir] = FaceState.NeighborMissing; // full plane emission
                    continue;
                }
                if (NeighborFullySolid(ref nDesc))
                {
                    faceStates[faceDir] = FaceState.NeighborFullSolid; // skip later
                    continue;
                }
                // Determine neighbor mask (if any) matching original switch
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
                    faceStates[faceDir] = FaceState.NeighborMask;
                    neighborMasks[faceDir] = neighborMask;
                }
                else
                {
                    faceStates[faceDir] = FaceState.NeighborFallback;
                }
            }

            // Predicted capacity (tighter than worst-case). Pessimistic for fallback faces (assume full plane). World boundary uses exact count of visible cells.
            int predictedFaces = 0;
            for (int fd = 0; fd < 6; fd++)
            {
                FaceState st = faceStates[fd];
                switch (st)
                {
                    case FaceState.NeighborMissing:
                        predictedFaces += PlaneCellCount(fd);
                        break;
                    case FaceState.WorldBoundary:
                        predictedFaces += CountVisibleWorldBoundary(worldPlanes[fd], wb_startA[fd], wb_startB[fd], wb_countA[fd], wb_countB[fd], wb_strideB[fd]);
                        break;
                    case FaceState.NeighborMask:
                        int pcells = PlaneCellCount(fd);
                        int occ = MaskOcclusionCount(neighborMasks[fd]);
                        int vis = pcells - occ; if (vis < 0) vis = 0; predictedFaces += vis;
                        break;
                    case FaceState.NeighborFullSolid:
                        break; // zero
                    case FaceState.NeighborFallback:
                        predictedFaces += PlaneCellCount(fd); // pessimistic
                        break;
                }
            }
            if (predictedFaces < 0) predictedFaces = 0; // safety
            if (predictedFaces > 6 * 256) predictedFaces = 6 * 256; // clamp (should not exceed)
            offsetList.EnsureCapacity(offsetList.Count + predictedFaces * 3);
            tileIndexList.EnsureCapacity(tileIndexList.Count + predictedFaces);
            faceDirList.EnsureCapacity(faceDirList.Count + predictedFaces);

            // ---------------- FULL-PLANE EMISSION FAST PATH ----------------
            for (int fd = 0; fd < 6; fd++)
            {
                if (faceStates[fd] == FaceState.NeighborMissing)
                {
                    // Emit entire plane (never world boundary here by classification).
                    EmitFullPlaneSingleTile(fd, faceTiles[fd], fixedCoords[fd]);
                }
            }

            // ---------------- REMAINING FACES (PARTIAL / BOUNDARY / FALLBACK) ----------------
            foreach (var meta in _faces)
            {
                int faceDir = meta.FaceDir;
                FaceState state = faceStates[faceDir];
                if (state == FaceState.NeighborMissing) continue; // already emitted
                uint tile = faceTiles[faceDir];

                if (state == FaceState.NeighborFullSolid)
                {
                    continue; // fully occluded by solid neighbor
                }

                if (state == FaceState.WorldBoundary)
                {
                    ulong[] worldPlane = worldPlanes[faceDir];
                    int startA = wb_startA[faceDir]; int startB = wb_startB[faceDir];
                    int countA = wb_countA[faceDir]; int countB = wb_countB[faceDir]; int strideB = wb_strideB[faceDir];
                    int fixedCoord = fixedCoords[faceDir];

                    if (!IsWorldPlaneFullySet(worldPlane, startA, startB, countA, countB, strideB))
                    {
                        // Per-axis emission with plane bit test (same as original body, retained, only wrapped by classification)
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
                    continue; // boundary face handled
                }

                if (state == FaceState.NeighborMask)
                {
                    int fixedCoord = fixedCoords[faceDir];
                    ulong[] neighborMask = neighborMasks[faceDir];
                    if (meta.Axis == 0) EmitVisibleByMask_XFace(faceDir, neighborMask, tile, fixedCoord);
                    else if (meta.Axis == 1) EmitVisibleByMask_YFace(faceDir, neighborMask, tile, fixedCoord);
                    else EmitVisibleByMask_ZFace(faceDir, neighborMask, tile, fixedCoords[faceDir]);
                    continue;
                }

                // Fallback per-voxel neighbor occupancy test (no mask available) (original logic retained). This covers FaceState.NeighborFallback.
                if (state == FaceState.NeighborFallback)
                {
                    // Need neighbor descriptor again (re-fetch) because we did not store it; classification limited memory. Cost negligible vs fallback scan.
                    int nsx = sx + meta.Dx; int nsy = sy + meta.Dy; int nsz = sz + meta.Dz;
                    ref var nDesc = ref Neighbor(nsx, nsy, nsz);
                    int fixedCoord = fixedCoords[faceDir];

                    // Fallback per-voxel neighbor occupancy test (no mask available)
                    if (meta.Axis == 0) // X fixed
                    {
                        int lxNeighbor = meta.Negative ? 15 : 0; // sample neighbor boundary layer
                        for (int z = 0; z < S; z++)
                            for (int y = 0; y < S; y++)
                                if (!NeighborVoxelSolid(ref nDesc, lxNeighbor, y, z))
                                    EmitOneInstance(fixedCoord, baseY + y, baseZ + z, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                    }
                    else if (meta.Axis == 1) // Y fixed
                    {
                        int lyNeighbor = meta.Negative ? 15 : 0;
                        for (int xLocal = 0; xLocal < S; xLocal++)
                            for (int z = 0; z < S; z++)
                                if (!NeighborVoxelSolid(ref nDesc, xLocal, lyNeighbor, z))
                                    EmitOneInstance(baseX + xLocal, fixedCoord, baseZ + z, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                    }
                    else // Z fixed
                    {
                        int lzNeighbor = meta.Negative ? 15 : 0;
                        for (int xLocal = 0; xLocal < S; xLocal++)
                            for (int yLocal = 0; yLocal < S; yLocal++)
                                if (!NeighborVoxelSolid(ref nDesc, xLocal, yLocal, lzNeighbor))
                                    EmitOneInstance(baseX + xLocal, baseY + yLocal, fixedCoord, tile, (byte)faceDir, offsetList, tileIndexList, faceDirList);
                        return true; // original behavior returned immediately after Z-face fallback; preserved intentionally
                    }
                }
            }
            return true;
        }
    }
}
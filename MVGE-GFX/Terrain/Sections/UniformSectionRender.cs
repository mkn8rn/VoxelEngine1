using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GFX.Models;
using System.Runtime.InteropServices;
using System;
using System.Numerics;
using MVGE_INF.Loaders;

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
        /// Only faces on the outer surface of the section that are exposed are emitted.
        /// Unified path for BOTH opaque and transparent uniform blocks:
        ///  Opaque visibility: face visible when adjacent cell (section/neighbor/world) is not opaque.
        ///  Transparent visibility: face visible when adjacent cell is air OR a different transparent id; hidden when neighbor is opaque or same transparent id.
        /// Steps (opaque branch preserves earlier logic / comments now describe combined behavior):
        ///  1. Compute per‑face tile indices (cached). Detect if all faces share one tile (single-tile fast path).
        ///  2. Early interior occlusion skip (opaque only). Transparent uniform blocks never skip: they may border opaque or air differently.
        ///  3. (Opaque only) Stratification pre-pass classifies faces and predicts capacity.
        ///  4. (Opaque only) Emit full-plane faces first, then partial / boundary / fallback faces.
        ///  5. (Transparent) Direct per-face plane scan applying transparent rules (no opaque stratification overhead, bounded to 6 * 256 samples). Same-id uniform neighbor planes are skipped entirely up-front. Chunk boundary same-id uniform transparent neighbor chunks are also skipped when every opposing boundary cell matches this id.
        ///  6. Return true (uniform path always handled; empty uniform returns true with no output).
        private bool EmitUniformSectionInstances(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
            List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            ushort block = desc.UniformBlockId; if (block == 0) return true; // treat as empty
            bool isOpaque = TerrainLoader.IsOpaque(block);

            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // Precompute clamped end ranges to eliminate per-iteration bounds checks in loops.
            int endX = baseX + S; if (endX > maxX) endX = maxX;
            int endY = baseY + S; if (endY > maxY) endY = maxY;
            int endZ = baseZ + S; if (endZ > maxZ) endZ = maxZ;

            // Opaque world boundary plane bitsets (used to hide faces where outside neighbor is opaque)
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // Use cached uniform face tile set to avoid recomputing per-face indices for repeated block ids.
            var tileSet = GetFaceTileSet(block);
            bool sameTileAllFaces = tileSet.AllSame;
            uint rTileNX = sameTileAllFaces ? tileSet.SingleTile : tileSet.TileNX;
            uint rTilePX = sameTileAllFaces ? tileSet.SingleTile : tileSet.TilePX;
            uint rTileNY = sameTileAllFaces ? tileSet.SingleTile : tileSet.TileNY;
            uint rTilePY = sameTileAllFaces ? tileSet.SingleTile : tileSet.TilePY;
            uint rTileNZ = sameTileAllFaces ? tileSet.SingleTile : tileSet.TileNZ;
            uint rTilePZ = sameTileAllFaces ? tileSet.SingleTile : tileSet.TilePZ;
            Span<uint> faceTiles = stackalloc uint[6] { rTileNX, rTilePX, rTileNY, rTilePY, rTileNZ, rTilePZ }; // stackalloc to avoid heap alloc per section

            // TRANSPARENT BRANCH (unified path): For transparent uniform blocks we bypass opaque stratification and apply transparent per-face rules.
            if (!isOpaque)
            {
                // Precompute same-id uniform neighbor presence per direction to skip entire planes (avoids per-cell sampling & ensures no faces between identical transparent uniform sections).
                bool skipNX = false, skipPX = false, skipNY = false, skipPY = false, skipNZ = false, skipPZ = false;
                var secs = data.SectionDescs;
                int syCount = data.sectionsY; int szCount = data.sectionsZ; int sxCount = data.sectionsX;

                // Helper local to probe neighbor descriptor safely (returns true if neighbor exists AND is same uniform transparent id).
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                bool SameTransparentUniform(int nsx, int nsy, int nsz)
                {
                    if ((uint)nsx >= (uint)sxCount || (uint)nsy >= (uint)syCount || (uint)nsz >= (uint)szCount) return false;
                    ref var nd = ref secs[((nsx * syCount) + nsy) * szCount + nsz];
                    return nd.Kind == 1 && nd.UniformBlockId == block && !TerrainLoader.IsOpaque(block);
                }
                if (sx > 0) skipNX = SameTransparentUniform(sx - 1, sy, sz);
                if (sx + 1 < sxCount) skipPX = SameTransparentUniform(sx + 1, sy, sz);
                if (sy > 0) skipNY = SameTransparentUniform(sx, sy - 1, sz);
                if (sy + 1 < syCount) skipPY = SameTransparentUniform(sx, sy + 1, sz);
                if (sz > 0) skipNZ = SameTransparentUniform(sx, sy, sz - 1);
                if (sz + 1 < szCount) skipPZ = SameTransparentUniform(sx, sy, sz + 1);

                // plane skip across chunk boundaries: if at chunk boundary and every cell of the neighbor chunk's opposing transparent plane equals this id, skip emission.
                var tNegX = data.NeighborTransparentPlaneNegX; var tPosX = data.NeighborTransparentPlanePosX;
                var tNegY = data.NeighborTransparentPlaneNegY; var tPosY = data.NeighborTransparentPlanePosY;
                var tNegZ = data.NeighborTransparentPlaneNegZ; var tPosZ = data.NeighborTransparentPlanePosZ;

                if (baseX == 0 && !skipNX && tNegX != null) // -X world boundary
                {
                    bool allSame = true;
                    for (int z = baseZ; allSame && z < endZ; z++)
                        for (int y = baseY; y < endY; y++)
                        {
                            int idx = z * maxY + y;
                            if ((uint)idx >= (uint)tNegX.Length || tNegX[idx] != block) { allSame = false; break; }
                        }
                    if (allSame) skipNX = true;
                }
                if (endX == maxX && !skipPX && tPosX != null) // +X world boundary
                {
                    bool allSame = true;
                    for (int z = baseZ; allSame && z < endZ; z++)
                        for (int y = baseY; y < endY; y++)
                        {
                            int idx = z * maxY + y;
                            if ((uint)idx >= (uint)tPosX.Length || tPosX[idx] != block) { allSame = false; break; }
                        }
                    if (allSame) skipPX = true;
                }
                if (baseY == 0 && !skipNY && tNegY != null) // -Y world boundary
                {
                    bool allSame = true;
                    for (int xw = baseX; allSame && xw < endX; xw++)
                        for (int z = baseZ; z < endZ; z++)
                        {
                            int idx = xw * maxZ + z;
                            if ((uint)idx >= (uint)tNegY.Length || tNegY[idx] != block) { allSame = false; break; }
                        }
                    if (allSame) skipNY = true;
                }
                if (endY == maxY && !skipPY && tPosY != null) // +Y world boundary
                {
                    bool allSame = true;
                    for (int xw = baseX; allSame && xw < endX; xw++)
                        for (int z = baseZ; z < endZ; z++)
                        {
                            int idx = xw * maxZ + z;
                            if ((uint)idx >= (uint)tPosY.Length || tPosY[idx] != block) { allSame = false; break; }
                        }
                    if (allSame) skipPY = true;
                }
                if (baseZ == 0 && !skipNZ && tNegZ != null) // -Z world boundary
                {
                    bool allSame = true;
                    for (int xw = baseX; allSame && xw < endX; xw++)
                        for (int y = baseY; y < endY; y++)
                        {
                            int idx = xw * maxY + y;
                            if ((uint)idx >= (uint)tNegZ.Length || tNegZ[idx] != block) { allSame = false; break; }
                        }
                    if (allSame) skipNZ = true;
                }
                if (endZ == maxZ && !skipPZ && tPosZ != null) // +Z world boundary
                {
                    bool allSame = true;
                    for (int xw = baseX; allSame && xw < endX; xw++)
                        for (int y = baseY; y < endY; y++)
                        {
                            int idx = xw * maxY + y;
                            if ((uint)idx >= (uint)tPosZ.Length || tPosZ[idx] != block) { allSame = false; break; }
                        }
                    if (allSame) skipPZ = true;
                }

                // Capacity reserve worst-case (all six planes visible). Exact refinement not required – uniform sections are small.
                int planeCellsXY = (endX - baseX) * (endY - baseY);
                int planeCellsXZ = (endX - baseX) * (endZ - baseZ);
                int planeCellsYZ = (endY - baseY) * (endZ - baseZ);
                int predictedFaces = planeCellsYZ * 2 + planeCellsXZ * 2 + planeCellsXY * 2; // 6 planes potential
                offsetList.EnsureCapacity(offsetList.Count + predictedFaces * 3);
                tileIndexList.EnsureCapacity(tileIndexList.Count + predictedFaces);
                faceDirList.EnsureCapacity(faceDirList.Count + predictedFaces);

                // Transparent neighbor plane ids (used to hide faces where outside neighbor has same transparent id)
                // (References retained above for skip and per-cell visibility tests.)

                // Helper to test visibility for a candidate neighbor world coordinate.
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                bool TransparentFaceVisible(int nx, int ny, int nz, int curX, int curY, int curZ)
                {
                    // World boundary checks: index mapping same as fallback path.
                    if (nx < 0)
                    {
                        int idx = curZ * maxY + curY;
                        if (PlaneBit(planeNegX, idx)) return false; // outside opaque
                        if (tNegX != null && (uint)idx < (uint)tNegX.Length && tNegX[idx] == block) return false; // same-id outside
                        return true;
                    }
                    if (nx >= maxX)
                    {
                        int idx = curZ * maxY + curY;
                        if (PlaneBit(planePosX, idx)) return false;
                        if (tPosX != null && (uint)idx < (uint)tPosX.Length && tPosX[idx] == block) return false;
                        return true;
                    }
                    if (ny < 0)
                    {
                        int idx = curX * maxZ + curZ;
                        if (PlaneBit(planeNegY, idx)) return false;
                        if (tNegY != null && (uint)idx < (uint)tNegY.Length && tNegY[idx] == block) return false;
                        return true;
                    }
                    if (ny >= maxY)
                    {
                        int idx = curX * maxZ + curZ;
                        if (PlaneBit(planePosY, idx)) return false;
                        if (tPosY != null && (uint)idx < (uint)tPosY.Length && tPosY[idx] == block) return false;
                        return true;
                    }
                    if (nz < 0)
                    {
                        int idx = curX * maxY + curY;
                        if (PlaneBit(planeNegZ, idx)) return false;
                        if (tNegZ != null && (uint)idx < (uint)tNegZ.Length && tNegZ[idx] == block) return false;
                        return true;
                    }
                    if (nz >= maxZ)
                    {
                        int idx = curX * maxY + curY;
                        if (PlaneBit(planePosZ, idx)) return false;
                        if (tPosZ != null && (uint)idx < (uint)tPosZ.Length && tPosZ[idx] == block) return false;
                        return true;
                    }
                    // Inside chunk: sample neighbor block id.
                    ushort nb = GetBlock(nx, ny, nz);
                    if (nb == 0) return true;               // air reveals face
                    if (TerrainLoader.IsOpaque(nb)) return false; // opaque hides
                    if (nb == block) return false;          // same transparent id hides seam
                    return true;                            // different transparent id shows seam edge
                }

                // Emit faces per direction using transparent rules, skipping entire planes when same-id uniform neighbor present (section or chunk).
                // LEFT (-X)
                if (!skipNX)
                {
                    for (int y = baseY; y < endY; y++)
                        for (int z = baseZ; z < endZ; z++)
                            if (TransparentFaceVisible(baseX - 1, y, z, baseX, y, z))
                                EmitOneInstance(baseX, y, z, rTileNX, 0, offsetList, tileIndexList, faceDirList);
                }
                // RIGHT (+X)
                int rx = endX - 1;
                if (!skipPX)
                {
                    for (int y = baseY; y < endY; y++)
                        for (int z = baseZ; z < endZ; z++)
                            if (TransparentFaceVisible(rx + 1, y, z, rx, y, z))
                                EmitOneInstance(rx, y, z, rTilePX, 1, offsetList, tileIndexList, faceDirList);
                }
                // BOTTOM (-Y)
                if (!skipNY)
                {
                    for (int xw = baseX; xw < endX; xw++)
                        for (int z = baseZ; z < endZ; z++)
                            if (TransparentFaceVisible(xw, baseY - 1, z, xw, baseY, z))
                                EmitOneInstance(xw, baseY, z, rTileNY, 2, offsetList, tileIndexList, faceDirList);
                }
                // TOP (+Y)
                int ty = endY - 1;
                if (!skipPY)
                {
                    for (int xw = baseX; xw < endX; xw++)
                        for (int z = baseZ; z < endZ; z++)
                            if (TransparentFaceVisible(xw, ty + 1, z, xw, ty, z))
                                EmitOneInstance(xw, ty, z, rTilePY, 3, offsetList, tileIndexList, faceDirList);
                }
                // BACK (-Z)
                if (!skipNZ)
                {
                    for (int xw = baseX; xw < endX; xw++)
                        for (int y = baseY; y < endY; y++)
                            if (TransparentFaceVisible(xw, y, baseZ - 1, xw, y, baseZ))
                                EmitOneInstance(xw, y, baseZ, rTileNZ, 4, offsetList, tileIndexList, faceDirList);
                }
                // FRONT (+Z)
                int fz = endZ - 1;
                if (!skipPZ)
                {
                    for (int xw = baseX; xw < endX; xw++)
                        for (int y = baseY; y < endY; y++)
                            if (TransparentFaceVisible(xw, y, fz + 1, xw, y, fz))
                                EmitOneInstance(xw, y, fz, rTilePZ, 5, offsetList, tileIndexList, faceDirList);
                }

                return true;
            }

            // ---------------- OPAQUE PATH (original stratified fast path) ----------------
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
            int predictedFacesOpaque = 0;
            for (int fd = 0; fd < 6; fd++)
            {
                FaceState st = faceStates[fd];
                switch (st)
                {
                    case FaceState.NeighborMissing:
                        predictedFacesOpaque += PlaneCellCount(fd);
                        break;
                    case FaceState.WorldBoundary:
                        predictedFacesOpaque += CountVisibleWorldBoundary(worldPlanes[fd], wb_startA[fd], wb_startB[fd], wb_countA[fd], wb_countB[fd], wb_strideB[fd]);
                        break;
                    case FaceState.NeighborMask:
                        int pcells = PlaneCellCount(fd);
                        int occ = MaskOcclusionCount(neighborMasks[fd]);
                        int vis = pcells - occ; if (vis < 0) vis = 0; predictedFacesOpaque += vis;
                        break;
                    case FaceState.NeighborFullSolid:
                        break; // zero
                    case FaceState.NeighborFallback:
                        predictedFacesOpaque += PlaneCellCount(fd); // pessimistic
                        break;
                }
            }
            if (predictedFacesOpaque < 0) predictedFacesOpaque = 0; // safety
            if (predictedFacesOpaque > 6 * 256) predictedFacesOpaque = 6 * 256; // clamp (should not exceed)
            offsetList.EnsureCapacity(offsetList.Count + predictedFacesOpaque * 3);
            tileIndexList.EnsureCapacity(tileIndexList.Count + predictedFacesOpaque);
            faceDirList.EnsureCapacity(faceDirList.Count + predictedFacesOpaque);

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
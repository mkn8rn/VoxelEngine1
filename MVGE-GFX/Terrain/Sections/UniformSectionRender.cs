using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GFX.Models;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Instanced emission for uniform sections. Emits only boundary faces.
        // Returns true if handled (always true for uniform non-air).
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
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            uint ComputeTileIndex(ushort blk, Faces face)
            {
                var uvFace = atlas.GetBlockUVs(blk, face);
                byte minTileX = 255, minTileY = 255;
                for (int i = 0; i < 4; i++) { if (uvFace[i].x < minTileX) minTileX = uvFace[i].x; if (uvFace[i].y < minTileY) minTileY = uvFace[i].y; }
                return (uint)(minTileY * atlas.tilesX + minTileX);
            }
            uint tileNX = ComputeTileIndex(block, Faces.LEFT);
            uint tilePX = ComputeTileIndex(block, Faces.RIGHT);
            uint tileNY = ComputeTileIndex(block, Faces.BOTTOM);
            uint tilePY = ComputeTileIndex(block, Faces.TOP);
            uint tileNZ = ComputeTileIndex(block, Faces.BACK);
            uint tilePZ = ComputeTileIndex(block, Faces.FRONT);
            bool sameTileAllFaces = tileNX == tilePX && tileNX == tileNY && tileNX == tilePY && tileNX == tileNZ && tileNX == tilePZ;
            // When all 6 faces share the same tile, emit using a single tile index to reduce per-face variation handling.
            uint singleTileIndex = tileNX;
            // Resolve per-face tiles once (applies singleTileIndex when all faces match)
            uint rTileNX = sameTileAllFaces ? singleTileIndex : tileNX;
            uint rTilePX = sameTileAllFaces ? singleTileIndex : tilePX;
            uint rTileNY = sameTileAllFaces ? singleTileIndex : tileNY;
            uint rTilePY = sameTileAllFaces ? singleTileIndex : tilePY;
            uint rTileNZ = sameTileAllFaces ? singleTileIndex : tileNZ;
            uint rTilePZ = sameTileAllFaces ? singleTileIndex : tilePZ;

            // Helper to emit a single instance (direct write, no per-voxel atlas lookup)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitOne(byte faceDir, int wx, int wy, int wz, uint tileIndex)
            {
                offsetList.Add((byte)wx); offsetList.Add((byte)wy); offsetList.Add((byte)wz);
                tileIndexList.Add(tileIndex);
                faceDirList.Add(faceDir);
            }

            // Helper to emit an entire face plane of SxS blocks
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitPlaneSingleTile(int faceDir, int wxFixed, int wyFixed, int wzFixed, uint tileIndex)
            {
                // axis: derived from faceDir (0/1: x fixed, varying y,z) (2/3: y fixed, varying x,z) (4/5: z fixed, varying x,y)
                if (faceDir == 0 || faceDir == 1)
                {
                    for (int y = baseY; y < endY; y++)
                        for (int z = baseZ; z < endZ; z++)
                            EmitOne((byte)faceDir, wxFixed, y, z, tileIndex);
                }
                else if (faceDir == 2 || faceDir == 3)
                {
                    for (int x = baseX; x < endX; x++)
                        for (int z = baseZ; z < endZ; z++)
                            EmitOne((byte)faceDir, x, wyFixed, z, tileIndex);
                }
                else
                {
                    for (int x = baseX; x < endX; x++)
                        for (int y = baseY; y < endY; y++)
                            EmitOne((byte)faceDir, x, y, wzFixed, tileIndex);
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
                // Multi/Single packed cannot guarantee full fill without occupancy; rely on NonAirCount==4096 heuristic if metadata kept.
                if ((n.Kind == 4 || n.Kind == 5) && n.NonAirCount == 4096) return true;
                return false;
            }

            // NEW: Fast per-boundary-voxel occlusion test leveraging neighbor face bitsets or occupancy
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborFaceBit(ref SectionPrerenderDesc n, int whichFaceArray /*0 NX,1 PX,2 NY,3 PY,4 NZ,5 PZ*/, int planeIndex)
            {
                ulong[] arr = whichFaceArray switch
                {
                    0 => n.FaceNegXBits,
                    1 => n.FacePosXBits,
                    2 => n.FaceNegYBits,
                    3 => n.FacePosYBits,
                    4 => n.FaceNegZBits,
                    5 => n.FacePosZBits,
                    _ => null
                };
                if (arr == null) return false;
                int w = planeIndex >> 6; int b = planeIndex & 63; if (w >= arr.Length) return false; return (arr[w] & (1UL << b)) != 0UL;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborVoxelOccupied(ref SectionPrerenderDesc n, int lx, int ly, int lz)
            {
                if (n.OccupancyBits == null) return false;
                int li = ((lz * 16 + lx) * 16) + ly;
                return (n.OccupancyBits[li >> 6] & (1UL << (li & 63))) != 0UL;
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
                        int bit = System.Numerics.BitOperations.TrailingZeroCount(visible);
                        visible &= visible - 1;
                        int idx = (wi << 6) + bit; if (idx >= 256) continue; // guard if longer
                        int z = idx >> 4; int y = idx & 15;
                        EmitOne((byte)faceDir, xFixed, baseY + y, baseZ + z, tileIndex);
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
                        EmitOne((byte)faceDir, baseX + x, yFixed, baseZ + z, tileIndex);
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
                        EmitOne((byte)faceDir, baseX + x, baseY + y, zFixed, tileIndex);
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
                // Check neighbor plane bitset at chunk boundary. Whole-plane fast skip if plane says fully occluded.
                if (baseX == 0 && IsWorldPlaneFullySet(data.NeighborPlaneNegX, baseZ, baseY, endZ - baseZ, endY - baseY, maxY))
                {
                    // fully hidden
                }
                else
                {
                    for (int z = baseZ; z < endZ; z++)
                        for (int y = baseY; y < endY; y++)
                            if (!(baseX == 0 && PlaneBit(data.NeighborPlaneNegX, z * maxY + y))) // hidden when neighbor plane bit is set
                                EmitOne(0, baseX, y, z, rTileNX);
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
                        // Fast mask-based iteration: use neighbor's +X face (FacePosXBits); emit only visible bits.
                        if (n.FacePosXBits != null)
                        {
                            EmitVisibleByMask_XFace(0, n.FacePosXBits, rTileNX, baseX);
                        }
                        else
                        {
                            // fallback occupancy test at neighbor local (x=15,y,z)
                            for (int z = 0; z < S; z++)
                                for (int y = 0; y < S; y++)
                                    if (!NeighborVoxelOccupied(ref n, 15, y, z)) EmitOne(0, baseX, baseY + y, baseZ + z, rTileNX);
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
                                EmitOne(1, wxRight, y, z, rTilePX);
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
                                    if (!NeighborVoxelOccupied(ref n, 0, y, z)) EmitOne(1, wxRight, baseY + y, baseZ + z, rTilePX);
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
                                EmitOne(2, x, baseY, z, rTileNY);
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
                                if (!NeighborVoxelOccupied(ref n, x, 15, z)) EmitOne(2, baseX + x, baseY, baseZ + z, rTileNY);
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
                                EmitOne(3, x, wyTop, z, rTilePY);
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
                                if (!NeighborVoxelOccupied(ref n, x, 0, z)) EmitOne(3, baseX + x, wyTop, baseZ + z, rTilePY);
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
                                EmitOne(4, x, y, baseZ, rTileNZ);
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
                                if (!NeighborVoxelOccupied(ref n, x, y, 15)) EmitOne(4, baseX + x, baseY + y, baseZ, rTileNZ);
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
                                EmitOne(5, x, y, wzFront, rTilePZ);
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
                                if (!NeighborVoxelOccupied(ref n, x, y, 0)) EmitOne(5, baseX + x, baseY + y, wzFront, rTilePZ);
                    }
                }
            }
            return true;
        }
    }
}

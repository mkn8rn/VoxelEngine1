using MVGE_INF.Models.Generation;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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

            // Helper to emit an entire face plane of SxS blocks
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void EmitPlane(int faceDir, int wxFixed, int wyFixed, int wzFixed, int axis)
            {
                // axis: 0=x varies,1=y varies,2=z varies
                for (int a = 0; a < S; a++)
                {
                    for (int b = 0; b < S; b++)
                    {
                        int x = wxFixed, y = wyFixed, z = wzFixed;
                        switch (faceDir)
                        {
                            case 0: // LEFT  (x fixed)
                            case 1: // RIGHT (x fixed)
                                y = baseY + a; z = baseZ + b; break;
                            case 2: // BOTTOM (y fixed)
                            case 3: // TOP    (y fixed)
                                x = baseX + a; z = baseZ + b; break;
                            case 4: // BACK  (z fixed)
                            case 5: // FRONT (z fixed)
                                x = baseX + a; y = baseY + b; break;
                        }
                        EmitFaceInstance(block, (byte)faceDir, x, y, z, offsetList, tileIndexList, faceDirList);
                    }
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

            // LEFT
            if (sx == 0)
            {
                // Check neighbor plane bitset at chunk boundary
                for (int z = 0; z < S; z++)
                {
                    int wz = baseZ + z; if (wz >= maxZ) break;
                    for (int y = 0; y < S; y++)
                    {
                        int wy = baseY + y; if (wy >= maxY) break;
                        if (baseX == 0 && PlaneBit(data.NeighborPlaneNegX, wz * maxY + wy)) continue; // hidden
                        EmitFaceInstance(block, 0, baseX, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            else
            {
                ref var n = ref Neighbor(sx - 1, sy, sz);
                if (n.Kind == 0 || (n.Kind == 1 && n.UniformBlockId == 0))
                {
                    EmitPlane(0, baseX, 0, 0, 0);
                }
                else
                {
                    bool fullOcclude = NeighborFullySolid(ref n);
                    if (!fullOcclude)
                    {
                        // Fast mask-based iteration: use neighbor's +X face (FacePosXBits) else fallback to occupancy
                        ulong[] mask = n.FacePosXBits;
                        for (int z = 0; z < S; z++)
                        {
                            int wz = baseZ + z; if (wz >= maxZ) break;
                            for (int y = 0; y < S; y++)
                            {
                                int wy = baseY + y; if (wy >= maxY) break;
                                bool occluded;
                                if (mask != null)
                                {
                                    int planeIndex = z * 16 + y; int w = planeIndex >> 6; int b = planeIndex & 63;
                                    occluded = (mask[w] & (1UL << b)) != 0UL;
                                }
                                else
                                {
                                    // occupancy test at neighbor local (x=15,y,z)
                                    occluded = NeighborVoxelOccupied(ref n, 15, y, z);
                                }
                                if (!occluded) EmitFaceInstance(block, 0, baseX, wy, wz, offsetList, tileIndexList, faceDirList);
                            }
                        }
                    }
                }
            }
            // RIGHT
            int wxRight = baseX + S - 1;
            if (sx == data.sectionsX - 1)
            {
                for (int z = 0; z < S; z++)
                {
                    int wz = baseZ + z; if (wz >= maxZ) break;
                    for (int y = 0; y < S; y++)
                    {
                        int wy = baseY + y; if (wy >= maxY) break;
                        if (wxRight == maxX - 1 && PlaneBit(data.NeighborPlanePosX, wz * maxY + wy)) continue;
                        EmitFaceInstance(block, 1, wxRight, wy, wz, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            else
            {
                ref var n = ref Neighbor(sx + 1, sy, sz);
                if (n.Kind == 0 || (n.Kind == 1 && n.UniformBlockId == 0))
                {
                    EmitPlane(1, wxRight, 0, 0, 0);
                }
                else
                {
                    bool fullOcclude = NeighborFullySolid(ref n);
                    if (!fullOcclude)
                    {
                        ulong[] mask = n.FaceNegXBits; // neighbor -X face
                        for (int z = 0; z < S; z++)
                        {
                            int wz = baseZ + z; if (wz >= maxZ) break;
                            for (int y = 0; y < S; y++)
                            {
                                int wy = baseY + y; if (wy >= maxY) break;
                                bool occluded;
                                if (mask != null)
                                {
                                    int planeIndex = z * 16 + y; int w = planeIndex >> 6; int b = planeIndex & 63; occluded = (mask[w] & (1UL << b)) != 0UL;
                                }
                                else
                                {
                                    occluded = NeighborVoxelOccupied(ref n, 0, y, z);
                                }
                                if (!occluded) EmitFaceInstance(block, 1, wxRight, wy, wz, offsetList, tileIndexList, faceDirList);
                            }
                        }
                    }
                }
            }
            // BOTTOM
            if (sy == 0)
            {
                for (int x = 0; x < S; x++)
                {
                    int wx = baseX + x; if (wx >= maxX) break;
                    for (int z = 0; z < S; z++)
                    {
                        int wz = baseZ + z; if (wz >= maxZ) break;
                        if (baseY == 0 && PlaneBit(data.NeighborPlaneNegY, wx * maxZ + wz)) continue;
                        EmitFaceInstance(block, 2, wx, baseY, wz, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy - 1, sz);
                bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    ulong[] mask = n.FacePosYBits; // neighbor +Y face
                    for (int x = 0; x < S; x++)
                    {
                        int wx = baseX + x; if (wx >= maxX) break;
                        for (int z = 0; z < S; z++)
                        {
                            int wz = baseZ + z; if (wz >= maxZ) break;
                            bool occluded;
                            if (mask != null)
                            {
                                int planeIndex = x * 16 + z; int w = planeIndex >> 6; int b = planeIndex & 63; occluded = (mask[w] & (1UL << b)) != 0UL;
                            }
                            else
                            {
                                occluded = NeighborVoxelOccupied(ref n, x, 15, z);
                            }
                            if (!occluded) EmitFaceInstance(block, 2, wx, baseY, wz, offsetList, tileIndexList, faceDirList);
                        }
                    }
                }
            }
            // TOP
            int wyTop = baseY + S - 1;
            if (sy == data.sectionsY - 1)
            {
                for (int x = 0; x < S; x++)
                {
                    int wx = baseX + x; if (wx >= maxX) break;
                    for (int z = 0; z < S; z++)
                    {
                        int wz = baseZ + z; if (wz >= maxZ) break;
                        if (wyTop == maxY - 1 && PlaneBit(data.NeighborPlanePosY, wx * maxZ + wz)) continue;
                        EmitFaceInstance(block, 3, wx, wyTop, wz, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy + 1, sz);
                bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    ulong[] mask = n.FaceNegYBits; // neighbor -Y face
                    for (int x = 0; x < S; x++)
                    {
                        int wx = baseX + x; if (wx >= maxX) break;
                        for (int z = 0; z < S; z++)
                        {
                            int wz = baseZ + z; if (wz >= maxZ) break;
                            bool occluded;
                            if (mask != null)
                            {
                                int planeIndex = x * 16 + z; int w = planeIndex >> 6; int b = planeIndex & 63; occluded = (mask[w] & (1UL << b)) != 0UL;
                            }
                            else
                            {
                                occluded = NeighborVoxelOccupied(ref n, x, 0, z);
                            }
                            if (!occluded) EmitFaceInstance(block, 3, wx, wyTop, wz, offsetList, tileIndexList, faceDirList);
                        }
                    }
                }
            }
            // BACK
            if (sz == 0)
            {
                for (int x = 0; x < S; x++)
                {
                    int wx = baseX + x; if (wx >= maxX) break;
                    for (int y = 0; y < S; y++)
                    {
                        int wy = baseY + y; if (wy >= maxY) break;
                        if (baseZ == 0 && PlaneBit(data.NeighborPlaneNegZ, wx * maxY + wy)) continue;
                        EmitFaceInstance(block, 4, wx, wy, baseZ, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy, sz - 1); bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    ulong[] mask = n.FacePosZBits; // neighbor +Z face
                    for (int x = 0; x < S; x++)
                    {
                        int wx = baseX + x; if (wx >= maxX) break;
                        for (int y = 0; y < S; y++)
                        {
                            int wy = baseY + y; if (wy >= maxY) break;
                            bool occluded;
                            if (mask != null)
                            {
                                int planeIndex = x * 16 + y; int w = planeIndex >> 6; int b = planeIndex & 63; occluded = (mask[w] & (1UL << b)) != 0UL;
                            }
                            else
                            {
                                occluded = NeighborVoxelOccupied(ref n, x, y, 15);
                            }
                            if (!occluded) EmitFaceInstance(block, 4, wx, wy, baseZ, offsetList, tileIndexList, faceDirList);
                        }
                    }
                }
            }
            // FRONT
            int wzFront = baseZ + S - 1;
            if (sz == data.sectionsZ - 1)
            {
                for (int x = 0; x < S; x++)
                {
                    int wx = baseX + x; if (wx >= maxX) break;
                    for (int y = 0; y < S; y++)
                    {
                        int wy = baseY + y; if (wy >= maxY) break;
                        if (wzFront == maxZ - 1 && PlaneBit(data.NeighborPlanePosZ, wx * maxY + wy)) continue;
                        EmitFaceInstance(block, 5, wx, wy, wzFront, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
            else
            {
                ref var n = ref Neighbor(sx, sy, sz + 1); bool fullOcclude = NeighborFullySolid(ref n);
                if (!fullOcclude)
                {
                    ulong[] mask = n.FaceNegZBits; // neighbor -Z face
                    for (int x = 0; x < S; x++)
                    {
                        int wx = baseX + x; if (wx >= maxX) break;
                        for (int y = 0; y < S; y++)
                        {
                            int wy = baseY + y; if (wy >= maxY) break;
                            bool occluded;
                            if (mask != null)
                            {
                                int planeIndex = x * 16 + y; int w = planeIndex >> 6; int b = planeIndex & 63; occluded = (mask[w] & (1UL << b)) != 0UL;
                            }
                            else
                            {
                                occluded = NeighborVoxelOccupied(ref n, x, y, 0);
                            }
                            if (!occluded) EmitFaceInstance(block, 5, wx, wy, wzFront, offsetList, tileIndexList, faceDirList);
                        }
                    }
                }
            }
            return true;
        }
    }
}

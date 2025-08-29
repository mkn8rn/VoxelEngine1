using MVGE_GEN.Models;
using MVGE_GFX.Terrain;
using MVGE_INF.Models.Generation;
using MVGE_INF.Models.Terrain;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        // ----- Added plane caches for pre-render handoff -----
        // Layouts:
        //  Neg/Pos X: YZ plane (index = z * dimY + y)
        //  Neg/Pos Y: XZ plane (index = x * dimZ + z)
        //  Neg/Pos Z: XY plane (index = x * dimY + y)
        internal ulong[] PlaneNegX; // this chunk's -X face
        internal ulong[] PlanePosX; // +X face
        internal ulong[] PlaneNegY; // -Y face
        internal ulong[] PlanePosY; // +Y face
        internal ulong[] PlaneNegZ; // -Z face
        internal ulong[] PlanePosZ; // +Z face

        // Neighbor plane caches (populated by WorldResources just before BuildRender)
        internal ulong[] NeighborPlaneNegXFace; // neighbor at -X (+X face of neighbor)
        internal ulong[] NeighborPlanePosXFace; // neighbor at +X (-X face of neighbor)
        internal ulong[] NeighborPlaneNegYFace; // neighbor at -Y (+Y face)
        internal ulong[] NeighborPlanePosYFace; // neighbor at +Y (-Y face)
        internal ulong[] NeighborPlaneNegZFace; // neighbor at -Z (+Z face)
        internal ulong[] NeighborPlanePosZFace; // neighbor at +Z (-Z face)

        private void EnsurePlaneArrays()
        {
            // allocate only once; lengths fixed by dimensions
            int yzBits = dimY * dimZ; int yzWC = (yzBits + 63) >> 6;
            int xzBits = dimX * dimZ; int xzWC = (xzBits + 63) >> 6;
            int xyBits = dimX * dimY; int xyWC = (xyBits + 63) >> 6;
            PlaneNegX ??= new ulong[yzWC];
            PlanePosX ??= new ulong[yzWC];
            PlaneNegY ??= new ulong[xzWC];
            PlanePosY ??= new ulong[xzWC];
            PlaneNegZ ??= new ulong[xyWC];
            PlanePosZ ??= new ulong[xyWC];
        }

        private void BuildAllBoundaryPlanesInitial()
        {
            EnsurePlaneArrays();
            Array.Clear(PlaneNegX); Array.Clear(PlanePosX);
            Array.Clear(PlaneNegY); Array.Clear(PlanePosY);
            Array.Clear(PlaneNegZ); Array.Clear(PlanePosZ);
            ushort EMPTY = (ushort)BaseBlockType.Empty;

            // -X / +X (YZ planes)
            for (int z = 0; z < dimZ; z++)
            {
                for (int y = 0; y < dimY; y++)
                {
                    int yzIndex = z * dimY + y; int w = yzIndex >> 6; int b = yzIndex & 63;
                    if (GetBlockLocal(0, y, z) != EMPTY) PlaneNegX[w] |= 1UL << b;
                    if (GetBlockLocal(dimX - 1, y, z) != EMPTY) PlanePosX[w] |= 1UL << b;
                }
            }
            // -Y / +Y (XZ planes)
            for (int x = 0; x < dimX; x++)
            {
                for (int z = 0; z < dimZ; z++)
                {
                    int xzIndex = x * dimZ + z; int w = xzIndex >> 6; int b = xzIndex & 63;
                    if (GetBlockLocal(x, 0, z) != EMPTY) PlaneNegY[w] |= 1UL << b;
                    if (GetBlockLocal(x, dimY - 1, z) != EMPTY) PlanePosY[w] |= 1UL << b;
                }
            }
            // -Z / +Z (XY planes)
            for (int x = 0; x < dimX; x++)
            {
                for (int y = 0; y < dimY; y++)
                {
                    int xyIndex = x * dimY + y; int w = xyIndex >> 6; int b = xyIndex & 63;
                    if (GetBlockLocal(x, y, 0) != EMPTY) PlaneNegZ[w] |= 1UL << b;
                    if (GetBlockLocal(x, y, dimZ - 1) != EMPTY) PlanePosZ[w] |= 1UL << b;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateBoundaryPlaneBit(int lx, int ly, int lz, ushort blockId)
        {
            ushort EMPTY = (ushort)BaseBlockType.Empty;
            bool solid = blockId != EMPTY;
            // allocate if not present yet (late creation path after generation)
            EnsurePlaneArrays();
            if (lx == 0)
            {
                int yzIndex = lz * dimY + ly; int w = yzIndex >> 6; int b = yzIndex & 63;
                ulong mask = 1UL << b;
                if (solid) PlaneNegX[w] |= mask; else PlaneNegX[w] &= ~mask;
            }
            if (lx == dimX - 1)
            {
                int yzIndex = lz * dimY + ly; int w = yzIndex >> 6; int b = yzIndex & 63; ulong mask = 1UL << b;
                if (solid) PlanePosX[w] |= mask; else PlanePosX[w] &= ~mask;
            }
            if (ly == 0)
            {
                int xzIndex = lx * dimZ + lz; int w = xzIndex >> 6; int b = xzIndex & 63; ulong mask = 1UL << b;
                if (solid) PlaneNegY[w] |= mask; else PlaneNegY[w] &= ~mask;
            }
            if (ly == dimY - 1)
            {
                int xzIndex = lx * dimZ + lz; int w = xzIndex >> 6; int b = xzIndex & 63; ulong mask = 1UL << b;
                if (solid) PlanePosY[w] |= mask; else PlanePosY[w] &= ~mask;
            }
            if (lz == 0)
            {
                int xyIndex = lx * dimY + ly; int w = xyIndex >> 6; int b = xyIndex & 63; ulong mask = 1UL << b;
                if (solid) PlaneNegZ[w] |= mask; else PlaneNegZ[w] &= ~mask;
            }
            if (lz == dimZ - 1)
            {
                int xyIndex = lx * dimY + ly; int w = xyIndex >> 6; int b = xyIndex & 63; ulong mask = 1UL << b;
                if (solid) PlanePosZ[w] |= mask; else PlanePosZ[w] &= ~mask;
            }
        }
        private int sliceStampVersion;
        private int[] xSliceStamp;
        private int[] ySliceStamp;
        private int[] zSliceStamp;

        internal ChunkMeshPrepassStats MeshPrepassStats; // changed to field to allow direct mutation

        // Mesh prepass stats generated during flattening to avoid extra scans in ChunkRender
        internal struct ChunkMeshPrepassStats
        {
            public int SolidCount;
            public int ExposureEstimate;
            public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ; // inclusive bounds
            public int XNonEmpty, YNonEmpty, ZNonEmpty;     // counts of slices with any solid
            public bool HasStats;
            public void AccumulateBounds(int x, int y, int z)
            {
                if (!HasStats)
                {
                    MinX = MaxX = x; MinY = MaxY = y; MinZ = MaxZ = z; HasStats = true; return;
                }
                if (x < MinX) MinX = x; else if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y; else if (y > MaxY) MaxY = y;
                if (z < MinZ) MinZ = z; else if (z > MaxZ) MaxZ = z;
            }
        }

        internal ChunkPrerenderData BuildPrerenderData(ushort[] blocks)
        {
            return new ChunkPrerenderData
            {
                FaceNegX = FaceSolidNegX,
                FacePosX = FaceSolidPosX,
                FaceNegY = FaceSolidNegY,
                FacePosY = FaceSolidPosY,
                FaceNegZ = FaceSolidNegZ,
                FacePosZ = FaceSolidPosZ,
                NeighborNegXPosX = NeighborNegXFaceSolidPosX,
                NeighborPosXNegX = NeighborPosXFaceSolidNegX,
                NeighborNegYPosY = NeighborNegYFaceSolidPosY,
                NeighborPosYNegY = NeighborPosYFaceSolidNegY,
                NeighborNegZPosZ = NeighborNegZFaceSolidPosZ,
                NeighborPosZNegZ = NeighborPosZFaceSolidNegZ,
                AllOneBlock = AllOneBlockChunk,
                AllOneBlockId = AllOneBlockBlockId,
                PrepassSolidCount = MeshPrepassStats.SolidCount,
                PrepassExposureEstimate = MeshPrepassStats.ExposureEstimate,
                SelfPlaneNegX = PlaneNegX,
                SelfPlanePosX = PlanePosX,
                SelfPlaneNegY = PlaneNegY,
                SelfPlanePosY = PlanePosY,
                SelfPlaneNegZ = PlaneNegZ,
                SelfPlanePosZ = PlanePosZ,
                NeighborPlaneNegX = NeighborPlaneNegXFace,
                NeighborPlanePosX = NeighborPlanePosXFace,
                NeighborPlaneNegY = NeighborPlaneNegYFace,
                NeighborPlanePosY = NeighborPlanePosYFace,
                NeighborPlaneNegZ = NeighborPlaneNegZFace,
                NeighborPlanePosZ = NeighborPlanePosZFace,

                chunkData = chunkData,
                flatBlocks = blocks,
                maxX = dimX,
                maxY = dimY,
                maxZ = dimZ
            };
        }

        private bool[,,] PrecomputeOccludedFullSections()
        {
            // A section is occluded full if it and all 6 orthogonal neighbors exist and are CompletelyFull.
            bool[,,] occluded = new bool[sectionsX, sectionsY, sectionsZ];
            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null || !sec.CompletelyFull) continue;
                        // Require in-bounds neighbors (internal only)
                        if (sx == 0 || sx == sectionsX - 1 || sy == 0 || sy == sectionsY - 1 || sz == 0 || sz == sectionsZ - 1) continue;
                        if (sections[sx - 1, sy, sz]?.CompletelyFull != true) continue;
                        if (sections[sx + 1, sy, sz]?.CompletelyFull != true) continue;
                        if (sections[sx, sy - 1, sz]?.CompletelyFull != true) continue;
                        if (sections[sx, sy + 1, sz]?.CompletelyFull != true) continue;
                        if (sections[sx, sy, sz - 1]?.CompletelyFull != true) continue;
                        if (sections[sx, sy, sz + 1]?.CompletelyFull != true) continue;
                        occluded[sx, sy, sz] = true;
                    }
                }
            }
            return occluded;
        }

        private int FlattenSections(ushort[] dest)
        {
            int strideX = dimZ * dimY; // (x * dimZ + z) * dimY + y
            int strideZ = dimY;
            int sectionSize = ChunkSection.SECTION_SIZE;

            int nonAirTotal = 0;
            HasAnyBoundarySolid = false;

            // Stats aggregated from section metadata (bounds + slice occupancy derived later if needed)
            ChunkMeshPrepassStats stats = default;
            long exposureInternalSum = 0; // sum of per-section internal exposure

            int totalVoxels = dimX * dimY * dimZ;
            dest.AsSpan(0, totalVoxels).Clear();

            var occludedFull = PrecomputeOccludedFullSections();

            // Track shared face adjacency counts
            long sharedAdjX = 0, sharedAdjY = 0, sharedAdjZ = 0;

            // Accurate unique slice counting setup
            EnsureSliceStampArrays();

            for (int sx = 0; sx < sectionsX; sx++)
            {
                int baseX = sx * sectionSize; if (baseX >= dimX) break;
                int maxLocalX = Math.Min(sectionSize, dimX - baseX);
                for (int sz = 0; sz < sectionsZ; sz++)
                {
                    int baseZ = sz * sectionSize; if (baseZ >= dimZ) break;
                    int maxLocalZ = Math.Min(sectionSize, dimZ - baseZ);
                    for (int sy = 0; sy < sectionsY; sy++)
                    {
                        int baseY = sy * sectionSize; if (baseY >= dimY) break;
                        int maxLocalY = Math.Min(sectionSize, dimY - baseY);

                        var sec = sections[sx, sy, sz];
                        if (sec == null || sec.NonAirCount == 0 || sec.Kind == ChunkSection.RepresentationKind.Empty) continue;

                        bool isOccludedFull = occludedFull[sx, sy, sz] && sec.CompletelyFull;
                        if (isOccludedFull)
                        {
                            // Preserve actual uniform block if available.
                            ushort solid = sec.Kind == ChunkSection.RepresentationKind.Uniform ? sec.UniformBlockId : (ushort)BaseBlockType.Stone;
                            int voxels = maxLocalX * maxLocalZ * maxLocalY;
                            nonAirTotal += voxels;
                            for (int lx = 0; lx < maxLocalX; lx++)
                            {
                                int gx = baseX + lx; int destXBase = gx * strideX;
                                for (int lz = 0; lz < maxLocalZ; lz++)
                                {
                                    int gz = baseZ + lz;
                                    dest.AsSpan(destXBase + gz * strideZ + baseY, maxLocalY).Fill(solid);
                                }
                            }
                            // Uniform full section => bounds are whole local range
                            if (!stats.HasStats)
                            {
                                stats.MinX = baseX; stats.MaxX = baseX + maxLocalX - 1;
                                stats.MinY = baseY; stats.MaxY = baseY + maxLocalY - 1;
                                stats.MinZ = baseZ; stats.MaxZ = baseZ + maxLocalZ - 1;
                                stats.HasStats = true;
                            }
                            else
                            {
                                if (baseX < stats.MinX) stats.MinX = baseX; if (baseX + maxLocalX - 1 > stats.MaxX) stats.MaxX = baseX + maxLocalX - 1;
                                if (baseY < stats.MinY) stats.MinY = baseY; if (baseY + maxLocalY - 1 > stats.MaxY) stats.MaxY = baseY + maxLocalY - 1;
                                if (baseZ < stats.MinZ) stats.MinZ = baseZ; if (baseZ + maxLocalZ - 1 > stats.MaxZ) stats.MaxZ = baseZ + maxLocalZ - 1;
                            }
                            // Slice stamping (accurate unique counts)
                            for (int gx = baseX; gx < baseX + maxLocalX; gx++) if (xSliceStamp[gx] != sliceStampVersion) { xSliceStamp[gx] = sliceStampVersion; stats.XNonEmpty++; }
                            for (int gy = baseY; gy < baseY + maxLocalY; gy++) if (ySliceStamp[gy] != sliceStampVersion) { ySliceStamp[gy] = sliceStampVersion; stats.YNonEmpty++; }
                            for (int gz = baseZ; gz < baseZ + maxLocalZ; gz++) if (zSliceStamp[gz] != sliceStampVersion) { zSliceStamp[gz] = sliceStampVersion; stats.ZNonEmpty++; }
                            continue;
                        }

                        // Ensure metadata built (in case of on-demand classification earlier not executed)
                        if (!sec.MetadataBuilt)
                        {
                            MVGE_GEN.Utils.SectionUtils.ClassifyRepresentation(sec);
                        }

                        nonAirTotal += sec.NonAirCount;
                        exposureInternalSum += sec.InternalExposure;

                        // Update global bounds from section-local bounds
                        if (sec.HasBounds)
                        {
                            int gMinX = baseX + sec.MinLX;
                            int gMaxX = baseX + sec.MaxLX;
                            int gMinY = baseY + sec.MinLY;
                            int gMaxY = baseY + sec.MaxLY;
                            int gMinZ = baseZ + sec.MinLZ;
                            int gMaxZ = baseZ + sec.MaxLZ;
                            if (!stats.HasStats)
                            {
                                stats.MinX = gMinX; stats.MaxX = gMaxX;
                                stats.MinY = gMinY; stats.MaxY = gMaxY;
                                stats.MinZ = gMinZ; stats.MaxZ = gMaxZ;
                                stats.HasStats = true;
                            }
                            else
                            {
                                if (gMinX < stats.MinX) stats.MinX = gMinX; if (gMaxX > stats.MaxX) stats.MaxX = gMaxX;
                                if (gMinY < stats.MinY) stats.MinY = gMinY; if (gMaxY > stats.MaxY) stats.MaxY = gMaxY;
                                if (gMinZ < stats.MinZ) stats.MinZ = gMinZ; if (gMaxZ > stats.MaxZ) stats.MaxZ = gMaxZ;
                            }
                            // Accurate slice stamping per axis using real bounds (no overestimation)
                            for (int gx = gMinX; gx <= gMaxX; gx++) if (xSliceStamp[gx] != sliceStampVersion) { xSliceStamp[gx] = sliceStampVersion; stats.XNonEmpty++; }
                            for (int gy = gMinY; gy <= gMaxY; gy++) if (ySliceStamp[gy] != sliceStampVersion) { ySliceStamp[gy] = sliceStampVersion; stats.YNonEmpty++; }
                            for (int gz = gMinZ; gz <= gMaxZ; gz++) if (zSliceStamp[gz] != sliceStampVersion) { zSliceStamp[gz] = sliceStampVersion; stats.ZNonEmpty++; }
                        }

                        // Boundary solidity flag (cheap) – if section touches chunk boundary and has any voxel on that face
                        if (!HasAnyBoundarySolid)
                        {
                            if ((baseX == 0 && sec.MinLX == 0) || (baseX + maxLocalX == dimX && sec.MaxLX == sectionSize - 1) ||
                                (baseY == 0 && sec.MinLY == 0) || (baseY + maxLocalY == dimY && sec.MaxLY == sectionSize - 1) ||
                                (baseZ == 0 && sec.MinLZ == 0) || (baseZ + maxLocalZ == dimZ && sec.MaxLZ == sectionSize - 1))
                            {
                                HasAnyBoundarySolid = true;
                            }
                        }

                        // Write voxel data fast based on representation
                        switch (sec.Kind)
                        {
                            case ChunkSection.RepresentationKind.Uniform:
                                {
                                    ushort solid = sec.UniformBlockId;
                                    for (int lx = 0; lx < maxLocalX; lx++)
                                    {
                                        int gx = baseX + lx; int destXBase = gx * strideX;
                                        for (int lz = 0; lz < maxLocalZ; lz++)
                                        {
                                            int gz = baseZ + lz;
                                            dest.AsSpan(destXBase + gz * strideZ + baseY, maxLocalY).Fill(solid);
                                        }
                                    }
                                    break;
                                }
                            case ChunkSection.RepresentationKind.DenseExpanded:
                                {
                                    var arr = sec.ExpandedDense;
                                    for (int lx = 0; lx < maxLocalX; lx++)
                                    {
                                        int gx = baseX + lx; int destXBase = gx * strideX;
                                        for (int lz = 0; lz < maxLocalZ; lz++)
                                        {
                                            int gz = baseZ + lz; int destBase = destXBase + gz * strideZ + baseY;
                                            int columnIndex = lz * sectionSize + lx; // z*S + x
                                            int li = columnIndex << 4; // y=0
                                            for (int ly = 0; ly < maxLocalY; ly++, li++)
                                            {
                                                ushort id = arr[li];
                                                if (id != ChunkSection.AIR) dest[destBase + ly] = id;
                                            }
                                        }
                                    }
                                    break;
                                }
                            case ChunkSection.RepresentationKind.Sparse:
                                {
                                    var idx = sec.SparseIndices; var blocks = sec.SparseBlocks;
                                    for (int i = 0; i < idx.Length; i++)
                                    {
                                        int li = idx[i];
                                        int y = li & 15; int columnIndex = li >> 4; int x = columnIndex & 15; int z = columnIndex >> 4;
                                        if (x >= maxLocalX || y >= maxLocalY || z >= maxLocalZ) continue;
                                        int gx = baseX + x; int gy = baseY + y; int gz = baseZ + z;
                                        dest[gx * strideX + gz * strideZ + gy] = blocks[i];
                                    }
                                    break;
                                }
                            case ChunkSection.RepresentationKind.Packed:
                                {
                                    var occ = sec.OccupancyBits;
                                    if (occ == null)
                                    {
                                        goto case ChunkSection.RepresentationKind.Uniform;
                                    }
                                    int bpi = sec.BitsPerIndex; var bitData = sec.BitData; var palette = sec.Palette; int maskLocal = (1 << bpi) - 1;
                                    for (int word = 0; word < occ.Length; word++)
                                    {
                                        ulong w = occ[word];
                                        while (w != 0)
                                        {
                                            int bit = System.Numerics.BitOperations.TrailingZeroCount(w);
                                            int li = (word << 6) + bit;
                                            int y = li & 15; int columnIndex = li >> 4; int x = columnIndex & 15; int z = columnIndex >> 4;
                                            if (x >= maxLocalX || y >= maxLocalY || z >= maxLocalZ) { w &= w - 1; continue; }
                                            // Inline packed palette index decode
                                            long bitPos = (long)li * bpi;
                                            int dataIndex = (int)(bitPos >> 5);
                                            int bitOffset = (int)(bitPos & 31);
                                            uint value = bitData[dataIndex] >> bitOffset;
                                            int remaining = 32 - bitOffset;
                                            if (remaining < bpi) value |= bitData[dataIndex + 1] << remaining;
                                            int paletteIndex = (int)(value & (uint)maskLocal);
                                            ushort blockId = palette[paletteIndex];
                                            int gx = baseX + x; int gy = baseY + y; int gz = baseZ + z;
                                            dest[gx * strideX + gz * strideZ + gy] = blockId;
                                            w &= w - 1;
                                        }
                                    }
                                    break;
                                }
                        }

                        // Accumulate slice counts approximately (use section bounds). This is cheaper than per-voxel stamps.
                        if (sec.HasBounds)
                        {
                            stats.XNonEmpty += (sec.MaxLX - sec.MinLX + 1);
                            stats.YNonEmpty += (sec.MaxLY - sec.MinLY + 1);
                            stats.ZNonEmpty += (sec.MaxLZ - sec.MinLZ + 1);
                        }
                    }
                }
            }

            // Cross-section shared face adjustment (subtract 2 per shared solid pair) using face masks
            for (int sx = 0; sx < sectionsX - 1; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var a = sections[sx, sy, sz]; var b = sections[sx + 1, sy, sz];
                        if (a == null || b == null || a.NonAirCount == 0 || b.NonAirCount == 0) continue;
                        var fa = a.FacePosXBits; var fb = b.FaceNegXBits;
                        if (fa != null && fb != null)
                        {
                            for (int w = 0; w < fa.Length; w++) sharedAdjX += System.Numerics.BitOperations.PopCount(fa[w] & fb[w]);
                        }
                        else if (a.Kind == ChunkSection.RepresentationKind.Uniform && b.Kind == ChunkSection.RepresentationKind.Uniform)
                        {
                            sharedAdjX += 16 * 16; // full face
                        }
                    }
                }
            }
            for (int sy = 0; sy < sectionsY - 1; sy++)
            {
                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var a = sections[sx, sy, sz]; var b = sections[sx, sy + 1, sz];
                        if (a == null || b == null || a.NonAirCount == 0 || b.NonAirCount == 0) continue;
                        var fa = a.FacePosYBits; var fb = b.FaceNegYBits;
                        if (fa != null && fb != null)
                        {
                            for (int w = 0; w < fa.Length; w++) sharedAdjY += System.Numerics.BitOperations.PopCount(fa[w] & fb[w]);
                        }
                        else if (a.Kind == ChunkSection.RepresentationKind.Uniform && b.Kind == ChunkSection.RepresentationKind.Uniform)
                        {
                            sharedAdjY += 16 * 16;
                        }
                    }
                }
            }
            for (int sz = 0; sz < sectionsZ - 1; sz++)
            {
                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sy = 0; sy < sectionsY; sy++)
                    {
                        var a = sections[sx, sy, sz]; var b = sections[sx, sy, sz + 1];
                        if (a == null || b == null || a.NonAirCount == 0 || b.NonAirCount == 0) continue;
                        var fa = a.FacePosZBits; var fb = b.FaceNegZBits;
                        if (fa != null && fb != null)
                        {
                            for (int w = 0; w < fa.Length; w++) sharedAdjZ += System.Numerics.BitOperations.PopCount(fa[w] & fb[w]);
                        }
                        else if (a.Kind == ChunkSection.RepresentationKind.Uniform && b.Kind == ChunkSection.RepresentationKind.Uniform)
                        {
                            sharedAdjZ += 16 * 16;
                        }
                    }
                }
            }

            long exposure = exposureInternalSum - 2L * (sharedAdjX + sharedAdjY + sharedAdjZ);
            stats.SolidCount = nonAirTotal;
            stats.ExposureEstimate = (int)Math.Min(int.MaxValue, exposure);
            MeshPrepassStats = stats;
            IsEmpty = nonAirTotal == 0;
            return nonAirTotal;
        }

        public void BuildRender(Func<int, int, int, ushort> worldBlockGetter)
        {
            chunkRender?.ScheduleDelete();

            // Unified occlusion early exit
            if (AllAirChunk || OcclusionStatus != OcclusionClass.None)
            {
                return; // leave chunkRender null
            }

            // Two-phase stats shortcut: if chunk is uniform single block we can skip full flatten
            if (AllOneBlockChunk)
            {
                int vol = dimX * dimY * dimZ;
                // Analytical exposure estimate for solid rectangular prism fully filled
                long internalAdj = (long)(dimX - 1) * dimY * dimZ + (long)dimX * (dimZ - 1) * dimY + (long)dimX * dimZ * (dimY - 1);
                int exposure = (int)(6L * vol - 2L * internalAdj);
                MeshPrepassStats = new ChunkMeshPrepassStats
                {
                    SolidCount = vol,
                    ExposureEstimate = exposure,
                    MinX = 0,
                    MinY = 0,
                    MinZ = 0,
                    MaxX = dimX - 1,
                    MaxY = dimY - 1,
                    MaxZ = dimZ - 1,
                    XNonEmpty = dimX,
                    YNonEmpty = dimY,
                    ZNonEmpty = dimZ,
                    HasStats = true
                };

                // Provide a fully-populated flat array so ChunkRender logic that still indexes flatBlocks (occlusion checks etc.) is safe.
                int voxelCountUniform = vol;
                ushort[] uniformFlat = ArrayPool<ushort>.Shared.Rent(voxelCountUniform);
                // Fill with uniform block id
                uniformFlat.AsSpan(0, voxelCountUniform).Fill(AllOneBlockBlockId);

                var prerender = BuildPrerenderData(uniformFlat);
                chunkRender = new ChunkRender(prerender);
                return;
            }

            int voxelCount = dimX * dimY * dimZ;
            ushort[] flat = ArrayPool<ushort>.Shared.Rent(voxelCount);
            // Removed full zero init (empty block == 0); per-section zero fill applied selectively in FlattenSections
            int nonAir = FlattenSections(flat);

            if (nonAir == 0)
            {
                ArrayPool<ushort>.Shared.Return(flat, false);
                return; // chunkRender stays null; Render() will no-op
            }

            var prerenderData = BuildPrerenderData(flat);
            chunkRender = new ChunkRender(prerenderData);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureSliceStampArrays()
        {
            xSliceStamp ??= new int[dimX];
            ySliceStamp ??= new int[dimY];
            zSliceStamp ??= new int[dimZ];
            // Handle rare int overflow / wrap
            if (sliceStampVersion == int.MaxValue)
            {
                Array.Clear(xSliceStamp);
                Array.Clear(ySliceStamp);
                Array.Clear(zSliceStamp);
                sliceStampVersion = 0;
            }
            sliceStampVersion++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkSlices(ref ChunkMeshPrepassStats stats, int gx, int gy, int gz)
        {
            if (xSliceStamp[gx] != sliceStampVersion) { xSliceStamp[gx] = sliceStampVersion; stats.XNonEmpty++; }
            if (ySliceStamp[gy] != sliceStampVersion) { ySliceStamp[gy] = sliceStampVersion; stats.YNonEmpty++; }
            if (zSliceStamp[gz] != sliceStampVersion) { zSliceStamp[gz] = sliceStampVersion; stats.ZNonEmpty++; }
        }
    }
}

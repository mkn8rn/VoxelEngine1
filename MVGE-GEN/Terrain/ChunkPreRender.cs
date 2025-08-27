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

        internal ChunkPrerenderData BuildPrerenderData()
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
                NeighborPlanePosZ = NeighborPlanePosZFace
            };
        }

        private int FlattenSections(ushort[] dest)
        {
            int strideX = dimZ * dimY; // (x * dimZ + z) * dimY + y
            int strideZ = dimY;
            int sectionSize = ChunkSection.SECTION_SIZE;
            int sectionPlane = sectionSize * sectionSize; // 256

            int nonAirTotal = 0;
            HasAnyBoundarySolid = false;

            ChunkMeshPrepassStats localStats = default;
            long exposureEstimate = 0; // keep as long while accumulating to avoid overflow, cast later

            EnsureSliceStampArrays();

            // --- GLOBAL CLEAR ONCE ---
            // Instead of clearing per-section (which caused large redundant memory writes),
            // clear the entire destination buffer once. This allows early-outs for empty sections
            // without touching their memory region again.
            int totalVoxels = dimX * dimY * dimZ;
            dest.AsSpan(0, totalVoxels).Clear();

            // Iterate sections (X,Z,Y) – keeps inner loops tight & predictable
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
                        // Completely empty / all air – EARLY OUT: no per-column clears needed (already zeroed globally)
                        if (sec == null || sec.IsAllAir || sec.NonAirCount == 0 || sec.Kind == ChunkSection.RepresentationKind.Empty)
                        {
                            continue;
                        }

                        switch (sec.Kind)
                        {
                            case ChunkSection.RepresentationKind.Uniform:
                            {
                                ushort solid = sec.UniformBlockId;
                                int voxels = maxLocalX * maxLocalZ * maxLocalY;
                                nonAirTotal += voxels;
                                // Bounds
                                int minGX = baseX; int maxGX = baseX + maxLocalX - 1;
                                int minGY = baseY; int maxGY = baseY + maxLocalY - 1;
                                int minGZ = baseZ; int maxGZ = baseZ + maxLocalZ - 1;
                                if (!localStats.HasStats)
                                {
                                    localStats.MinX = minGX; localStats.MaxX = maxGX;
                                    localStats.MinY = minGY; localStats.MaxY = maxGY;
                                    localStats.MinZ = minGZ; localStats.MaxZ = maxGZ;
                                    localStats.HasStats = true;
                                }
                                else
                                {
                                    if (minGX < localStats.MinX) localStats.MinX = minGX;
                                    if (maxGX > localStats.MaxX) localStats.MaxX = maxGX;
                                    if (minGY < localStats.MinY) localStats.MinY = minGY;
                                    if (maxGY > localStats.MaxY) localStats.MaxY = maxGY;
                                    if (minGZ < localStats.MinZ) localStats.MinZ = minGZ;
                                    if (maxGZ > localStats.MaxZ) localStats.MaxZ = maxGZ;
                                }
                                for (int gx = minGX; gx <= maxGX; gx++) if (xSliceStamp[gx] != sliceStampVersion) { xSliceStamp[gx] = sliceStampVersion; localStats.XNonEmpty++; }
                                for (int gy = minGY; gy <= maxGY; gy++) if (ySliceStamp[gy] != sliceStampVersion) { ySliceStamp[gy] = sliceStampVersion; localStats.YNonEmpty++; }
                                for (int gz = minGZ; gz <= maxGZ; gz++) if (zSliceStamp[gz] != sliceStampVersion) { zSliceStamp[gz] = sliceStampVersion; localStats.ZNonEmpty++; }
                                long lenX = maxLocalX; long lenY = maxLocalY; long lenZ = maxLocalZ;
                                long N = lenX * lenY * lenZ;
                                long internalAdj = (lenX - 1) * lenY * lenZ + lenX * (lenZ - 1) * lenY + lenX * lenZ * (lenY - 1);
                                exposureEstimate += 6 * N - 2 * internalAdj;
                                if (!HasAnyBoundarySolid && (minGX == 0 || minGY == 0 || minGZ == 0 || maxGX == dimX - 1 || maxGY == dimY - 1 || maxGZ == dimZ - 1)) HasAnyBoundarySolid = true;
                                for (int lx = 0; lx < maxLocalX; lx++)
                                {
                                    int gx = baseX + lx; int destXBase = gx * strideX;
                                    for (int lz = 0; lz < maxLocalZ; lz++)
                                    {
                                        int gz = baseZ + lz;
                                        dest.AsSpan(destXBase + gz * strideZ + baseY, maxLocalY).Fill(solid);
                                    }
                                }
                                continue;
                            }
                        }

                        // Legacy packed path (with global clear optimization)
                        // Uniform re-check retained for safety on Packed uniform sections
                        if (sec.Palette != null &&
                            sec.Palette.Count == 2 &&
                            sec.Palette[0] == ChunkSection.AIR &&
                            sec.NonAirCount == sec.VoxelCount &&
                            sec.VoxelCount != 0)
                        {
                            ushort solid = sec.Palette[1];
                            int voxels = maxLocalX * maxLocalZ * maxLocalY;
                            nonAirTotal += voxels;
                            int minGX = baseX; int maxGX = baseX + maxLocalX - 1;
                            int minGY = baseY; int maxGY = baseY + maxLocalY - 1;
                            int minGZ = baseZ; int maxGZ = baseZ + maxLocalZ - 1;
                            if (!localStats.HasStats)
                            {
                                localStats.MinX = minGX; localStats.MaxX = maxGX;
                                localStats.MinY = minGY; localStats.MaxY = maxGY;
                                localStats.MinZ = minGZ; localStats.MaxZ = maxGZ;
                                localStats.HasStats = true;
                            }
                            else
                            {
                                if (minGX < localStats.MinX) localStats.MinX = minGX;
                                if (maxGX > localStats.MaxX) localStats.MaxX = maxGX;
                                if (minGY < localStats.MinY) localStats.MinY = minGY;
                                if (maxGY > localStats.MaxY) localStats.MaxY = maxGY;
                                if (minGZ < localStats.MinZ) localStats.MinZ = minGZ;
                                if (maxGZ > localStats.MaxZ) localStats.MaxZ = maxGZ;
                            }
                            for (int gx = minGX; gx <= maxGX; gx++) if (xSliceStamp[gx] != sliceStampVersion) { xSliceStamp[gx] = sliceStampVersion; localStats.XNonEmpty++; }
                            for (int gy = minGY; gy <= maxGY; gy++) if (ySliceStamp[gy] != sliceStampVersion) { ySliceStamp[gy] = sliceStampVersion; localStats.YNonEmpty++; }
                            for (int gz = minGZ; gz <= maxGZ; gz++) if (zSliceStamp[gz] != sliceStampVersion) { zSliceStamp[gz] = sliceStampVersion; localStats.ZNonEmpty++; }
                            long lenX = maxLocalX; long lenY = maxLocalY; long lenZ = maxLocalZ; long N = lenX * lenY * lenZ;
                            long internalAdj = (lenX - 1) * lenY * lenZ + lenX * (lenZ - 1) * lenY + lenX * lenZ * (lenY - 1);
                            exposureEstimate += 6 * N - 2 * internalAdj;
                            if (!HasAnyBoundarySolid && (minGX == 0 || minGY == 0 || minGZ == 0 || maxGX == dimX - 1 || maxGY == dimY - 1 || maxGZ == dimZ - 1)) HasAnyBoundarySolid = true;
                            for (int lx = 0; lx < maxLocalX; lx++)
                            {
                                int gx = baseX + lx; int destXBase = gx * strideX;
                                for (int lz = 0; lz < maxLocalZ; lz++)
                                {
                                    int gz = baseZ + lz;
                                    dest.AsSpan(destXBase + gz * strideZ + baseY, maxLocalY).Fill(solid);
                                }
                            }
                            continue;
                        }

                        int bitsPer = sec.BitsPerIndex;
                        uint[] bitData = sec.BitData;
                        var palette = sec.Palette;
                        if (bitsPer == 0 || bitData == null || palette == null) continue;
                        int mask = (1 << bitsPer) - 1;
                        int strideBitsY = sectionPlane * bitsPer;

                        // Decode per (x,z) column
                        for (int lx = 0; lx < maxLocalX; lx++)
                        {
                            int gx = baseX + lx; int destXBase = gx * strideX;
                            bool boundaryX = (gx == 0) || (gx == dimX - 1);

                            for (int lz = 0; lz < maxLocalZ; lz++)
                            {
                                int gz = baseZ + lz; int destZBase = destXBase + gz * strideZ;
                                bool boundaryXZ = boundaryX || gz == 0 || gz == dimZ - 1;

                                int baseXZ = lz * sectionSize + lx;
                                long baseBitPos = (long)baseXZ * bitsPer;
                                long bitPos = baseBitPos;
                                for (int ly = 0; ly < maxLocalY; ly++, bitPos += strideBitsY)
                                {
                                    int gy = baseY + ly;
                                    int dataIndex = (int)(bitPos >> 5);
                                    int bitOffset = (int)(bitPos & 31);
                                    uint word = bitData[dataIndex];
                                    uint value = word >> bitOffset;
                                    int used = 32 - bitOffset;
                                    if (used < bitsPer) value |= bitData[dataIndex + 1] << used;
                                    ushort id = palette[(int)(value & (uint)mask)];
                                    if (id == ChunkSection.AIR) continue;

                                    dest[destZBase + gy] = id;
                                    nonAirTotal++;
                                    if (!HasAnyBoundarySolid && (boundaryXZ || gy == 0 || gy == dimY - 1)) HasAnyBoundarySolid = true;
                                    if (!localStats.HasStats)
                                    {
                                        localStats.MinX = localStats.MaxX = gx;
                                        localStats.MinY = localStats.MaxY = gy;
                                        localStats.MinZ = localStats.MaxZ = gz;
                                        localStats.HasStats = true;
                                    }
                                    else
                                    {
                                        if (gx < localStats.MinX) localStats.MinX = gx; else if (gx > localStats.MaxX) localStats.MaxX = gx;
                                        if (gy < localStats.MinY) localStats.MinY = gy; else if (gy > localStats.MaxY) localStats.MaxY = gy;
                                        if (gz < localStats.MinZ) localStats.MinZ = gz; else if (gz > localStats.MaxZ) localStats.MaxZ = gz;
                                    }
                                    MarkSlices(ref localStats, gx, gy, gz);
                                    exposureEstimate += 6;
                                    if (gx > 0 && dest[(gx - 1) * strideX + gz * strideZ + gy] != ChunkSection.AIR) exposureEstimate -= 2;
                                    if (gz > 0 && dest[gx * strideX + (gz - 1) * strideZ + gy] != ChunkSection.AIR) exposureEstimate -= 2;
                                    if (gy > 0 && dest[gx * strideX + gz * strideZ + (gy - 1)] != ChunkSection.AIR) exposureEstimate -= 2;
                                }
                            }
                        }
                    }
                }
            }

            IsEmpty = nonAirTotal == 0;
            localStats.SolidCount = nonAirTotal;
            localStats.ExposureEstimate = (int)Math.Min(int.MaxValue, exposureEstimate);
            MeshPrepassStats = localStats;

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

                var prerender = BuildPrerenderData();
                chunkRender = new ChunkRender(
                    chunkData,
                    uniformFlat,
                    dimX,
                    dimY,
                    dimZ,
                    prerender);
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

            var prerenderData = BuildPrerenderData();
            chunkRender = new ChunkRender(
                chunkData,
                flat,
                dimX,
                dimY,
                dimZ,
                prerenderData);
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

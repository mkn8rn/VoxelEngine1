using MVGE_GFX.Terrain;
using MVGE_INF.Generation.Models;
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

        internal ChunkPrerenderData BuildPrerenderData(SectionPrerenderDesc[] sectionDescs)
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
                SectionDescs = sectionDescs,
                sectionsX = sectionsX,
                sectionsY = sectionsY,
                sectionsZ = sectionsZ,
                sectionSize = ChunkSection.SECTION_SIZE,
                maxX = dimX,
                maxY = dimY,
                maxZ = dimZ
            };
        }

        private SectionPrerenderDesc[] BuildSectionDescriptors()
        {
            int total = sectionsX * sectionsY * sectionsZ;
            var arr = new SectionPrerenderDesc[total];
            int idx = 0;
            int S = ChunkSection.SECTION_SIZE;
            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++, idx++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null)
                        {
                            arr[idx] = default;
                            continue;
                        }
                        arr[idx] = new SectionPrerenderDesc
                        {
                            Kind = (byte)sec.Kind,
                            UniformBlockId = sec.UniformBlockId,
                            NonAirCount = sec.NonAirCount,
                            SparseIndices = sec.SparseIndices,
                            SparseBlocks = sec.SparseBlocks,
                            ExpandedDense = sec.ExpandedDense,
                            PackedBitData = sec.BitData,
                            Palette = sec.Palette,
                            BitsPerIndex = sec.BitsPerIndex,
                            OccupancyBits = sec.OccupancyBits,
                            FaceNegXBits = sec.FaceNegXBits,
                            FacePosXBits = sec.FacePosXBits,
                            FaceNegYBits = sec.FaceNegYBits,
                            FacePosYBits = sec.FacePosYBits,
                            FaceNegZBits = sec.FaceNegZBits,
                            FacePosZBits = sec.FacePosZBits,
                            HasBounds = sec.HasBounds,
                            MinLX = sec.MinLX,
                            MinLY = sec.MinLY,
                            MinLZ = sec.MinLZ,
                            MaxLX = sec.MaxLX,
                            MaxLY = sec.MaxLY,
                            MaxLZ = sec.MaxLZ,
                            SectionBaseX = sx * S,
                            SectionBaseY = sy * S,
                            SectionBaseZ = sz * S
                        };
                    }
                }
            }
            return arr;
        }

        public void BuildRender(Func<int, int, int, ushort> worldBlockGetter)
        {
            chunkRender?.ScheduleDelete();

            if (AllAirChunk || OcclusionStatus != OcclusionClass.None)
            {
                return;
            }

            if (AllOneBlockChunk)
            {
                int vol = dimX * dimY * dimZ;
                long internalAdj = (long)(dimX - 1) * dimY * dimZ + (long)dimX * (dimZ - 1) * dimY + (long)dimX * dimZ * (dimY - 1);
                int exposure = (int)(6L * vol - 2L * internalAdj);
                MeshPrepassStats = new ChunkMeshPrepassStats
                {
                    SolidCount = vol,
                    ExposureEstimate = exposure,
                    MinX = 0, MinY = 0, MinZ = 0,
                    MaxX = dimX - 1, MaxY = dimY - 1, MaxZ = dimZ - 1,
                    XNonEmpty = dimX, YNonEmpty = dimY, ZNonEmpty = dimZ, HasStats = true
                };
                var prerenderUniform = BuildPrerenderData(BuildSectionDescriptors());
                chunkRender = new ChunkRender(prerenderUniform);
                return;
            }

            // Aggregate stats directly from sections (no flatten)
            long internalExposureSum = 0;
            int totalNonAir = 0;
            bool boundsInit = false;
            int gMinX = 0, gMinY = 0, gMinZ = 0, gMaxX = 0, gMaxY = 0, gMaxZ = 0;
            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null || sec.NonAirCount == 0) continue;
                        totalNonAir += sec.NonAirCount;
                        internalExposureSum += sec.InternalExposure;
                        if (sec.HasBounds)
                        {
                            int baseX = sx * ChunkSection.SECTION_SIZE;
                            int baseY = sy * ChunkSection.SECTION_SIZE;
                            int baseZ = sz * ChunkSection.SECTION_SIZE;
                            int sMinX = baseX + sec.MinLX; int sMaxX = baseX + sec.MaxLX;
                            int sMinY = baseY + sec.MinLY; int sMaxY = baseY + sec.MaxLY;
                            int sMinZ = baseZ + sec.MinLZ; int sMaxZ = baseZ + sec.MaxLZ;
                            if (!boundsInit)
                            {
                                gMinX = sMinX; gMaxX = sMaxX; gMinY = sMinY; gMaxY = sMaxY; gMinZ = sMinZ; gMaxZ = sMaxZ; boundsInit = true;
                            }
                            else
                            {
                                if (sMinX < gMinX) gMinX = sMinX; if (sMaxX > gMaxX) gMaxX = sMaxX;
                                if (sMinY < gMinY) gMinY = sMinY; if (sMaxY > gMaxY) gMaxY = sMaxY;
                                if (sMinZ < gMinZ) gMinZ = sMinZ; if (sMaxZ > gMaxZ) gMaxZ = sMaxZ;
                            }
                        }
                    }
                }
            }
            MeshPrepassStats = new ChunkMeshPrepassStats
            {
                SolidCount = totalNonAir,
                ExposureEstimate = (int)Math.Min(int.MaxValue, internalExposureSum),
                MinX = gMinX, MinY = gMinY, MinZ = gMinZ,
                MaxX = gMaxX, MaxY = gMaxY, MaxZ = gMaxZ,
                XNonEmpty = dimX, // coarse placeholders
                YNonEmpty = dimY,
                ZNonEmpty = dimZ,
                HasStats = boundsInit
            };
            if (totalNonAir == 0) return;
            var prerender = BuildPrerenderData(BuildSectionDescriptors());
            chunkRender = new ChunkRender(prerender);
        }
    }
}

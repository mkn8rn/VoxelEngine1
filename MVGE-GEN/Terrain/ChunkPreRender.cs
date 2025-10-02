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
using MVGE_INF.Loaders;
using System.Numerics;
using MVGE_GEN.Utils;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        // ----- Added plane caches for pre-render handoff -----
        // Layouts:
        //  Neg/Pos X: YZ plane (index = z * dimY + y)
        //  Neg/Pos Y: XZ plane (index = x * dimZ + z)
        //  Neg/Pos Z: XY plane (index = x * dimY + y)
        internal ulong[] PlaneNegX; // this chunk's -X face (opaque occupancy bits)
        internal ulong[] PlanePosX; // +X face (opaque occupancy bits)
        internal ulong[] PlaneNegY; // -Y face (opaque occupancy bits)
        internal ulong[] PlanePosY; // +Y face (opaque occupancy bits)
        internal ulong[] PlaneNegZ; // -Z face (opaque occupancy bits)
        internal ulong[] PlanePosZ; // +Z face (opaque occupancy bits)

        // Transparent boundary id maps. Each array stores per-boundary-cell transparent block ids (ushort) or 0 when absent.
        // Allocated only when at least one transparent (non-air, non-opaque) voxel exists on that face.
        // Layouts mirror the opaque plane layouts above for direct index reuse:
        //  Neg/Pos X: size dimY * dimZ (index = z * dimY + y)
        //  Neg/Pos Y: size dimX * dimZ (index = x * dimZ + z)
        //  Neg/Pos Z: size dimX * dimY (index = x * dimY + y)
        internal ushort[] TransparentPlaneNegX; // transparent ids along -X boundary
        internal ushort[] TransparentPlanePosX; // transparent ids along +X boundary
        internal ushort[] TransparentPlaneNegY; // transparent ids along -Y boundary
        internal ushort[] TransparentPlanePosY; // transparent ids along +Y boundary
        internal ushort[] TransparentPlaneNegZ; // transparent ids along -Z boundary
        internal ushort[] TransparentPlanePosZ; // transparent ids along +Z boundary

        // Neighbor plane caches (populated by WorldResources just before BuildRender)
        internal ulong[] NeighborPlaneNegXFace; // neighbor at -X (+X face of neighbor)
        internal ulong[] NeighborPlanePosXFace; // neighbor at +X (-X face of neighbor)
        internal ulong[] NeighborPlaneNegYFace; // neighbor at -Y (+Y face)
        internal ulong[] NeighborPlanePosYFace; // neighbor at +Y (-Y face)
        internal ulong[] NeighborPlaneNegZFace; // neighbor at -Z (+Z face)
        internal ulong[] NeighborPlanePosZFace; // neighbor at +Z (-Z face)

        // Neighbor transparent boundary id maps (mirroring opaque neighbor plane conventions)
        internal ushort[] NeighborTransparentPlaneNegXFace; // neighbor -X +X transparent ids
        internal ushort[] NeighborTransparentPlanePosXFace; // neighbor +X -X transparent ids
        internal ushort[] NeighborTransparentPlaneNegYFace; // neighbor -Y +Y transparent ids
        internal ushort[] NeighborTransparentPlanePosYFace; // neighbor +Y -Y transparent ids
        internal ushort[] NeighborTransparentPlaneNegZFace; // neighbor -Z +Z transparent ids
        internal ushort[] NeighborTransparentPlanePosZFace; // neighbor +Z -Z transparent ids

        const int S = Section.SECTION_SIZE;

        static void SetPlaneBit(ulong[] plane, int index)
        {
            if (plane == null) return;
            int w = index >> 6;
            int b = index & 63;
            plane[w] |= 1UL << b;
        }

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

        // Allocate transparent boundary ID arrays with proper per-cell sizes (not bit-word counts).
        private void EnsureTransparentPlaneArrays()
        {
            int yzCells = dimY * dimZ;
            int xzCells = dimX * dimZ;
            int xyCells = dimX * dimY;

            TransparentPlaneNegX ??= new ushort[yzCells];
            TransparentPlanePosX ??= new ushort[yzCells];
            TransparentPlaneNegY ??= new ushort[xzCells];
            TransparentPlanePosY ??= new ushort[xzCells];
            TransparentPlaneNegZ ??= new ushort[xyCells];
            TransparentPlanePosZ ??= new ushort[xyCells];
        }

        // Build transparent boundary planes from current voxel data. Non-air, non-opaque block ids are written; others are 0.
        internal void RebuildTransparentBoundaryPlanes()
        {
            EnsureTransparentPlaneArrays();

            // Clear previous content to avoid stale suppression or false seams.
            Array.Clear(TransparentPlaneNegX, 0, TransparentPlaneNegX.Length);
            Array.Clear(TransparentPlanePosX, 0, TransparentPlanePosX.Length);
            Array.Clear(TransparentPlaneNegY, 0, TransparentPlaneNegY.Length);
            Array.Clear(TransparentPlanePosY, 0, TransparentPlanePosY.Length);
            Array.Clear(TransparentPlaneNegZ, 0, TransparentPlaneNegZ.Length);
            Array.Clear(TransparentPlanePosZ, 0, TransparentPlanePosZ.Length);

            // -X / +X faces (YZ layout: index = z * dimY + y)
            for (int y = 0; y < dimY; y++)
            {
                for (int z = 0; z < dimZ; z++)
                {
                    int idxYZ = z * dimY + y;

                    ushort idNX = GetBlockLocal(0, y, z);
                    if (idNX != 0 && !TerrainLoader.IsOpaque(idNX))
                        TransparentPlaneNegX[idxYZ] = idNX;

                    ushort idPX = GetBlockLocal(dimX - 1, y, z);
                    if (idPX != 0 && !TerrainLoader.IsOpaque(idPX))
                        TransparentPlanePosX[idxYZ] = idPX;
                }
            }

            // -Y / +Y faces (XZ layout: index = x * dimZ + z)
            for (int x = 0; x < dimX; x++)
            {
                for (int z = 0; z < dimZ; z++)
                {
                    int idxXZ = x * dimZ + z;

                    ushort idNY = GetBlockLocal(x, 0, z);
                    if (idNY != 0 && !TerrainLoader.IsOpaque(idNY))
                        TransparentPlaneNegY[idxXZ] = idNY;

                    ushort idPY = GetBlockLocal(x, dimY - 1, z);
                    if (idPY != 0 && !TerrainLoader.IsOpaque(idPY))
                        TransparentPlanePosY[idxXZ] = idPY;
                }
            }

            // -Z / +Z faces (XY layout: index = x * dimY + y)
            for (int x = 0; x < dimX; x++)
            {
                for (int y = 0; y < dimY; y++)
                {
                    int idxXY = x * dimY + y;

                    ushort idNZ = GetBlockLocal(x, y, 0);
                    if (idNZ != 0 && !TerrainLoader.IsOpaque(idNZ))
                        TransparentPlaneNegZ[idxXY] = idNZ;

                    ushort idPZ = GetBlockLocal(x, y, dimZ - 1);
                    if (idPZ != 0 && !TerrainLoader.IsOpaque(idPZ))
                        TransparentPlanePosZ[idxXY] = idPZ;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateBoundaryPlaneBit(int lx, int ly, int lz, ushort blockId)
        {
            // A voxel contributes to boundary plane solidity only if it is opaque (not air / not transparent).
            bool solid = TerrainLoader.IsOpaque(blockId);
            // allocate if not present yet (late creation path after generation)
            EnsurePlaneArrays();
            if (lx == 0)
            {
                int yzIndex = lz * dimY + ly; int w = yzIndex >> 6; int b = yzIndex & 63; ulong mask = 1UL << b;
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
            public int SolidCount;              // opaque voxel count at chunk level
            public int ExposureEstimate;        // exposure estimate over opaque occupancy
            public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ; // inclusive bounds
            public int XNonEmpty, YNonEmpty, ZNonEmpty;     // counts of slices with any opaque voxel
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
                // self transparent planes
                SelfTransparentPlaneNegX = TransparentPlaneNegX,
                SelfTransparentPlanePosX = TransparentPlanePosX,
                SelfTransparentPlaneNegY = TransparentPlaneNegY,
                SelfTransparentPlanePosY = TransparentPlanePosY,
                SelfTransparentPlaneNegZ = TransparentPlaneNegZ,
                SelfTransparentPlanePosZ = TransparentPlanePosZ,
                // neighbor transparent planes
                NeighborTransparentPlaneNegX = NeighborTransparentPlaneNegXFace,
                NeighborTransparentPlanePosX = NeighborTransparentPlanePosXFace,
                NeighborTransparentPlaneNegY = NeighborTransparentPlaneNegYFace,
                NeighborTransparentPlanePosY = NeighborTransparentPlanePosYFace,
                NeighborTransparentPlaneNegZ = NeighborTransparentPlaneNegZFace,
                NeighborTransparentPlanePosZ = NeighborTransparentPlanePosZFace,
                chunkData = chunkData,
                SectionDescs = sectionDescs,
                sectionsX = sectionsX,
                sectionsY = sectionsY,
                sectionsZ = sectionsZ,
                sectionSize = Section.SECTION_SIZE,
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
            int S = Section.SECTION_SIZE;
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

                        // Build transparent palette index list (fast classification set for renderer). Only when palette present.
                        int[] transparentPaletteIndices = null;
                        if (sec.Palette != null && sec.Palette.Count > 0)
                        {
                            List<int> tpi = null;
                            for (int pi = 1; pi < sec.Palette.Count; pi++) // skip air index 0
                            {
                                ushort bid = sec.Palette[pi];
                                if (bid != Section.AIR && !TerrainLoader.IsOpaque(bid))
                                {
                                    (tpi ??= new List<int>(4)).Add(pi);
                                }
                            }
                            if (tpi != null) transparentPaletteIndices = tpi.ToArray();
                        }

                        // For uniform transparent sections ensure TransparentBits allocated (all 4096 bits set) for direct bit iteration.
                        ulong[] uniformTransparentBits = null;
                        if (sec.Kind == Section.RepresentationKind.Uniform && sec.UniformBlockId != Section.AIR && !TerrainLoader.IsOpaque(sec.UniformBlockId))
                        {
                            uniformTransparentBits = new ulong[64];
                            // Set all 4096 bits -> each ulong to all ones
                            for (int w = 0; w < 64; w++) uniformTransparentBits[w] = ulong.MaxValue;
                        }

                        // Dominant transparent id detection (simple heuristic >=90% of transparent voxels) for multi-packed / packed representations.
                        ushort dominantId = 0;
                        int dominantCount = 0;
                        ulong[] dominantBits = null;
                        ulong[] residualBits = sec.TransparentBits;
                        int residualCount = sec.TransparentCount;
                        if (sec.TransparentCount > 0 && sec.Palette != null && transparentPaletteIndices != null && transparentPaletteIndices.Length > 1)
                        {
                            // Tally counts per transparent palette index using bit scans with on-demand decode (acceptable during prerender build).
                            Span<int> perIdCounts = stackalloc int[transparentPaletteIndices.Length]; perIdCounts.Clear();
                            // Build perId bitset lazily only for the candidate; we just count first.
                            for (int w = 0; w < 64; w++)
                            {
                                ulong word = sec.TransparentBits?[w] ?? 0UL;
                                while (word != 0)
                                {
                                    int bit = BitOperations.TrailingZeroCount(word);
                                    word &= word - 1;
                                    int li = (w << 6) + bit;
                                    int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                                    ushort id = SectionUtils.GetBlock(sec, lx, ly, lz);
                                    if (id != 0 && !TerrainLoader.IsOpaque(id))
                                    {
                                        int pi = sec.PaletteLookup != null && sec.PaletteLookup.TryGetValue(id, out int pidx) ? pidx : -1;
                                        if (pi >= 0)
                                        {
                                            int localListIndex = Array.IndexOf(transparentPaletteIndices, pi);
                                            if (localListIndex >= 0) perIdCounts[localListIndex]++;
                                        }
                                    }
                                }
                            }
                            // Find dominant
                            int threshold = (int)(sec.TransparentCount * 0.9f);
                            int bestIdx = -1; int best = 0;
                            for (int i = 0; i < perIdCounts.Length; i++) if (perIdCounts[i] > best) { best = perIdCounts[i]; bestIdx = i; }
                            if (bestIdx >= 0 && best >= threshold)
                            {
                                int paletteIndex = transparentPaletteIndices[bestIdx];
                                dominantId = sec.Palette[paletteIndex];
                                dominantCount = best;
                                dominantBits = new ulong[64];
                                ulong[] res = new ulong[64];
                                for (int w = 0; w < 64; w++)
                                {
                                    ulong word = sec.TransparentBits[w];
                                    ulong maskWord = 0UL;
                                    if (word != 0)
                                    {
                                        ulong tmp = word;
                                        while (tmp != 0)
                                        {
                                            int bit = BitOperations.TrailingZeroCount(tmp);
                                            tmp &= tmp - 1;
                                            int li = (w << 6) + bit;
                                            int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                                            ushort id = SectionUtils.GetBlock(sec, lx, ly, lz);
                                            if (id == dominantId) maskWord |= 1UL << bit;
                                        }
                                    }
                                    dominantBits[w] = maskWord;
                                    res[w] = word & ~maskWord;
                                }
                                residualBits = res;
                                residualCount = sec.TransparentCount - dominantCount;
                            }
                        }

                        // Precompute per-face tile indices for transparent palette ids (6 each) for fast emission.
                        uint[] transparentFaceTiles = null;
                        if (transparentPaletteIndices != null && transparentPaletteIndices.Length > 0 && sec.Palette != null)
                        {
                            transparentFaceTiles = new uint[transparentPaletteIndices.Length * 6];
                            for (int i = 0; i < transparentPaletteIndices.Length; i++)
                            {
                                ushort bid = sec.Palette[transparentPaletteIndices[i]];
                                // Reuse texture atlas computation pattern used elsewhere via SectionRender.ComputeTileIndex in runtime phase.
                                for (int face = 0; face < 6; face++)
                                {
                                    transparentFaceTiles[i * 6 + face] = 0; // placeholder; actual tile fill deferred to runtime SectionRender (atlas not accessible here)
                                }
                            }
                        }

                        arr[idx] = new SectionPrerenderDesc
                        {
                            Kind = (byte)sec.Kind,
                            UniformBlockId = sec.UniformBlockId,
                            OpaqueCount = sec.OpaqueVoxelCount,
                            ExpandedDense = sec.ExpandedDense,
                            PackedBitData = sec.BitData,
                            Palette = sec.Palette,
                            BitsPerIndex = sec.BitsPerIndex,
                            OpaqueBits = sec.OpaqueBits,
                            FaceNegXBits = sec.FaceNegXBits,
                            FacePosXBits = sec.FacePosXBits,
                            FaceNegYBits = sec.FaceNegYBits,
                            FacePosYBits = sec.FacePosYBits,
                            FaceNegZBits = sec.FaceNegZBits,
                            FacePosZBits = sec.FacePosZBits,
                            TransparentCount = residualCount,
                            TransparentBits = uniformTransparentBits ?? residualBits,
                            TransparentFaceNegXBits = sec.TransparentFaceNegXBits,
                            TransparentFacePosXBits = sec.TransparentFacePosXBits,
                            TransparentFaceNegYBits = sec.TransparentFaceNegYBits,
                            TransparentFacePosYBits = sec.TransparentFacePosYBits,
                            TransparentFaceNegZBits = sec.TransparentFaceNegZBits,
                            TransparentFacePosZBits = sec.TransparentFacePosZBits,
                            TransparentPaletteIndices = transparentPaletteIndices,
                            DominantTransparentId = dominantId,
                            DominantTransparentCount = dominantCount,
                            DominantTransparentBits = dominantBits,
                            TransparentPaletteFaceTiles = transparentFaceTiles,
                            EmptyCount = sec.EmptyCount,
                            EmptyBits = sec.EmptyBits,
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
                bool uniformOpaque = TerrainLoader.IsOpaque(AllOneBlockBlockId);
                long internalAdj = (long)(dimX - 1) * dimY * dimZ + (long)dimX * (dimZ - 1) * dimY + (long)dimX * dimZ * (dimY - 1);
                int exposure = uniformOpaque ? (int)(6L * vol - 2L * internalAdj) : 0;
                MeshPrepassStats = new ChunkMeshPrepassStats
                {
                    SolidCount = uniformOpaque ? vol : 0,
                    ExposureEstimate = exposure,
                    MinX = 0, MinY = 0, MinZ = 0,
                    MaxX = dimX - 1, MaxY = dimY - 1, MaxZ = dimZ - 1,
                    XNonEmpty = uniformOpaque ? dimX : 0,
                    YNonEmpty = uniformOpaque ? dimY : 0,
                    ZNonEmpty = uniformOpaque ? dimZ : 0,
                    HasStats = uniformOpaque
                };
                var prerenderUniform = BuildPrerenderData(BuildSectionDescriptors());
                chunkRender = new ChunkRender(prerenderUniform);
                return;
            }

            // Aggregate opaque stats directly from sections (no flatten) and detect any transparent content.
            long internalExposureSum = 0;
            int totalOpaque = 0;
            bool boundsInit = false;
            int gMinX = 0, gMinY = 0, gMinZ = 0, gMaxX = 0, gMaxY = 0, gMaxZ = 0;
            bool anyTransparent = false;
            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null) continue;
                        if (sec.OpaqueVoxelCount > 0)
                        {
                            totalOpaque += sec.OpaqueVoxelCount;
                            internalExposureSum += sec.InternalExposure;
                        }
                        if (!anyTransparent && sec.TransparentCount > 0) anyTransparent = true;
                        if (sec.HasBounds && sec.OpaqueVoxelCount > 0)
                        {
                            int baseX = sx * Section.SECTION_SIZE;
                            int baseY = sy * Section.SECTION_SIZE;
                            int baseZ = sz * Section.SECTION_SIZE;
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
                SolidCount = totalOpaque,
                ExposureEstimate = (int)Math.Min(int.MaxValue, internalExposureSum),
                MinX = gMinX, MinY = gMinY, MinZ = gMinZ,
                MaxX = gMaxX, MaxY = gMaxY, MaxZ = gMaxZ,
                XNonEmpty = totalOpaque > 0 ? dimX : 0,
                YNonEmpty = totalOpaque > 0 ? dimY : 0,
                ZNonEmpty = totalOpaque > 0 ? dimZ : 0,
                HasStats = boundsInit
            };
            if (totalOpaque == 0 && !anyTransparent) return;

            var prerender = BuildPrerenderData(BuildSectionDescriptors());
            chunkRender = new ChunkRender(prerender);
        }
    }
}

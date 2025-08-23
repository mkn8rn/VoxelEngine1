using System;
using System.Collections.Generic;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Generation.Models;
using MVGE_INF.Managers;
using MVGE_Tools.Noise;
using MVGE_GEN.Utils;

namespace MVGE_GEN.Terrain
{
    // Split generation responsibilities out of Chunk (partial)
    public partial class Chunk
    {
        public void GenerateInitialChunkData()
        {
            int maxX = dimX;
            int maxY = dimY;
            int maxZ = dimZ;
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);

            int chunkBaseY = (int)position.Y;
            int topOfChunk = chunkBaseY + maxY - 1;

            // Precompute burial classification BEFORE we null out the heightmap reference.
            // A chunk is FullyBuried if for every (x,z) column the top of the chunk is strictly below
            // (surfaceHeight - BURIAL_MARGIN).
            bool allBuried = true;
            int maxSurface = int.MinValue; // also track highest surface for all-air fast-path
            for (int x = 0; x < maxX && allBuried; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int surface = (int)heightmap[x, z];
                    if (surface > maxSurface) maxSurface = surface;
                    if (topOfChunk >= surface - BURIAL_MARGIN)
                    {
                        allBuried = false;
                        // we still continue updating maxSurface in rest of columns (need highest surface)
                        // but break inner loop for burial condition
                        break;
                    }
                }
            }
            // If we broke early due to allBuried becoming false, we still need remaining columns' maxSurface
            if (!allBuried)
            {
                for (int x = 0; x < maxX; x++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        int surface = (int)heightmap[x, z];
                        if (surface > maxSurface) maxSurface = surface;
                    }
                }
            }
            FullyBuried = allBuried;

            // All-air fast path: entire chunk vertical span is above every surface sample.
            // Condition: chunk bottom (chunkBaseY) is strictly greater than maximum surface height.
            if (chunkBaseY > maxSurface)
            {
                AllAirChunk = true;
                IsEmpty = true;
                precomputedHeightmap = null;
                return; // skip any section allocation / fills
            }

            // Attempt uniform-stone detection before doing per-column writes.
            TryDetectAllStone(heightmap, chunkBaseY, topOfChunk);
            if (AllStoneChunk)
            {
                // We generated uniform stone sections inside detection. Clean up and exit.
                precomputedHeightmap = null;
                return;
            }
            // Attempt uniform-soil detection (if not fully buried)
            if (!FullyBuried)
            {
                TryDetectAllSoil(heightmap, chunkBaseY, topOfChunk);
                if (AllSoilChunk)
                {
                    // We generated uniform soil sections inside detection. Clean up and exit.
                    precomputedHeightmap = null;
                    return;
                }
            }

            // Biome absolute bounds
            int stoneMinY = biome.stone_min_ylevel;
            int stoneMaxY = biome.stone_max_ylevel;
            int soilMinY = biome.soil_min_ylevel;
            int soilMaxY = biome.soil_max_ylevel;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int columnHeight = (int)heightmap[x, z]; // inclusive highest world Y occupied by terrain

                    // If terrain surface below this chunk's base for this column, column is all air – skip.
                    if (columnHeight < chunkBaseY)
                        continue;

                    // STONE CALCULATION
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight); // cannot exceed actual terrain top
                    if (stoneBandEndWorld < stoneBandStartWorld)
                        continue; // nothing to place at all (also implies no soil since terrain lower than bands)

                    int available = stoneBandEndWorld - stoneBandStartWorld + 1; // total vertical cells available for stone+reserved soil
                    if (available <= 0)
                        continue;

                    // Reserve minimum soil depth (clamped to available)
                    int soilMinReserve = Math.Clamp(biome.soil_min_depth, 0, available);

                    // Desired stone depths
                    int stoneMinDepth = biome.stone_min_depth;
                    int stoneMaxDepth = biome.stone_max_depth;

                    // Raw candidate per spec: stoneDepth = min(stoneMaxDepth, max(stoneMinDepth, available - soilMinReserve))
                    int rawStoneDepth = available - soilMinReserve;
                    int stoneDepth = Math.Min(stoneMaxDepth, Math.Max(stoneMinDepth, rawStoneDepth));
                    if (stoneDepth > available) stoneDepth = available; // safety clamp if spec overflows

                    int finalStoneBottomWorld = stoneBandStartWorld;
                    int finalStoneTopWorld = finalStoneBottomWorld + stoneDepth - 1; // inclusive

                    // Place stone (clamp to chunk vertical span) using bulk column fill
                    int localStoneStart = finalStoneBottomWorld - chunkBaseY;
                    int localStoneEnd = finalStoneTopWorld - chunkBaseY;
                    if (localStoneEnd >= 0 && localStoneStart < maxY)
                    {
                        if (localStoneStart < 0) localStoneStart = 0;
                        if (localStoneEnd >= maxY) localStoneEnd = maxY - 1;
                        if (localStoneStart <= localStoneEnd)
                        {
                            int syStart = localStoneStart >> SECTION_SHIFT;
                            int syEnd = localStoneEnd >> SECTION_SHIFT;
                            int ox = x & SECTION_MASK;
                            int oz = z & SECTION_MASK;
                            int sx = x >> SECTION_SHIFT;
                            int sz = z >> SECTION_SHIFT;
                            for (int sy = syStart; sy <= syEnd; sy++)
                            {
                                var sec = sections[sx, sy, sz];
                                if (sec == null) { sec = new ChunkSection(); sections[sx, sy, sz] = sec; }
                                int yStartInSection = (sy == syStart) ? (localStoneStart & SECTION_MASK) : 0;
                                int yEndInSection = (sy == syEnd) ? (localStoneEnd & SECTION_MASK) : (ChunkSection.SECTION_SIZE - 1);
                                SectionUtils.FillColumnRangeInitial(sec, ox, oz, yStartInSection, yEndInSection, (ushort)BaseBlockType.Stone);
                            }
                        }
                    }

                    // SOIL CALCULATION
                    // Soil starts directly above stone; if no stone depth (stoneDepth==0) it starts at stoneBandStartWorld.
                    int soilStartWorld = finalStoneTopWorld + 1;
                    if (stoneDepth == 0) soilStartWorld = stoneBandStartWorld; // no stone placed, soil may start at band start

                    // Enforce absolute biome soil band bounds
                    if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
                    if (soilStartWorld > soilMaxY) continue; // out of soil band already

                    int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);
                    if (soilBandCapWorld < soilStartWorld) continue; // no vertical space for soil

                    // Remaining vertical space up to cap
                    int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                    if (soilAvailable <= 0) continue;

                    int soilMaxDepth = biome.soil_max_depth;
                    int soilDepth = Math.Min(soilMaxDepth, soilAvailable);

                    int soilEndWorld = soilStartWorld + soilDepth - 1;

                    // Place soil using bulk column fill
                    int localSoilStart = soilStartWorld - chunkBaseY;
                    int localSoilEnd = soilEndWorld - chunkBaseY;
                    if (localSoilEnd >= 0 && localSoilStart < maxY)
                    {
                        if (localSoilStart < 0) localSoilStart = 0;
                        if (localSoilEnd >= maxY) localSoilEnd = maxY - 1;
                        if (localSoilStart <= localSoilEnd)
                        {
                            int syStart = localSoilStart >> SECTION_SHIFT;
                            int syEnd = localSoilEnd >> SECTION_SHIFT;
                            int ox = x & SECTION_MASK;
                            int oz = z & SECTION_MASK;
                            int sx = x >> SECTION_SHIFT;
                            int sz = z >> SECTION_SHIFT;
                            for (int sy = syStart; sy <= syEnd; sy++)
                            {
                                var sec = sections[sx, sy, sz];
                                if (sec == null) { sec = new ChunkSection(); sections[sx, sy, sz] = sec; }
                                int yStartInSection = (sy == syStart) ? (localSoilStart & SECTION_MASK) : 0;
                                int yEndInSection = (sy == syEnd) ? (localSoilEnd & SECTION_MASK) : (ChunkSection.SECTION_SIZE - 1);
                                SectionUtils.FillColumnRangeInitial(sec, ox, oz, yStartInSection, yEndInSection, (ushort)BaseBlockType.Soil);
                            }
                        }
                    }
                }
            }

            precomputedHeightmap = null; // release reference
        }

        private void TryDetectAllStone(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            // Conditions per column (x,z):
            // 1. columnHeight >= topOfChunk (terrain covers chunk vertically)
            // 2. Stone band + computed stone depth covers [chunkBaseY, topOfChunk]
            // That requires chunkBaseY >= stoneBandStartWorld AND topOfChunk <= finalStoneTopWorld.
            int maxX = dimX;
            int maxZ = dimZ;
            int stoneMinY = biome.stone_min_ylevel;
            int stoneMaxY = biome.stone_max_ylevel;
            int soilMinDepthSpec = biome.soil_min_depth; // for depth reservation
            int stoneMinDepthSpec = biome.stone_min_depth;
            int stoneMaxDepthSpec = biome.stone_max_depth;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    if (columnHeight < topOfChunk)
                    {
                        AllStoneChunk = false; return; // exposed somewhere
                    }
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight);
                    if (stoneBandEndWorld < stoneBandStartWorld)
                    { AllStoneChunk = false; return; }
                    int available = stoneBandEndWorld - stoneBandStartWorld + 1;
                    if (available <= 0) { AllStoneChunk = false; return; }
                    int soilMinReserve = Math.Clamp(soilMinDepthSpec, 0, available);
                    int rawStoneDepth = available - soilMinReserve;
                    int stoneDepth = Math.Min(stoneMaxDepthSpec, Math.Max(stoneMinDepthSpec, rawStoneDepth));
                    if (stoneDepth > available) stoneDepth = available;
                    int finalStoneBottomWorld = stoneBandStartWorld;
                    int finalStoneTopWorld = finalStoneBottomWorld + stoneDepth - 1;
                    if (chunkBaseY < finalStoneBottomWorld || topOfChunk > finalStoneTopWorld)
                    { AllStoneChunk = false; return; }
                }
            }
            // If we reached here every column satisfies conditions.
            AllStoneChunk = true;
            IsEmpty = false;
            // Pre-create uniform stone sections.
            CreateUniformSections((ushort)BaseBlockType.Stone);
            // Every face solid.
            FaceSolidNegX = FaceSolidPosX = FaceSolidNegY = FaceSolidPosY = FaceSolidNegZ = FaceSolidPosZ = true;
        }

        private void TryDetectAllSoil(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            int maxX = dimX;
            int maxZ = dimZ;
            int stoneMinY = biome.stone_min_ylevel;
            int stoneMaxY = biome.stone_max_ylevel;
            int soilMinY = biome.soil_min_ylevel;
            int soilMaxY = biome.soil_max_ylevel;
            int soilMinDepthSpec = biome.soil_min_depth;
            int soilMaxDepthSpec = biome.soil_max_depth;
            int stoneMinDepthSpec = biome.stone_min_depth;
            int stoneMaxDepthSpec = biome.stone_max_depth;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    if (columnHeight < topOfChunk) { AllSoilChunk = false; return; }

                    // Stone layer computation (same as generation) to find top of stone in this column
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight);
                    int finalStoneTopWorld = int.MinValue;
                    if (stoneBandEndWorld >= stoneBandStartWorld)
                    {
                        int available = stoneBandEndWorld - stoneBandStartWorld + 1;
                        if (available > 0)
                        {
                            int soilMinReserve = Math.Clamp(soilMinDepthSpec, 0, available);
                            int rawStoneDepth = available - soilMinReserve;
                            int stoneDepth = Math.Min(stoneMaxDepthSpec, Math.Max(stoneMinDepthSpec, rawStoneDepth));
                            if (stoneDepth > available) stoneDepth = available;
                            finalStoneTopWorld = stoneBandStartWorld + stoneDepth - 1;
                        }
                    }
                    else
                    {
                        finalStoneTopWorld = stoneBandStartWorld - 1; // no stone present
                    }

                    int soilStartWorld = finalStoneTopWorld + 1;
                    if (finalStoneTopWorld < stoneBandStartWorld) // means no stone placed (stoneDepth 0)
                        soilStartWorld = stoneBandStartWorld;
                    if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
                    if (soilStartWorld > soilMaxY) { AllSoilChunk = false; return; }
                    int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);
                    if (soilBandCapWorld < soilStartWorld) { AllSoilChunk = false; return; }
                    int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                    if (soilAvailable <= 0) { AllSoilChunk = false; return; }
                    int soilDepth = Math.Min(soilMaxDepthSpec, soilAvailable);
                    int soilEndWorld = soilStartWorld + soilDepth - 1;

                    // Uniform soil condition: chunk inside [soilStartWorld, soilEndWorld] and entirely above stone top
                    if (chunkBaseY < soilStartWorld || topOfChunk > soilEndWorld || chunkBaseY <= finalStoneTopWorld)
                    { AllSoilChunk = false; return; }
                }
            }
            AllSoilChunk = true;
            IsEmpty = false;
            CreateUniformSections((ushort)BaseBlockType.Soil);
            FaceSolidNegX = FaceSolidPosX = FaceSolidNegY = FaceSolidPosY = FaceSolidNegZ = FaceSolidPosZ = true;
        }

        private void CreateUniformSections(ushort blockId)
        {
            int S = ChunkSection.SECTION_SIZE;
            int voxelsPerSection = S * S * S; // 4096
            int bitsPerIndex = 1; // palette [AIR, block]
            int indices = voxelsPerSection;
            int uintCount = (indices * bitsPerIndex + 31) >> 5; // ceil(bits/32)

            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = new ChunkSection
                        {
                            IsAllAir = false,
                            Palette = new List<ushort> { ChunkSection.AIR, blockId },
                            PaletteLookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 }, { blockId, 1 } },
                            BitsPerIndex = bitsPerIndex,
                            VoxelCount = voxelsPerSection,
                            NonAirCount = voxelsPerSection,
                            BitData = new uint[uintCount]
                        };
                        for (int i = 0; i < uintCount; i++) sec.BitData[i] = 0xFFFFFFFFu; // all ones select palette index 1
                        sections[sx, sy, sz] = sec;
                    }
                }
            }
        }
    }
}

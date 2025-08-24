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

            // Combined uniform detection for stone / soil
            DetectAllStoneOrSoil(heightmap, chunkBaseY, topOfChunk);
            if (AllStoneChunk || AllSoilChunk)
            {
                precomputedHeightmap = null;
                return; // uniform fast path handled
            }

            // Biome absolute bounds
            int stoneMinY = biome.stoneMinYLevel;
            int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel;
            int soilMaxY = biome.soilMaxYLevel;

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
                    int soilMinReserve = Math.Clamp(biome.soilMinDepth, 0, available);

                    // Desired stone depths
                    int stoneMinDepth = biome.stoneMinDepth;
                    int stoneMaxDepth = biome.stoneMaxDepth;

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

                    int soilMaxDepth = biome.soilMaxDepth;
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

        private void DetectAllStoneOrSoil(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            // Combined detection pass for uniform stone and uniform soil.
            // This merges logic from TryDetectAllStone and TryDetectAllSoil to avoid two full column scans.
            // Stone conditions per column (x,z):
            // 1. columnHeight >= topOfChunk (terrain covers chunk vertically)
            // 2. Stone band + computed stone depth covers [chunkBaseY, topOfChunk]
            //    => chunkBaseY >= stoneBandStartWorld AND topOfChunk <= finalStoneTopWorld.
            // Soil conditions per column (x,z):
            // 1. columnHeight >= topOfChunk
            // 2. Compute stone layer (as normal) to obtain finalStoneTopWorld
            // 3. Determine soilStartWorld (just above stone top or biome band start if no stone), clamp to biome soil bounds; compute soilEndWorld with biome depth
            // 4. Entire chunk vertical span [chunkBaseY, topOfChunk] lies fully within [soilStartWorld, soilEndWorld]
            // 5. Chunk sits strictly above any stone (chunkBaseY > finalStoneTopWorld)
            // If EVERY column satisfies either set, we flag the corresponding uniform chunk type.
            int maxX = dimX;
            int maxZ = dimZ;
            bool possibleStone = true;

            int stoneMinY = biome.stoneMinYLevel;
            int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel;
            int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth;
            int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth;
            int stoneMaxDepthSpec = biome.stoneMaxDepth;

            // Soil uniform detection is attempted whenever the chunk vertical span intersects the biome soil band
            bool possibleSoil = (topOfChunk >= soilMinY) && (chunkBaseY <= soilMaxY);

            for (int x = 0; x < maxX && (possibleStone || possibleSoil); x++)
            {
                for (int z = 0; z < maxZ && (possibleStone || possibleSoil); z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    if (columnHeight < topOfChunk)
                    {
                        possibleStone = false;
                        possibleSoil = false;
                        break;
                    }

                    // Stone band & depth calc (shared)
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight);
                    int available = stoneBandEndWorld - stoneBandStartWorld + 1;
                    int finalStoneTopWorld = stoneBandStartWorld - 1; // default (no stone)
                    if (stoneBandEndWorld >= stoneBandStartWorld && available > 0)
                    {
                        int soilMinReserve = Math.Clamp(soilMinDepthSpec, 0, available);
                        int rawStoneDepth = available - soilMinReserve;
                        int stoneDepth = Math.Min(stoneMaxDepthSpec, Math.Max(stoneMinDepthSpec, rawStoneDepth));
                        if (stoneDepth > available) stoneDepth = available;
                        if (stoneDepth > 0)
                            finalStoneTopWorld = stoneBandStartWorld + stoneDepth - 1;
                        else
                            finalStoneTopWorld = stoneBandStartWorld - 1; // treat as no stone placed
                    }
                    else
                    {
                        available = 0;
                    }

                    // Evaluate stone uniform condition for this column
                    if (possibleStone)
                    {
                        if (available <= 0) { possibleStone = false; }
                        else
                        {
                            // Need entire chunk span covered by stone depth
                            if (chunkBaseY < stoneBandStartWorld || topOfChunk > finalStoneTopWorld)
                                possibleStone = false;
                        }
                    }

                    // Evaluate soil uniform condition for this column
                    if (possibleSoil)
                    {
                        int soilStartWorld = finalStoneTopWorld + 1;
                        if (finalStoneTopWorld < stoneBandStartWorld) // means no stone placed (stoneDepth 0)
                            soilStartWorld = stoneBandStartWorld;
                        if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
                        if (soilStartWorld > soilMaxY) { possibleSoil = false; continue; }
                        int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);
                        if (soilBandCapWorld < soilStartWorld) { possibleSoil = false; continue; }
                        int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                        if (soilAvailable <= 0) { possibleSoil = false; continue; }
                        int soilDepth = Math.Min(soilMaxDepthSpec, soilAvailable);
                        int soilEndWorld = soilStartWorld + soilDepth - 1;
                        if (chunkBaseY < soilStartWorld || topOfChunk > soilEndWorld || chunkBaseY <= finalStoneTopWorld)
                            possibleSoil = false;
                    }
                }
            }

            if (possibleStone)
            {
                AllStoneChunk = true;
                IsEmpty = false;
                CreateUniformSections((ushort)BaseBlockType.Stone);
                FaceSolidNegX = FaceSolidPosX = FaceSolidNegY = FaceSolidPosY = FaceSolidNegZ = FaceSolidPosZ = true;
                return;
            }
            if (possibleSoil)
            {
                AllSoilChunk = true;
                IsEmpty = false;
                CreateUniformSections((ushort)BaseBlockType.Soil);
                FaceSolidNegX = FaceSolidPosX = FaceSolidNegY = FaceSolidPosY = FaceSolidNegZ = FaceSolidPosZ = true;
            }
        }

        private void TryDetectAllStone(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            // Conditions per column (x,z):
            // 1. columnHeight >= topOfChunk (terrain covers chunk vertically)
            // 2. Stone band + computed stone depth covers [chunkBaseY, topOfChunk]
            // That requires chunkBaseY >= stoneBandStartWorld AND topOfChunk <= finalStoneTopWorld.
            // (Wrapper kept for compatibility; now calls unified detector.)
            if (AllStoneChunk || AllSoilChunk) return;
            DetectAllStoneOrSoil(heightmap, chunkBaseY, topOfChunk);
        }

        private void TryDetectAllSoil(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            // Conditions per column (x,z):
            // 1. columnHeight >= topOfChunk (terrain covers chunk vertically)
            // 2. Compute the stone layer exactly as normal generation would (respecting depth reservations & biome bands) and obtain finalStoneTopWorld
            // 3. Determine soilStartWorld (just above stone top or biome start if no stone) clamped to biome soil min & max, then soilEndWorld using biome soil depth rules
            // 4. Chunk qualifies as uniform all-soil iff its entire vertical span [chunkBaseY, topOfChunk] lies fully within the computed soil interval [soilStartWorld, soilEndWorld]
            // 5. Additionally the chunk must sit strictly above any stone (chunkBaseY > finalStoneTopWorld) to avoid containing stone voxels
            // If EVERY column satisfies these conditions the chunk can be flagged AllSoilChunk and filled uniformly.
            // (Wrapper kept for compatibility; now calls unified detector.)
            if (AllStoneChunk || AllSoilChunk) return;
            DetectAllStoneOrSoil(heightmap, chunkBaseY, topOfChunk);
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

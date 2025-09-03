using System;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Generation.Models;
using MVGE_INF.Managers;
using MVGE_Tools.Noise;
using MVGE_GEN.Utils;
using MVGE_INF.Models.Generation;
using MVGE_INF.Loaders;
using System.Buffers;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        private void CreateUniformSections(ushort blockId)
        {
            int S = ChunkSection.SECTION_SIZE;
            int voxelsPerSection = S * S * S;
            for (int sx = 0; sx < sectionsX; sx++)
            for (int sy = 0; sy < sectionsY; sy++)
            for (int sz = 0; sz < sectionsZ; sz++)
            {
                var sec = new ChunkSection
                {
                    IsAllAir = false,
                    Kind = ChunkSection.RepresentationKind.Uniform,
                    UniformBlockId = blockId,
                    NonAirCount = voxelsPerSection,
                    VoxelCount = voxelsPerSection,
                    CompletelyFull = true,
                    MetadataBuilt = true,
                    HasBounds = true,
                    MinLX = 0, MinLY = 0, MinLZ = 0, MaxLX = 15, MaxLY = 15, MaxLZ = 15
                };
                sections[sx, sy, sz] = sec;
            }
        }

        private void DetectAllStoneOrSoil(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            int maxX = dimX; int maxZ = dimZ;
            bool possibleStone = true; // remains true only if ENTIRE chunk volume lies wholly inside a valid stone span with no soil/air
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel; int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth; int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth; int stoneMaxDepthSpec = biome.stoneMaxDepth;
            bool possibleSoil = (topOfChunk >= soilMinY) && (chunkBaseY <= soilMaxY); // remains true only if ENTIRE chunk volume is soil (no stone / air)
            for (int x = 0; x < maxX && (possibleStone || possibleSoil); x++)
            {
                for (int z = 0; z < maxZ && (possibleStone || possibleSoil); z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    if (columnHeight < topOfChunk){ possibleStone=false; possibleSoil=false; break; }
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight);
                    int available = stoneBandEndWorld - stoneBandStartWorld + 1;
                    int finalStoneTopWorld = stoneBandStartWorld - 1;
                    int stoneDepth = 0;
                    if (stoneBandEndWorld >= stoneBandStartWorld && available > 0)
                    {
                        int soilMinReserve = Math.Clamp(soilMinDepthSpec, 0, available);
                        int rawStoneDepth = available - soilMinReserve;
                        stoneDepth = Math.Min(stoneMaxDepthSpec, Math.Max(stoneMinDepthSpec, rawStoneDepth));
                        if (stoneDepth > available) stoneDepth = available;
                        if (stoneDepth > 0) finalStoneTopWorld = stoneBandStartWorld + stoneDepth - 1;
                    }
                    else available = 0;
                    if (possibleStone)
                    {
                        if (available <= 0 || chunkBaseY < stoneBandStartWorld || topOfChunk > finalStoneTopWorld) possibleStone = false;
                    }
                    if (possibleSoil)
                    {
                        int soilStartWorld = finalStoneTopWorld + 1;
                        if (finalStoneTopWorld < stoneBandStartWorld) soilStartWorld = stoneBandStartWorld; // no stone actually placed
                        if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
                        if (soilStartWorld > soilMaxY){ possibleSoil=false; continue; }
                        int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);
                        if (soilBandCapWorld < soilStartWorld){ possibleSoil=false; continue; }
                        int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                        if (soilAvailable <= 0){ possibleSoil=false; continue; }
                        int soilDepth = Math.Min(soilMaxDepthSpec, soilAvailable);
                        int soilEndWorld = soilStartWorld + soilDepth - 1;
                        if (chunkBaseY < soilStartWorld || topOfChunk > soilEndWorld || chunkBaseY <= finalStoneTopWorld) possibleSoil = false;
                    }
                }
            }

            // FAST PATH: if after scanning all columns the chunk is guaranteed fully stone or fully soil,
            // set flags, synthesize uniform sections immediately and skip the heavier span machinery.
            // (Comments kept; augmented to reflect new early-out behavior.)
            if (possibleStone && !possibleSoil && !AllStoneChunk)
            {
                AllStoneChunk = true;
                CreateUniformSections((ushort)BaseBlockType.Stone);
            }
            else if (possibleSoil && !possibleStone && !AllSoilChunk)
            {
                AllSoilChunk = true;
                CreateUniformSections((ushort)BaseBlockType.Soil);
            }
        }

        public void GenerateInitialChunkData()
        {
            int maxX = dimX; int maxY = dimY; int maxZ = dimZ;
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);
            int chunkBaseY = (int)position.Y; int topOfChunk = chunkBaseY + maxY - 1;
            const int LocalBurialMargin = 2;
            bool allBuried = true; int maxSurface = int.MinValue;
            // Single pass over heightmap columns: compute maxSurface and burial flag simultaneously.
            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int surface = (int)heightmap[x, z];
                    if (surface > maxSurface) maxSurface = surface;
                    if (allBuried && topOfChunk >= surface - LocalBurialMargin)
                    {
                        allBuried = false; // still continue scanning to finish maxSurface aggregation
                    }
                }
            }
            if (allBuried) candidateFullyBuried = true;
            if (chunkBaseY > maxSurface){ AllAirChunk=true; precomputedHeightmap=null; return; }

            // Detect fully uniform stone or soil chunk BEFORE performing span derivation.
            // (If flags set, we build boundary planes & burial classification and return early.)
            DetectAllStoneOrSoil(heightmap, chunkBaseY, topOfChunk);
            if (AllStoneChunk || AllSoilChunk)
            {
                // All sections were synthesized in DetectAllStoneOrSoil via CreateUniformSections.
                // Build boundary planes & final burial confirmation just like normal path.
                precomputedHeightmap = null; // heightmap no longer needed
                BuildAllBoundaryPlanesInitial();
                if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
                {
                    SetFullyBuried();
                }
                return; // EARLY EXIT: heavy mixed-span logic skipped.
            }

            // --------------------------------------------------------------
            // Section-level preclassification & consolidated AddRun reduction
            // (Executed only when chunk is NOT trivially all stone or all soil.)
            // --------------------------------------------------------------
            if (!AllStoneChunk && !AllSoilChunk)
            {
                // Precompute per-column stone / soil spans in chunk-local Y coordinates.
                // -1 indicates absence;


                int sectionSize = ChunkSection.SECTION_SIZE; // 16
                int sectionMask = sectionSize - 1;
                int sectionsYLocal = sectionsY;
                int columnCount = maxX * maxZ;

                // Stackalloc coverage counters (sectionsY is small — dimensions are multiples of 16)
                Span<int> stoneFullCoverCount = stackalloc int[sectionsYLocal]; stoneFullCoverCount.Clear();
                Span<int> soilFullCoverCount = stackalloc int[sectionsYLocal]; soilFullCoverCount.Clear();
                Span<bool> sectionUniformStone = stackalloc bool[sectionsYLocal]; sectionUniformStone.Clear();
                Span<bool> sectionUniformSoil = stackalloc bool[sectionsYLocal]; sectionUniformSoil.Clear();

                // Flattened pooled arrays for start/end spans (-1 = absent)
                var pool = ArrayPool<int>.Shared;
                int flatLen = columnCount;
                int[] stoneStart = pool.Rent(flatLen);
                int[] stoneEnd   = pool.Rent(flatLen);
                int[] soilStart  = pool.Rent(flatLen);
                int[] soilEnd    = pool.Rent(flatLen);
                int[] stoneFirstSection = pool.Rent(flatLen); // first section index intersected by stone span, -1 if none
                int[] stoneLastSection  = pool.Rent(flatLen); // last section index intersected by stone span
                int[] soilFirstSection  = pool.Rent(flatLen);
                int[] soilLastSection   = pool.Rent(flatLen);

                // Initialize arrays
                for (int i = 0; i < flatLen; i++)
                {
                    stoneStart[i] = stoneEnd[i] = -1;
                    soilStart[i] = soilEnd[i] = -1;
                    stoneFirstSection[i] = stoneLastSection[i] = -1;
                    soilFirstSection[i] = soilLastSection[i] = -1;
                }

                // Track global touched section band
                int globalMinSectionY = int.MaxValue;
                int globalMaxSectionY = -1;

                // --- Column span derivation & section coverage accumulation (single pass) ---
                for (int x = 0; x < maxX; x++)
                {
                    int colBase = x; // used for index calc inside z loop
                    for (int z = 0; z < maxZ; z++)
                    {
                        int colIndex = z * maxX + colBase;
                        int columnHeight = (int)heightmap[x,z];
                        if (columnHeight < chunkBaseY) continue; // column entirely below chunk -> all air

                        // --- Stone band ---
                        int stoneBandStartWorld = Math.Max(biome.stoneMinYLevel, 0);
                        int stoneBandEndWorld = Math.Min(biome.stoneMaxYLevel, columnHeight);
                        int available = stoneBandEndWorld - stoneBandStartWorld + 1;
                        int finalStoneTopWorld = stoneBandStartWorld - 1;
                        int stoneDepth = 0;
                        if (available > 0)
                        {
                            int soilMinReserve = Math.Clamp(biome.soilMinDepth, 0, available);
                            int rawStoneDepth = available - soilMinReserve;
                            stoneDepth = Math.Min(biome.stoneMaxDepth, Math.Max(biome.stoneMinDepth, rawStoneDepth));
                            if (stoneDepth > available) stoneDepth = available;
                            if (stoneDepth > 0) finalStoneTopWorld = stoneBandStartWorld + stoneDepth - 1;
                        }

                        if (stoneDepth > 0)
                        {
                            int localStoneStart = stoneBandStartWorld - chunkBaseY;
                            int localStoneEnd = finalStoneTopWorld - chunkBaseY;
                            if (localStoneEnd >= 0 && localStoneStart < maxY)
                            {
                                if (localStoneStart < 0) localStoneStart = 0;
                                if (localStoneEnd >= maxY) localStoneEnd = maxY - 1;
                                if (localStoneStart <= localStoneEnd)
                                {
                                    stoneStart[colIndex] = localStoneStart;
                                    stoneEnd[colIndex] = localStoneEnd;
                                    // record section range
                                    int firstSecTmp = localStoneStart / sectionSize;
                                    int lastSecTmp = localStoneEnd / sectionSize;
                                    stoneFirstSection[colIndex] = firstSecTmp;
                                    stoneLastSection[colIndex] = lastSecTmp;
                                    if (firstSecTmp < globalMinSectionY) globalMinSectionY = firstSecTmp;
                                    if (lastSecTmp > globalMaxSectionY) globalMaxSectionY = lastSecTmp;
                                }
                            }
                        }

                        // --- Soil band (above stone) ---
                        int soilStartWorldLocal = (stoneDepth > 0 ? (finalStoneTopWorld + 1) : stoneBandStartWorld);
                        if (soilStartWorldLocal < biome.soilMinYLevel) soilStartWorldLocal = biome.soilMinYLevel;
                        if (soilStartWorldLocal <= biome.soilMaxYLevel)
                        {
                            int soilBandCapWorld = Math.Min(biome.soilMaxYLevel, columnHeight);
                            if (soilBandCapWorld >= soilStartWorldLocal)
                            {
                                int soilAvailable = soilBandCapWorld - soilStartWorldLocal + 1;
                                if (soilAvailable > 0)
                                {
                                    int soilDepth = Math.Min(biome.soilMaxDepth, soilAvailable);
                                    int soilEndWorldLocal = soilStartWorldLocal + soilDepth - 1;
                                    int localSoilStart = soilStartWorldLocal - chunkBaseY;
                                    int localSoilEnd = soilEndWorldLocal - chunkBaseY;
                                    if (localSoilEnd >= 0 && localSoilStart < maxY)
                                    {
                                        if (localSoilStart < 0) localSoilStart = 0;
                                        if (localSoilEnd >= maxY) localSoilEnd = maxY - 1;
                                        if (localSoilStart <= localSoilEnd)
                                        {
                                            soilStart[colIndex] = localSoilStart;
                                            soilEnd[colIndex] = localSoilEnd;
                                            int firstSecTmp = localSoilStart / sectionSize;
                                            int lastSecTmp = localSoilEnd / sectionSize;
                                            soilFirstSection[colIndex] = firstSecTmp;
                                            soilLastSection[colIndex] = lastSecTmp;
                                            if (firstSecTmp < globalMinSectionY) globalMinSectionY = firstSecTmp;
                                            if (lastSecTmp > globalMaxSectionY) globalMaxSectionY = lastSecTmp;
                                        }
                                    }
                                }
                            }
                        }

                        // After we know both spans we can accumulate per-section full coverage stats.
                        int sStart = stoneStart[colIndex]; int sEnd = stoneEnd[colIndex];
                        int soStart = soilStart[colIndex]; int soEnd = soilEnd[colIndex];
                        if (sStart >= 0)
                        {
                            int firstSec = sStart / sectionSize;
                            int lastSec = sEnd / sectionSize;
                            for (int sy = firstSec; sy <= lastSec && sy < sectionsYLocal; sy++)
                            {
                                int sectionBaseY = sy * sectionSize;
                                int sectionEndY = sectionBaseY + sectionMask;
                                // Must fully cover section and soil must not overlap this section
                                bool fullCover = sStart <= sectionBaseY && sEnd >= sectionEndY && !(soStart >= 0 && !(soEnd < sectionBaseY || soStart > sectionEndY));
                                if (fullCover) stoneFullCoverCount[sy]++;
                            }
                        }
                        if (soStart >= 0)
                        {
                            int firstSec = soStart / sectionSize;
                            int lastSec = soEnd / sectionSize;
                            for (int sy = firstSec; sy <= lastSec && sy < sectionsYLocal; sy++)
                            {
                                int sectionBaseY = sy * sectionSize;
                                int sectionEndY = sectionBaseY + sectionMask;
                                bool fullCover = soStart <= sectionBaseY && soEnd >= sectionEndY && !(sStart >= 0 && !(sEnd < sectionBaseY || sStart > sectionEndY));
                                if (fullCover) soilFullCoverCount[sy]++;
                            }
                        }
                    }
                }

                if (globalMaxSectionY < 0) // no spans touched any section -> nothing else to do
                {
                    // Return pooled arrays early
                    pool.Return(stoneStart, clearArray: true);
                    pool.Return(stoneEnd, clearArray: true);
                    pool.Return(soilStart, clearArray: true);
                    pool.Return(soilEnd, clearArray: true);
                    pool.Return(stoneFirstSection, clearArray: true);
                    pool.Return(stoneLastSection, clearArray: true);
                    pool.Return(soilFirstSection, clearArray: true);
                    pool.Return(soilLastSection, clearArray: true);
                }
                else
                {
                    if (globalMinSectionY < 0) globalMinSectionY = 0;
                    if (globalMaxSectionY >= sectionsYLocal) globalMaxSectionY = sectionsYLocal - 1;

                    // --- Decide uniform sections ---
                    for (int sy = globalMinSectionY; sy <= globalMaxSectionY; sy++)
                    {
                        bool stoneUniform = stoneFullCoverCount[sy] == columnCount;
                        bool soilUniform = !stoneUniform && soilFullCoverCount[sy] == columnCount; // mutually exclusive in generation model
                        if (stoneUniform)
                        {
                            sectionUniformStone[sy] = true;
                            // build uniform stone section if not already present
                            for (int sx = 0; sx < sectionsX; sx++)
                            for (int sz = 0; sz < sectionsZ; sz++)
                            {
                                if (sections[sx, sy, sz] == null)
                                {
                                    sections[sx, sy, sz] = new ChunkSection
                                    {
                                        IsAllAir = false,
                                        Kind = ChunkSection.RepresentationKind.Uniform,
                                        UniformBlockId = (ushort)BaseBlockType.Stone,
                                        NonAirCount = sectionSize * sectionSize * sectionSize,
                                        VoxelCount = sectionSize * sectionSize * sectionSize,
                                        CompletelyFull = true,
                                        MetadataBuilt = true,
                                        HasBounds = true,
                                        MinLX = 0, MinLY = 0, MinLZ = 0,
                                        MaxLX = 15, MaxLY = 15, MaxLZ = 15,
                                        StructuralDirty = false,
                                        IdMapDirty = false
                                    };
                                }
                            }
                            continue;
                        }
                        if (soilUniform)
                        {
                            sectionUniformSoil[sy] = true;
                            for (int sx = 0; sx < sectionsX; sx++)
                            for (int sz = 0; sz < sectionsZ; sz++)
                            {
                                if (sections[sx, sy, sz] == null)
                                {
                                    sections[sx, sy, sz] = new ChunkSection
                                    {
                                        IsAllAir = false,
                                        Kind = ChunkSection.RepresentationKind.Uniform,
                                        UniformBlockId = (ushort)BaseBlockType.Soil,
                                        NonAirCount = sectionSize * sectionSize * sectionSize,
                                        VoxelCount = sectionSize * sectionSize * sectionSize,
                                        CompletelyFull = true,
                                        MetadataBuilt = true,
                                        HasBounds = true,
                                        MinLX = 0, MinLY = 0, MinLZ = 0,
                                        MaxLX = 15, MaxLY = 15, MaxLZ = 15,
                                        StructuralDirty = false,
                                        IdMapDirty = false
                                    };
                                }
                            }
                        }
                    }

                    // --- Mixed section emission (only over ranges actually intersecting spans, restricted band) ---
                    for (int x = 0; x < maxX; x++)
                    {
                        for (int z = 0; z < maxZ; z++)
                        {
                            int colIndex = z * maxX + x;
                            int ss = stoneStart[colIndex]; int se = stoneEnd[colIndex];
                            int sols = soilStart[colIndex]; int sole = soilEnd[colIndex];
                            if (ss < 0 && sols < 0) continue; // empty column

                            int sxIndex = x >> SECTION_SHIFT; int ox = x & sectionMask;
                            int szIndex = z >> SECTION_SHIFT; int oz = z & sectionMask;

                            // Stone span -> iterate intersecting sections only
                            if (ss >= 0)
                            {
                                int firstSec = stoneFirstSection[colIndex];
                                int lastSec = stoneLastSection[colIndex];
                                if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                                if (lastSec > globalMaxSectionY) lastSec = globalMaxSectionY;
                                for (int sy = firstSec; sy <= lastSec && sy < sectionsYLocal; sy++)
                                {
                                    if (sectionUniformStone[sy] || sectionUniformSoil[sy]) continue; // skip uniform sections
                                    int sectionBaseY = sy * sectionSize;
                                    int sectionEndY = sectionBaseY + sectionMask;
                                    if (se < sectionBaseY || ss > sectionEndY) continue; // no overlap (safety)
                                    int clippedStart = ss < sectionBaseY ? sectionBaseY : ss;
                                    int clippedEnd = se > sectionEndY ? sectionEndY : se;
                                    int localStart = clippedStart - sectionBaseY;
                                    int localEnd = clippedEnd - sectionBaseY;
                                    if (localStart > localEnd) continue;
                                    ChunkSection secRef = sections[sxIndex, sy, szIndex];
                                    if (secRef == null)
                                    {
                                        secRef = new ChunkSection();
                                        sections[sxIndex, sy, szIndex] = secRef;
                                    }
                                    SectionUtils.AddRun(secRef, ox, oz, localStart, localEnd, (ushort)BaseBlockType.Stone);
                                }
                            }
                            // Soil span
                            if (sols >= 0)
                            {
                                int firstSec = soilFirstSection[colIndex];
                                int lastSec = soilLastSection[colIndex];
                                if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                                if (lastSec > globalMaxSectionY) lastSec = globalMaxSectionY;
                                for (int sy = firstSec; sy <= lastSec && sy < sectionsYLocal; sy++)
                                {
                                    if (sectionUniformStone[sy] || sectionUniformSoil[sy]) continue;
                                    int sectionBaseY = sy * sectionSize;
                                    int sectionEndY = sectionBaseY + sectionMask;
                                    if (sole < sectionBaseY || sols > sectionEndY) continue;
                                    int clippedStart = sols < sectionBaseY ? sectionBaseY : sols;
                                    int clippedEnd = sole > sectionEndY ? sectionEndY : sole;
                                    int localStart = clippedStart - sectionBaseY;
                                    int localEnd = clippedEnd - sectionBaseY;
                                    if (localStart > localEnd) continue;
                                    ChunkSection secRef = sections[sxIndex, sy, szIndex];
                                    if (secRef == null)
                                    {
                                        secRef = new ChunkSection();
                                        sections[sxIndex, sy, szIndex] = secRef;
                                    }
                                    SectionUtils.AddRun(secRef, ox, oz, localStart, localEnd, (ushort)BaseBlockType.Soil);
                                }
                            }
                        }
                    }

                    // Return pooled arrays
                    pool.Return(stoneStart, clearArray: true);
                    pool.Return(stoneEnd, clearArray: true);
                    pool.Return(soilStart, clearArray: true);
                    pool.Return(soilEnd, clearArray: true);
                    pool.Return(stoneFirstSection, clearArray: true);
                    pool.Return(stoneLastSection, clearArray: true);
                    pool.Return(soilFirstSection, clearArray: true);
                    pool.Return(soilLastSection, clearArray: true);
                }
            }

            // Chunk-level aggregate short-circuit:
            // After applying replacements, if every populated section is a Uniform section with the same non-air id
            // we can mark AllOneBlockChunk and skip per-section finalization (metadata already valid for uniforms
            // except for pending IdMapDirty which we clear).
            if (!AllAirChunk)
            {
                bool allUniformSame = true;
                ushort uniformId = 0;
                for (int sx=0; sx<sectionsX && allUniformSame; sx++)
                {
                    for (int sy=0; sy<sectionsY && allUniformSame; sy++)
                    {
                        for (int sz=0; sz<sectionsZ && allUniformSame; sz++)
                        {
                            var sec = sections[sx,sy,sz]; if (sec==null) continue; // treat null as empty air
                            if (sec.Kind != ChunkSection.RepresentationKind.Uniform || sec.UniformBlockId == ChunkSection.AIR)
                            {
                                allUniformSame = false; break;
                            }
                            if (uniformId == 0) uniformId = sec.UniformBlockId; else if (sec.UniformBlockId != uniformId) { allUniformSame = false; break; }
                        }
                    }
                }
                if (allUniformSame && uniformId != 0)
                {
                    AllOneBlockChunk = true;
                    AllOneBlockBlockId = uniformId;
                    // Clear dirty flags; no need to finalize each section.
                    for (int sx=0; sx<sectionsX; sx++)
                        for (int sy=0; sy<sectionsY; sy++)
                            for (int sz=0; sz<sectionsZ; sz++)
                            {
                                var sec = sections[sx,sy,sz]; if (sec==null) continue;
                                if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                                {
                                    sec.IdMapDirty = false; sec.StructuralDirty = false; sec.MetadataBuilt = true; // metadata for uniform stays valid
                                }
                            }
                }
            }

            if (!AllOneBlockChunk)
            {
                for (int sx=0; sx<sectionsX; sx++)
                for (int sy=0; sy<sectionsY; sy++)
                for (int sz=0; sz<sectionsZ; sz++)
                { var sec = sections[sx,sy,sz]; if (sec==null) continue; SectionUtils.FinalizeSection(sec); }
            }

            precomputedHeightmap = null;
            BuildAllBoundaryPlanesInitial();

            // Confirm burial only if all six faces ended up solid
            if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
            {
                SetFullyBuried();
            }
        }

        // Cheaply set per-face solidity flags using cached boundary plane bitsets
        private static bool PlaneIsFull(ulong[] plane, int wordCount, ulong fullWord, ulong lastMask)
        {
            if (plane == null || wordCount == 0) return false;
            // ensure plane length is sufficient
            if (plane.Length < wordCount) return false;
            // all full words must be 0xFFFFFFFFFFFFFFFF
            for (int i = 0; i < wordCount - 1; i++)
            {
                if (plane[i] != fullWord) return false;
            }
            // last word uses a partial mask
            if (plane[wordCount - 1] != lastMask) return false;
            return true;
        }
        private void SetFaceSolidFromPlanes()
        {
            // YZ plane (Neg/Pos X): size = dimY * dimZ
            int yzBits = dimY * dimZ;
            int yzWC = (yzBits + 63) >> 6;
            ulong fullWord = ~0UL;
            int remYZ = yzBits & 63;
            ulong lastYZ = remYZ == 0 ? fullWord : ((1UL << remYZ) - 1);
            FaceSolidNegX = PlaneIsFull(PlaneNegX, yzWC, fullWord, lastYZ);
            FaceSolidPosX = PlaneIsFull(PlanePosX, yzWC, fullWord, lastYZ);

            // XZ plane (Neg/Pos Y): size = dimX * dimZ
            int xzBits = dimX * dimZ;
            int xzWC = (xzBits + 63) >> 6;
            int remXZ = xzBits & 63;
            ulong lastXZ = remXZ == 0 ? fullWord : ((1UL << remXZ) - 1);
            FaceSolidNegY = PlaneIsFull(PlaneNegY, xzWC, fullWord, lastXZ);
            FaceSolidPosY = PlaneIsFull(PlanePosY, xzWC, fullWord, lastXZ);

            // XY plane (Neg/Pos Z): size = dimX * dimY
            int xyBits = dimX * dimY;
            int xyWC = (xyBits + 63) >> 6;
            int remXY = xyBits & 63;
            ulong lastXY = remXY == 0 ? fullWord : ((1UL << remXY) - 1);
            FaceSolidNegZ = PlaneIsFull(PlaneNegZ, xyWC, fullWord, lastXY);
            FaceSolidPosZ = PlaneIsFull(PlanePosZ, xyWC, fullWord, lastXY);
        }

        private void BuildAllBoundaryPlanesInitial()
        {
            EnsurePlaneArrays();
            Array.Clear(PlaneNegX);
            Array.Clear(PlanePosX);
            Array.Clear(PlaneNegY);
            Array.Clear(PlanePosY);
            Array.Clear(PlaneNegZ);
            Array.Clear(PlanePosZ);

            const int S = ChunkSection.SECTION_SIZE;

            // Helpers: set one bit in a plane
            static void SetPlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return;
                int w = index >> 6;
                int b = index & 63;
                plane[w] |= 1UL << b;
            }

            // --- -X / +X (YZ planes) ---
            if (sectionsX > 0)
            {
                int sxNeg = 0;
                int sxPos = sectionsX - 1;

                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        // Neg X: section local face at x=0 contributes to global plane x==0
                        var secNeg = sections[sxNeg, sy, sz];
                        if (secNeg != null)
                        {
                            // Uniform sections intentionally leave Face*Bits null but imply fully solid face
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localZ = 0; localZ < S; localZ++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlaneNegX, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegXBits != null)
                            {
                                var bits = secNeg.FaceNegXBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localZ = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlaneNegX, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }

                        }

                        // Pos X: section local face at x=15 contributes to global plane x==dimX-1
                        var secPos = sections[sxPos, sy, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localZ = 0; localZ < S; localZ++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlanePosX, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosXBits != null)
                            {
                                var bits = secPos.FacePosXBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localZ = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlanePosX, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // --- -Y / +Y (XZ planes) ---
            if (sectionsY > 0)
            {
                int syNeg = 0;
                int syPos = sectionsY - 1;

                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var secNeg = sections[sx, syNeg, sz];
                        if (secNeg != null)
                        {
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlaneNegY, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegYBits != null)
                            {
                                var bits = secNeg.FaceNegYBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localZ = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlaneNegY, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }

                        var secPos = sections[sx, syPos, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlanePosY, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosYBits != null)
                            {
                                var bits = secPos.FacePosYBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localZ = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlanePosY, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // --- -Z / +Z (XY planes) ---
            if (sectionsZ > 0)
            {
                int szNeg = 0;
                int szPos = sectionsZ - 1;

                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sy = 0; sy < sectionsY; sy++)
                    {
                        var secNeg = sections[sx, sy, szNeg];
                        if (secNeg != null)
                        {
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlaneNegZ, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegZBits != null)
                            {
                                var bits = secNeg.FaceNegZBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlaneNegZ, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }

                        var secPos = sections[sx, sy, szPos];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlanePosZ, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosZBits != null)
                            {
                                var bits = secPos.FacePosZBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlanePosZ, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Derive per-face solidity flags from planes
            SetFaceSolidFromPlanes();
        }
    }
}
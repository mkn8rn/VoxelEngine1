using System;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Generation.Models;
using MVGE_INF.Managers;
using MVGE_Tools.Noise;
using MVGE_GEN.Utils;
using MVGE_INF.Models.Generation;
using MVGE_INF.Loaders;
using System.Buffers;
using System.Numerics;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        // Result container for per-column span derivation.
        internal readonly struct LocalBlockColumnProfile
        {
            public readonly bool HasStone;
            public readonly short StoneStart;
            public readonly short StoneEnd;
            public readonly bool HasSoil;
            public readonly short SoilStart;
            public readonly short SoilEnd;
            public readonly bool InvalidateStoneUniform;
            public readonly bool InvalidateSoilUniform;

            public LocalBlockColumnProfile(bool hasStone, short stoneStart, short stoneEnd,
                                        bool hasSoil, short soilStart, short soilEnd,
                                        bool invalidateStoneUniform, bool invalidateSoilUniform)
            {
                HasStone = hasStone;
                StoneStart = stoneStart;
                StoneEnd = stoneEnd;
                HasSoil = hasSoil;
                SoilStart = soilStart;
                SoilEnd = soilEnd;
                InvalidateStoneUniform = invalidateStoneUniform;
                InvalidateSoilUniform = invalidateSoilUniform;
            }
        }

        /// Per (x,z) column material spans inside this chunk's vertical slab.
        /// Each column can contain at most one contiguous stone span and one contiguous soil span directly above it.
        /// Section indices (first/last) are cached for emission & coverage calculations.
        /// Presence is indicated via bitsets; default field values are ignored when the span is absent.
        private struct ColumnSpans
        {
            public short stoneStart;    // inclusive local Y
            public short stoneEnd;      // inclusive local Y
            public short soilStart;     // inclusive local Y
            public short soilEnd;       // inclusive local Y
            public short stoneFirstSec; // first vertical section touched by the stone span
            public short stoneLastSec;  // last vertical section touched by the stone span
            public short soilFirstSec;  // first vertical section touched by the soil span
            public short soilLastSec;   // last vertical section touched by the soil span
        }

        /// Generates initial voxel data for the chunk.
        /// Phases:
        ///  1. Column pass (derivation): Using precomputed world BlockColumnProfile spans (columnSpanMap) -> clip to chunk, format as LocalBlockColumnProfile.
        ///     Accumulates fully‑covered section ranges via difference arrays (stoneDiff / soilDiff) and tracks which columns have material.
        ///  2. Prefix phase: Convert difference arrays -> per‑section coverage counts for stone & soil.
        ///  3. Whole chunk classification: detect AllAir / AllStone / AllSoil & early exit.
        ///  4. Section emission: mark section‑uniform stone/soil, skip them during sparse run emission for remaining spans.
        ///  5. Whole chunk single block collapse (AllOneBlockChunk) if all sections are uniform with the same id.
        ///  6. Finalization: finalize non‑uniform sections (representation + metadata) and build boundary planes; confirm burial.
        internal void GenerateInitialChunkData(BlockColumnProfile[] columnSpanMap)
        {
            if (columnSpanMap == null)
                throw new InvalidOperationException("columnSpanMap must be provided for non-uniform chunk generation.");

            // ------------------------------------------------------------------
            // Basic chunk & biome constants
            // ------------------------------------------------------------------
            int maxX = dimX, maxY = dimY, maxZ = dimZ;
            int chunkBaseY = (int)position.Y;
            int topOfChunk = chunkBaseY + maxY - 1; // inclusive local top

            const int LocalBurialMargin = 2;                // vertical margin near chunk top to avoid premature burial flag
            int burialInvalidateThreshold = topOfChunk - LocalBurialMargin;
            const ushort StoneId = (ushort)BaseBlockType.Stone;
            const ushort SoilId  = (ushort)BaseBlockType.Soil;

            int maxSurface = int.MinValue;                  // track highest column surface (all‑air early rejection)
            candidateFullyBuried = true;                    // invalidated if any surface approaches chunk top

            // Biome vertical bands (used for early band overlap tests)
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY  = biome.soilMinYLevel;  int soilMaxY  = biome.soilMaxYLevel;

            // ------------------------------------------------------------------
            // Aggregated uniform classification candidates (replaces per-column uniformFlags invalidation logic)
            // ------------------------------------------------------------------
            // allStoneFullCandidate: remains true only if every column's stone span fully covers the chunk slab (and exists).
            bool allStoneFullCandidate = true;
            // allSoilFullCandidate: remains true only if every column's soil span fully covers the chunk slab AND stone does not intrude that slab in that column.
            bool allSoilFullCandidate = true;
            // If soil biome band does not overlap the chunk slab at all, soil uniform candidate impossible from the start.
            if (!(topOfChunk >= soilMinY && chunkBaseY <= soilMaxY))
            {
                allSoilFullCandidate = false;
            }

            // ------------------------------------------------------------------
            // Section precomputation
            // ------------------------------------------------------------------
            int sectionSize = ChunkSection.SECTION_SIZE; // expected 16
            int sectionMask = sectionSize - 1;
            int sectionsYLocal = sectionsY;
            int columnCount = maxX * maxZ; // total vertical columns in this chunk

            Span<int> sectionBaseYArr = stackalloc int[sectionsYLocal];
            Span<int> sectionEndYArr  = stackalloc int[sectionsYLocal];
            for (int sy = 0; sy < sectionsYLocal; sy++)
            {
                int baseY = sy << SECTION_SHIFT; // faster than sy * 16
                sectionBaseYArr[sy] = baseY;
                sectionEndYArr[sy]  = baseY | (sectionSize - 1); // inclusive end
            }

            // Per‑section full coverage counts (after prefix) tell us how many columns fully cover a section.
            Span<int> stoneFullCoverCount = stackalloc int[sectionsYLocal]; stoneFullCoverCount.Clear();
            Span<int> soilFullCoverCount  = stackalloc int[sectionsYLocal]; soilFullCoverCount.Clear();

            // Section uniform classification flags (stone preferred). True => section becomes uniform & skipped for emission.
            Span<bool> sectionUniformStone = stackalloc bool[sectionsYLocal]; sectionUniformStone.Clear();
            Span<bool> sectionUniformSoil  = stackalloc bool[sectionsYLocal]; sectionUniformSoil.Clear();

            // Column span storage (AoS). Stack allocate if typical size, otherwise rent.
            const int STACKALLOC_COLUMN_THRESHOLD = 512; // 16x16 = 256 < threshold -> stack path
            ColumnSpans[] pooledColumns = null;
            Span<ColumnSpans> columns = columnCount <= STACKALLOC_COLUMN_THRESHOLD
                ? stackalloc ColumnSpans[columnCount]
                : (pooledColumns = ArrayPool<ColumnSpans>.Shared.Rent(columnCount)).AsSpan(0, columnCount);

            // Column presence bitsets for stone & soil (enable sparse emission over used columns only)
            int bitWordCount = (columnCount + 63) >> 6;
            Span<ulong> stonePresentBits = stackalloc ulong[bitWordCount]; stonePresentBits.Clear();
            Span<ulong> soilPresentBits  = stackalloc ulong[bitWordCount]; soilPresentBits.Clear();

            // Track min/max section index touched (restrict later loops)
            int globalMinSectionY = int.MaxValue;
            int globalMaxSectionY = -1;

            // Difference arrays for fully‑covered sections. Length+1 for range end sentinel.
            Span<int> stoneDiff = stackalloc int[sectionsYLocal + 1]; stoneDiff.Clear();
            Span<int> soilDiff  = stackalloc int[sectionsYLocal + 1]; soilDiff.Clear();

            // Helper to set a bit in the stone/soil presence bitsets
            static void SetBit(Span<ulong> bits, int index)
            {
                bits[index >> 6] |= 1UL << (index & 63);
            }

            // ------------------------------------------------------------------
            // Phase 1: Column pass (span derivation + coverage accumulation)
            // ------------------------------------------------------------------
            // We now use the provided world-space BlockColumnProfile data instead of TerrainGeneration.DeriveStoneSoilSpans.
            for (int z = 0; z < maxZ; z++)
            {
                int rowOffset = z * maxX;
                for (int x = 0; x < maxX; x++)
                {
                    int colIndex = rowOffset + x;
                    int surface = columnSpanMap[colIndex].Surface;

                    // Track highest surface for later all‑air classification
                    if (surface > maxSurface) maxSurface = surface;

                    // Burial candidate invalidation
                    if (candidateFullyBuried & (surface >= burialInvalidateThreshold))
                        candidateFullyBuried = false;

                    // World span index from per-block column map: index = localX * maxZ + localZ
                    int spanIndex = x * maxZ + z;
                    if ((uint)spanIndex >= (uint)columnSpanMap.Length)
                        continue; // defensive

                    ref readonly BlockColumnProfile worldCol = ref columnSpanMap[spanIndex];

                    // Extract world spans
                    int wStoneStart = worldCol.StoneStart;
                    int wStoneEnd   = worldCol.StoneEnd;
                    int wSoilStart  = worldCol.SoilStart;
                    int wSoilEnd    = worldCol.SoilEnd;

                    bool hasStone = wStoneStart >= 0 && wStoneEnd >= wStoneStart && wStoneEnd >= chunkBaseY && wStoneStart <= topOfChunk;
                    bool hasSoil  = wSoilStart >= 0 && wSoilEnd >= wSoilStart && wSoilEnd >= chunkBaseY && wSoilStart <= topOfChunk;

                    // Aggregated uniform classification candidate updates (replaces per-column uniformFlags logic)
                    if (allStoneFullCandidate)
                    {
                        // stone span must exist and fully cover chunk slab
                        if (!(hasStone && wStoneStart <= chunkBaseY && wStoneEnd >= topOfChunk))
                            allStoneFullCandidate = false;
                    }
                    if (allSoilFullCandidate)
                    {
                        // soil span must exist and fully cover chunk slab and stone must not intrude (stoneEnd < baseY or no stone)
                        bool stoneIntrudesSoil = hasStone && wStoneEnd >= chunkBaseY; // any stone voxel at/above base breaks pure soil
                        if (!(hasSoil && wSoilStart <= chunkBaseY && wSoilEnd >= topOfChunk && !stoneIntrudesSoil))
                            allSoilFullCandidate = false;
                    }

                    // Clip to chunk slab & convert to local short indices
                    short localStoneStart = 0, localStoneEnd = 0;
                    if (hasStone)
                    {
                        int cs = wStoneStart < chunkBaseY ? chunkBaseY : wStoneStart;
                        int ce = wStoneEnd   > topOfChunk ? topOfChunk : wStoneEnd;
                        if (cs <= ce)
                        {
                            localStoneStart = (short)(cs - chunkBaseY);
                            localStoneEnd   = (short)(ce - chunkBaseY);
                        }
                        else
                        {
                            hasStone = false; // no overlap after clip
                        }
                    }

                    short localSoilStart = 0, localSoilEnd = 0;
                    if (hasSoil)
                    {
                        int cs = wSoilStart < chunkBaseY ? chunkBaseY : wSoilStart;
                        int ce = wSoilEnd   > topOfChunk ? topOfChunk : wSoilEnd;
                        if (cs <= ce)
                        {
                            localSoilStart = (short)(cs - chunkBaseY);
                            localSoilEnd   = (short)(ce - chunkBaseY);
                        }
                        else
                        {
                            hasSoil = false;
                        }
                    }

                    // Skip empty column (no stone & no soil in this chunk slab)
                    if (!hasStone && !hasSoil) continue;

                    ref ColumnSpans spanRef = ref columns[colIndex];

                    // Record stone span
                    if (hasStone)
                    {
                        spanRef.stoneStart = localStoneStart;
                        spanRef.stoneEnd   = localStoneEnd;
                        short firstSec = (short)(localStoneStart >> SECTION_SHIFT);
                        short lastSec  = (short)(localStoneEnd   >> SECTION_SHIFT);
                        spanRef.stoneFirstSec = firstSec; spanRef.stoneLastSec = lastSec;
                        SetBit(stonePresentBits, colIndex);
                        if (firstSec < globalMinSectionY) globalMinSectionY = firstSec;
                        if (lastSec  > globalMaxSectionY) globalMaxSectionY = lastSec;
                    }

                    // Record soil span
                    if (hasSoil)
                    {
                        spanRef.soilStart = localSoilStart;
                        spanRef.soilEnd   = localSoilEnd;
                        short firstSec = (short)(localSoilStart >> SECTION_SHIFT);
                        short lastSec  = (short)(localSoilEnd   >> SECTION_SHIFT);
                        spanRef.soilFirstSec = firstSec; spanRef.soilLastSec = lastSec;
                        SetBit(soilPresentBits, colIndex);
                        if (firstSec < globalMinSectionY) globalMinSectionY = firstSec;
                        if (lastSec  > globalMaxSectionY) globalMaxSectionY = lastSec;
                    }

                    bool stonePresent = hasStone;
                    bool soilPresent  = hasSoil;

                    // Fully covered section accumulation (stone)
                    if (stonePresent)
                    {
                        int sStart = spanRef.stoneStart;
                        int sEnd   = spanRef.stoneEnd;
                        int first  = spanRef.stoneFirstSec;
                        int last   = spanRef.stoneLastSec;

                        bool firstFull = sStart <= sectionBaseYArr[first] && sEnd >= sectionEndYArr[first];
                        bool lastFull  = sStart <= sectionBaseYArr[last]  && sEnd >= sectionEndYArr[last];

                        int fullStart = first;
                        int fullEnd   = last;
                        if (!firstFull) fullStart = first + 1;
                        if (!lastFull)  fullEnd   = last - 1;
                        if (first == last && !(firstFull && lastFull)) fullStart = fullEnd + 1; // invalidate single partial section

                        if (fullStart <= fullEnd)
                        {
                            // Truncate if soil intrudes within the covered range
                            if (soilPresent)
                            {
                                int soilStartLocal = spanRef.soilStart;
                                int soilStartSec   = soilStartLocal >> SECTION_SHIFT;
                                if (soilStartSec <= fullEnd) fullEnd = soilStartSec - 1;
                            }
                            if (fullStart <= fullEnd)
                            {
                                stoneDiff[fullStart]++;
                                int stop = fullEnd + 1; if (stop < stoneDiff.Length) stoneDiff[stop]--;
                            }
                        }
                    }

                    // Fully covered section accumulation (soil)
                    if (soilPresent)
                    {
                        int soStart = spanRef.soilStart;
                        int soEnd   = spanRef.soilEnd;
                        int first   = spanRef.soilFirstSec;
                        int last    = spanRef.soilLastSec;

                        bool firstFull = soStart <= sectionBaseYArr[first] && soEnd >= sectionEndYArr[first];
                        bool lastFull  = soStart <= sectionBaseYArr[last]  && soEnd >= sectionEndYArr[last];

                        int fullStart = first;
                        int fullEnd   = last;
                        if (!firstFull) fullStart = first + 1;
                        if (!lastFull)  fullEnd   = last - 1;
                        if (first == last && !(firstFull && lastFull)) fullStart = fullEnd + 1;

                        if (fullStart <= fullEnd)
                        {
                            // Shrink start if stone overlaps from below into soil region
                            if (stonePresent)
                            {
                                int stoneEndLocal = spanRef.stoneEnd;
                                int stoneEndSec = stoneEndLocal >> SECTION_SHIFT;
                                if (stoneEndSec >= fullStart) fullStart = stoneEndSec + 1;
                            }
                            if (fullStart <= fullEnd)
                            {
                                soilDiff[fullStart]++;
                                int stop = fullEnd + 1; if (stop < soilDiff.Length) soilDiff[stop]--;
                            }
                        }
                    }
                }
            }

            // ------------------------------------------------------------------
            // Phase 2: Prefix classification (convert difference arrays -> coverage counts)
            // ------------------------------------------------------------------
            if (globalMaxSectionY >= 0)
            {
                if (globalMinSectionY < 0) globalMinSectionY = 0;
                if (globalMaxSectionY >= sectionsYLocal) globalMaxSectionY = sectionsYLocal - 1;

                int runStone = 0;
                int runSoil  = 0;
                for (int sy = globalMinSectionY; sy <= globalMaxSectionY; sy++)
                {
                    runStone += stoneDiff[sy]; stoneFullCoverCount[sy] = runStone;
                    runSoil  += soilDiff[sy];  soilFullCoverCount[sy]  = runSoil;
                }
            }

            // ------------------------------------------------------------------
            // Phase 3: Whole‑chunk trivial classification
            // ------------------------------------------------------------------
            if (chunkBaseY > maxSurface)
            {
                AllAirChunk = true;
                if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false);
                return;
            }
            // Stone chunk if stone spans fully cover and soil does NOT qualify as full-cover soil everywhere.
            if (allStoneFullCandidate && !allSoilFullCandidate)
            {
                AllStoneChunk = true; CreateUniformSections(StoneId);
            }
            // Soil chunk if soil spans fully cover and stone does NOT fully cover everywhere.
            else if (allSoilFullCandidate && !allStoneFullCandidate)
            {
                AllSoilChunk = true; CreateUniformSections(SoilId);
            }
            if (AllStoneChunk || AllSoilChunk)
            {
                BuildAllBoundaryPlanesInitial();
                if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
                    SetFullyBuried();
                if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false);
                return;
            }

            // ------------------------------------------------------------------
            // Phase 4: Section uniform detection and sparse emission
            // ------------------------------------------------------------------
            if (globalMaxSectionY >= 0)
            {
                // (a) Classify per‑section uniform coverage
                for (int sy = globalMinSectionY; sy <= globalMaxSectionY; sy++)
                {
                    bool stoneUniform = stoneFullCoverCount[sy] == columnCount;
                    bool soilUniform  = !stoneUniform && soilFullCoverCount[sy] == columnCount;

                    if (stoneUniform)
                    {
                        sectionUniformStone[sy] = true;
                        for (int sx = 0; sx < sectionsX; sx++)
                        for (int sz = 0; sz < sectionsZ; sz++)
                        {
                            if (sections[sx, sy, sz] == null)
                            {
                                sections[sx, sy, sz] = new ChunkSection
                                {
                                    IsAllAir = false,
                                    Kind = ChunkSection.RepresentationKind.Uniform,
                                    UniformBlockId = StoneId,
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
                        continue; // soil never overrides stone when stone is uniform
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
                                    UniformBlockId = SoilId,
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

                // (b) Build uniform skip bitset (1 => already uniform) for fast emission skip
                int uniformWordCount = (sectionsYLocal + 63) >> 6;
                Span<ulong> uniformSkipBits = stackalloc ulong[uniformWordCount];
                for (int sy = 0; sy < sectionsYLocal; sy++)
                {
                    if (sectionUniformStone[sy] || sectionUniformSoil[sy])
                    {
                        int w = sy >> 6; int b = sy & 63; uniformSkipBits[w] |= 1UL << b;
                    }
                }

                // (c) Build union bitset of any spans (stone OR soil) to iterate sparsely
                Span<ulong> anySpanBits = stackalloc ulong[bitWordCount];
                for (int w = 0; w < bitWordCount; w++) anySpanBits[w] = stonePresentBits[w] | soilPresentBits[w];

                // Local emission helper (run-wise insertion)
                static void EmitSpan(ChunkSection secRef, int ox, int oz, int localStart, int localEnd, ushort id)
                {
                    if (localStart <= localEnd)
                        SectionUtils.GenerationAddRun(secRef, ox, oz, localStart, localEnd, id);
                }

                // (d) Iterate columns sparsely using trailing-zero bit scanning
                for (int w = 0; w < bitWordCount; w++)
                {
                    ulong word = anySpanBits[w];
                    while (word != 0)
                    {
                        int tz = BitOperations.TrailingZeroCount(word);
                        int colIndex = (w << 6) + tz;
                        word &= word - 1; // clear processed bit
                        if (colIndex >= columnCount) break; // safety (partial final word)

                        ref ColumnSpans spanRef = ref columns[colIndex];
                        ulong mask = 1UL << tz;
                        bool hasStone = (stonePresentBits[w] & mask) != 0UL;
                        bool hasSoil  = (soilPresentBits[w]  & mask) != 0UL;
                        if (!hasStone && !hasSoil) continue; // defensive guard

                        int x = colIndex % maxX;
                        int z = colIndex / maxX;
                        int sxIndex = x >> SECTION_SHIFT; int ox = x & sectionMask;
                        int szIndex = z >> SECTION_SHIFT; int oz = z & sectionMask;

                        // Stone emission
                        if (hasStone)
                        {
                            int firstSec = spanRef.stoneFirstSec;
                            int lastSec  = spanRef.stoneLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec  > globalMaxSectionY) lastSec  = globalMaxSectionY;
                            int ss = spanRef.stoneStart; int se = spanRef.stoneEnd;

                            for (int sy = firstSec; sy <= lastSec; sy++)
                            {
                                int wSkip = sy >> 6; int bSkip = sy & 63;
                                if ((uniformSkipBits[wSkip] & (1UL << bSkip)) != 0UL) continue; // already uniform
                                int sectionBase = sectionBaseYArr[sy];
                                int sectionEnd  = sectionEndYArr[sy];
                                if (se < sectionBase || ss > sectionEnd) continue; // no overlap
                                int clippedStart = ss < sectionBase ? sectionBase : ss;
                                int clippedEnd   = se > sectionEnd ? sectionEnd : se;
                                var secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null) { secRef = new ChunkSection(); sections[sxIndex, sy, szIndex] = secRef; }
                                EmitSpan(secRef, ox, oz, clippedStart - sectionBase, clippedEnd - sectionBase, StoneId);
                            }
                        }

                        // Soil emission
                        if (hasSoil)
                        {
                            int firstSec = spanRef.soilFirstSec;
                            int lastSec  = spanRef.soilLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec  > globalMaxSectionY) lastSec  = globalMaxSectionY;
                            int sols = spanRef.soilStart; int sole = spanRef.soilEnd;

                            for (int sy = firstSec; sy <= lastSec; sy++)
                            {
                                int wSkip = sy >> 6; int bSkip = sy & 63;
                                if ((uniformSkipBits[wSkip] & (1UL << bSkip)) != 0UL) continue;
                                int sectionBase = sectionBaseYArr[sy];
                                int sectionEnd  = sectionEndYArr[sy];
                                if (sole < sectionBase || sols > sectionEnd) continue;
                                int clippedStart = sols < sectionBase ? sectionBase : sols;
                                int clippedEnd   = sole > sectionEnd ? sectionEnd : sole;
                                var secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null) { secRef = new ChunkSection(); sections[sxIndex, sy, szIndex] = secRef; }
                                EmitSpan(secRef, ox, oz, clippedStart - sectionBase, clippedEnd - sectionBase, SoilId);
                            }
                        }
                    }
                }

                if (pooledColumns != null)
                    ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false);
            }
            else if (pooledColumns != null)
            {
                ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false);
            }

            // ------------------------------------------------------------------
            // Phase 5: Whole‑chunk single block collapse (post section creation)
            // ------------------------------------------------------------------
            if (!AllAirChunk)
            {
                bool allUniformSame = true;
                ushort uniformId = 0;

                for (int sx = 0; sx < sectionsX && allUniformSame; sx++)
                for (int sy = 0; sy < sectionsY && allUniformSame; sy++)
                for (int sz = 0; sz < sectionsZ && allUniformSame; sz++)
                {
                    var sec = sections[sx, sy, sz];
                    if (sec == null) continue; // air section
                    if (sec.Kind != ChunkSection.RepresentationKind.Uniform || sec.UniformBlockId == ChunkSection.AIR)
                    {
                        allUniformSame = false; break;
                    }
                    if (uniformId == 0) uniformId = sec.UniformBlockId;
                    else if (sec.UniformBlockId != uniformId) { allUniformSame = false; break; }
                }

                if (allUniformSame && uniformId != 0)
                {
                    AllOneBlockChunk = true;
                    AllOneBlockBlockId = uniformId;
                    // Mark uniform sections clean; metadata already sufficient
                    for (int sx = 0; sx < sectionsX; sx++)
                    for (int sy = 0; sy < sectionsY; sy++)
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null) continue;
                        if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                        {
                            sec.IdMapDirty = false;
                            sec.StructuralDirty = false;
                            sec.MetadataBuilt = true;
                        }
                    }
                }
            }

            // ------------------------------------------------------------------
            // Phase 6: Finalize non‑uniform sections & build boundary planes
            // ------------------------------------------------------------------
            if (!AllOneBlockChunk)
            {
                for (int sx = 0; sx < sectionsX; sx++)
                for (int sy = 0; sy < sectionsY; sy++)
                for (int sz = 0; sz < sectionsZ; sz++)
                {
                    var sec = sections[sx, sy, sz];
                    if (sec == null) continue; // empty (air) section
                    SectionUtils.GenerationFinalizeSection(sec);
                }
            }

            // Build initial boundary planes (using finalized section representations)
            BuildAllBoundaryPlanesInitial();

            // Confirm burial if faces are all solid and candidate still holds
            if (candidateFullyBuried &&
                FaceSolidNegX && FaceSolidPosX &&
                FaceSolidNegY && FaceSolidPosY &&
                FaceSolidNegZ && FaceSolidPosZ)
            {
                SetFullyBuried();
            }
        }
    }
}
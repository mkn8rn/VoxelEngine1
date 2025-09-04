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
        /// Packed per-column span info (array-of-struct).
        /// Each column can have at most two vertical material spans in this terrain phase:
        ///   1. Stone span (stoneStart..stoneEnd inclusive)
        ///   2. Soil span  (soilStart..soilEnd inclusive) directly above stone when present
        /// Section indices (first/last) are cached to avoid recomputing shifts during emission.
        /// Absence of a span is marked ONLY by its presence bitset – struct numeric fields may hold default values that are ignored.
        private struct ColumnSpans
        {
            public short stoneStart;   // inclusive local Y
            public short stoneEnd;     // inclusive local Y
            public short soilStart;    // inclusive local Y
            public short soilEnd;      // inclusive local Y
            public short stoneFirstSec; // first section touched by stone span
            public short stoneLastSec;  // last section touched by stone span
            public short soilFirstSec;  // first section touched by soil span
            public short soilLastSec;   // last section touched by soil span
        }

        /// Generates initial voxel data for the chunk.
        /// Pipeline:
        ///   1. Fused column pass across (x,z) computing: stone & soil spans, whole‑chunk uniform candidates,
        ///      burial candidate, max surface, and fully‑covered section ranges (difference arrays) without emitting voxels.
        ///      This pass now also records only columns that actually have material spans (bitsets) so later emission can be sparse.
        ///   2. Prefix both difference arrays (single loop) to derive per‑section coverage counts -> classify section‑level uniformity.
        ///   3. Whole‑chunk trivial classification (all air / all stone / all soil) with early exits.
        ///   4. Run emission for non‑uniform sections only: enumerate union bitset (stone|soil) → per column, clip spans per section
        ///      and write runs using GenerationAddRun. Uniform sections are skipped via a compact skip bitset.
        ///   5. Whole‑chunk single block collapse check (all sections uniform with same non‑air id) → AllOneBlockChunk fast path.
        ///   6. Finalize remaining non‑uniform sections (representation + metadata) & build chunk boundary planes; confirm burial.
        public void GenerateInitialChunkData()
        {
            // ---- Basic chunk & biome constants ----
            int maxX = dimX, maxY = dimY, maxZ = dimZ;
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);
            int chunkBaseY = (int)position.Y;
            int topOfChunk = chunkBaseY + maxY - 1;

            const int LocalBurialMargin = 2; // small offset near the top face to reject false burial positives
            int burialInvalidateThreshold = topOfChunk - LocalBurialMargin;

            const ushort StoneId = (ushort)BaseBlockType.Stone;
            const ushort SoilId = (ushort)BaseBlockType.Soil;

            int maxSurface = int.MinValue;        // highest surface encountered among columns (for all‑air fast path)
            candidateFullyBuried = true;          // invalidated if any column surface reaches near the top of chunk

            // ---- Biome-configured vertical constraints / depth specs (hoisted) ----
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel;   int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth; int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth; int stoneMaxDepthSpec = biome.stoneMaxDepth;

            // Uniform candidate flags (bit0 = stone possible, bit1 = soil possible). 
            // Soil candidate pre‑filtered by vertical overlap of chunk with soil biome band.
            int uniformFlags = 0b11;
            if (!(topOfChunk >= soilMinY && chunkBaseY <= soilMaxY)) uniformFlags &= ~0b10;

            // ---- Whole-chunk early all-air short-circuit ----
            if (stoneMaxY < chunkBaseY && soilMaxY < chunkBaseY)
            {
                AllAirChunk = true; precomputedHeightmap = null; return;
            }

            // ---- Section precomputation ----
            int sectionSize = ChunkSection.SECTION_SIZE; // 16
            int sectionMask = sectionSize - 1;
            int sectionsYLocal = sectionsY;
            int columnCount = maxX * maxZ; // number of vertical columns in this chunk

            // Precompute base/end Y for each vertical section to avoid repeated shifts/masks.
            Span<int> sectionBaseYArr = stackalloc int[sectionsYLocal];
            Span<int> sectionEndYArr  = stackalloc int[sectionsYLocal];
            for (int sy = 0; sy < sectionsYLocal; sy++)
            {
                int baseY = sy << SECTION_SHIFT; // sy * 16
                sectionBaseYArr[sy] = baseY;
                sectionEndYArr[sy]  = baseY | (sectionSize - 1); // base + 15
            }

            // Per-section full coverage counts (derived from difference arrays later)
            Span<int> stoneFullCoverCount   = stackalloc int[sectionsYLocal]; stoneFullCoverCount.Clear();
            Span<int> soilFullCoverCount    = stackalloc int[sectionsYLocal]; soilFullCoverCount.Clear();
            // Uniform classification flags per section
            Span<bool> sectionUniformStone  = stackalloc bool[sectionsYLocal]; sectionUniformStone.Clear();
            Span<bool> sectionUniformSoil   = stackalloc bool[sectionsYLocal]; sectionUniformSoil.Clear();

            // ---- Span storage (AoS + presence via bitsets) ----
            const int STACKALLOC_COLUMN_THRESHOLD = 512; // threshold to keep on stack for typical 16x16 = 256 grids
            ColumnSpans[] pooledColumns = null;
            Span<ColumnSpans> columns = columnCount <= STACKALLOC_COLUMN_THRESHOLD
                ? stackalloc ColumnSpans[columnCount]
                : (pooledColumns = ArrayPool<ColumnSpans>.Shared.Rent(columnCount)).AsSpan(0, columnCount);

            // Column presence bitsets (stone / soil). Union built later for sparse emission.
            int bitWordCount = (columnCount + 63) >> 6;
            Span<ulong> stonePresentBits = stackalloc ulong[bitWordCount]; stonePresentBits.Clear();
            Span<ulong> soilPresentBits  = stackalloc ulong[bitWordCount]; soilPresentBits.Clear();

            // Track min/max vertical section indices touched by any span to restrict later loops.
            int globalMinSectionY = int.MaxValue;
            int globalMaxSectionY = -1;

            // Difference arrays for fully covered sections (range add technique):
            //   For a fully covered inclusive section index range [a,b]: diff[a]++, diff[b+1]--.
            // After prefix: value == number of columns fully covering that section.
            Span<int> stoneDiff = stackalloc int[sectionsYLocal + 1]; stoneDiff.Clear();
            Span<int> soilDiff  = stackalloc int[sectionsYLocal + 1]; soilDiff.Clear();

            static void SetBit(Span<ulong> bits, int index) => bits[index >> 6] |= 1UL << (index & 63);

            // =====================================================================
            // 1. Fused column pass (row-major: z outer, x inner) – span derivation & coverage accumulation
            // =====================================================================
            for (int z = 0; z < maxZ; z++)
            {
                int rowOffset = z * maxX;
                for (int x = 0; x < maxX; x++)
                {
                    int colIndex = rowOffset + x;           // linear column index
                    int columnHeight = (int)heightmap[x, z]; // world surface height (float -> int)

                    // Update max surface (used for all-air chunk detection)
                    if (columnHeight > maxSurface) maxSurface = columnHeight;
                    // Burial candidate invalidation early (single flag & condition)
                    if (candidateFullyBuried & (columnHeight >= burialInvalidateThreshold)) candidateFullyBuried = false;

                    // Column lies entirely below chunk vertical range -> cannot contain material; invalidate both uniform flags.
                    if (columnHeight < chunkBaseY)
                    {
                        uniformFlags = 0; // neither stone nor soil can be uniform
                        continue;
                    }

                    ref ColumnSpans spanRef = ref columns[colIndex];

                    // ---- Stone span derivation ----
                    // Stone band is constrained by biome [stoneMinY, stoneMaxY] and actual column surface.
                    int stoneBandStartWorld = stoneMinY > 0 ? stoneMinY : 0; // clamp floor to non-negative
                    int stoneBandEndWorld   = stoneMaxY < columnHeight ? stoneMaxY : columnHeight;
                    int available = stoneBandEndWorld - stoneBandStartWorld + 1; // inclusive vertical length of candidate band
                    int stoneDepth = 0;
                    if (available > 0)
                    {
                        // Reserve minimum soil depth above stone (bounded by available)
                        int soilReserve = soilMinDepthSpec; if (soilReserve < 0) soilReserve = 0; if (soilReserve > available) soilReserve = available;
                        int rawStone = available - soilReserve; // candidate stone thickness after reserving soil
                        // Clamp to biome stone depth limits
                        if (rawStone < stoneMinDepthSpec) rawStone = stoneMinDepthSpec;
                        if (rawStone > stoneMaxDepthSpec) rawStone = stoneMaxDepthSpec;
                        if (rawStone > available) rawStone = available;
                        stoneDepth = rawStone > 0 ? rawStone : 0;
                    }
                    // finalStoneTopWorld inclusive; if no stone, set below start so that depth==0 test excludes it.
                    int finalStoneTopWorld = stoneDepth > 0 ? (stoneBandStartWorld + stoneDepth - 1) : (stoneBandStartWorld - 1);

                    // Whole-chunk uniform stone candidate invalidation using compressed flag bit.
                    if ((uniformFlags & 0b01) != 0 && (available <= 0 || chunkBaseY < stoneBandStartWorld || topOfChunk > finalStoneTopWorld))
                        uniformFlags &= ~0b01;

                    // Record stone span (convert to local Y, clamp to chunk vertical bounds) only if non-empty.
                    if (stoneDepth > 0)
                    {
                        int localStoneStart = stoneBandStartWorld - chunkBaseY;
                        int localStoneEnd   = finalStoneTopWorld - chunkBaseY;
                        if (localStoneEnd >= 0 && localStoneStart < maxY) // intersects chunk slab
                        {
                            if (localStoneStart < 0) localStoneStart = 0; if (localStoneEnd >= maxY) localStoneEnd = maxY - 1;
                            if (localStoneStart <= localStoneEnd)
                            {
                                spanRef.stoneStart = (short)localStoneStart;
                                spanRef.stoneEnd   = (short)localStoneEnd;
                                short firstSecTmp = (short)(localStoneStart >> SECTION_SHIFT);
                                short lastSecTmp  = (short)(localStoneEnd   >> SECTION_SHIFT);
                                spanRef.stoneFirstSec = firstSecTmp; spanRef.stoneLastSec = lastSecTmp;
                                SetBit(stonePresentBits, colIndex);
                                if (firstSecTmp < globalMinSectionY) globalMinSectionY = firstSecTmp;
                                if (lastSecTmp  > globalMaxSectionY) globalMaxSectionY = lastSecTmp;
                            }
                        }
                    }

                    // ---- Soil span derivation ----
                    // Soil sits above stone or starts at stone band base when no stone; constrained by biome & surface.
                    int soilStartWorldLocal = stoneDepth > 0 ? (finalStoneTopWorld + 1) : stoneBandStartWorld;
                    if (soilStartWorldLocal < soilMinY) soilStartWorldLocal = soilMinY;
                    int soilEndWorldLocal = -1;
                    if (soilStartWorldLocal <= soilMaxY && soilStartWorldLocal <= columnHeight)
                    {
                        int soilBandCapWorld = soilMaxY < columnHeight ? soilMaxY : columnHeight;
                        if (soilBandCapWorld >= soilStartWorldLocal)
                        {
                            int soilAvailable = soilBandCapWorld - soilStartWorldLocal + 1;
                            if (soilAvailable > 0)
                            {
                                int soilDepth = soilAvailable < soilMaxDepthSpec ? soilAvailable : soilMaxDepthSpec;
                                soilEndWorldLocal = soilStartWorldLocal + soilDepth - 1;
                                int localSoilStart = soilStartWorldLocal - chunkBaseY;
                                int localSoilEnd   = soilEndWorldLocal - chunkBaseY;
                                if (localSoilEnd >= 0 && localSoilStart < maxY)
                                {
                                    if (localSoilStart < 0) localSoilStart = 0; if (localSoilEnd >= maxY) localSoilEnd = maxY - 1;
                                    if (localSoilStart <= localSoilEnd)
                                    {
                                        spanRef.soilStart = (short)localSoilStart;
                                        spanRef.soilEnd   = (short)localSoilEnd;
                                        short firstSecTmp = (short)(localSoilStart >> SECTION_SHIFT);
                                        short lastSecTmp  = (short)(localSoilEnd   >> SECTION_SHIFT);
                                        spanRef.soilFirstSec = firstSecTmp; spanRef.soilLastSec = lastSecTmp;
                                        SetBit(soilPresentBits, colIndex);
                                        if (firstSecTmp < globalMinSectionY) globalMinSectionY = firstSecTmp;
                                        if (lastSecTmp  > globalMaxSectionY) globalMaxSectionY = lastSecTmp;
                                    }
                                }
                            }
                        }
                    }

                    // Whole-chunk uniform soil candidate invalidation
                    if ((uniformFlags & 0b10) != 0)
                    {
                        int soilStartWorldCheck = soilStartWorldLocal;
                        bool invalidSoil = soilStartWorldCheck > soilMaxY || soilEndWorldLocal < 0 ||
                                           (chunkBaseY < soilStartWorldCheck || topOfChunk > soilEndWorldLocal ||
                                            chunkBaseY <= (stoneDepth > 0 ? finalStoneTopWorld : (stoneBandStartWorld - 1)));
                        if (invalidSoil) uniformFlags &= ~0b10;
                    }

                    // ---- Fully covered section accumulation ----
                    // Criteria:
                    //  * Stone section fully covered if stoneStart <= sectionBase && stoneEnd >= sectionEnd AND (no soil overlap intruding).
                    //  * Soil  section fully covered if soilStart  <= sectionBase && soilEnd  >= sectionEnd AND (no stone overlap above).
                    // Edge sections partially overlapped are excluded (we shrink first & last when partial coverage).
                    int wStone = colIndex >> 6; ulong maskStone = 1UL << (colIndex & 63);
                    bool stonePresent = (stonePresentBits[wStone] & maskStone) != 0UL;
                    bool soilPresent  = (soilPresentBits[wStone]  & maskStone) != 0UL;

                    if (stonePresent)
                    {
                        int sStart = spanRef.stoneStart; int sEnd = spanRef.stoneEnd;
                        int firstSec = spanRef.stoneFirstSec; int lastSec = spanRef.stoneLastSec;
                        bool firstFull = sStart <= sectionBaseYArr[firstSec] && sEnd >= sectionEndYArr[firstSec];
                        bool lastFull  = sStart <= sectionBaseYArr[lastSec]  && sEnd >= sectionEndYArr[lastSec];
                        int fullStart = firstSec; int fullEnd = lastSec;
                        if (!firstFull) fullStart = firstSec + 1;
                        if (!lastFull)  fullEnd = lastSec - 1;
                        if (firstSec == lastSec && !(firstFull && lastFull)) fullStart = fullEnd + 1; // invalidate span
                        if (fullStart <= fullEnd)
                        {
                            // Soil begins within covered range → truncate stone coverage so they do not overlap logically.
                            if (soilPresent)
                            {
                                int localSoilStart = spanRef.soilStart;
                                int soilStartSec = localSoilStart >> SECTION_SHIFT;
                                if (soilStartSec <= fullEnd) fullEnd = soilStartSec - 1;
                            }
                            if (fullStart <= fullEnd)
                            {
                                stoneDiff[fullStart]++;
                                int idx = fullEnd + 1; if (idx < stoneDiff.Length) stoneDiff[idx]--;
                            }
                        }
                    }
                    if (soilPresent)
                    {
                        int soStart = spanRef.soilStart; int soEnd = spanRef.soilEnd;
                        int firstSec = spanRef.soilFirstSec; int lastSec = spanRef.soilLastSec;
                        bool firstFull = soStart <= sectionBaseYArr[firstSec] && soEnd >= sectionEndYArr[firstSec];
                        bool lastFull  = soStart <= sectionBaseYArr[lastSec]  && soEnd >= sectionEndYArr[lastSec];
                        int fullStart = firstSec; int fullEnd = lastSec;
                        if (!firstFull) fullStart = firstSec + 1;
                        if (!lastFull)  fullEnd = lastSec - 1;
                        if (firstSec == lastSec && !(firstFull && lastFull)) fullStart = fullEnd + 1;
                        if (fullStart <= fullEnd)
                        {
                            // If stone overlaps from below bridging into soil region, shrink soil coverage start.
                            if (stonePresent)
                            {
                                int sEnd = spanRef.stoneEnd;
                                int stoneOverlapSec = sEnd >> SECTION_SHIFT;
                                if (stoneOverlapSec >= fullStart) fullStart = stoneOverlapSec + 1;
                            }
                            if (fullStart <= fullEnd)
                            {
                                soilDiff[fullStart]++;
                                int idx = fullEnd + 1; if (idx < soilDiff.Length) soilDiff[idx]--;
                            }
                        }
                    }
                }
            }

            // =====================================================================
            // 2. Prefix classification (build per-section coverage counts)
            // =====================================================================
            if (globalMaxSectionY >= 0)
            {
                if (globalMinSectionY < 0) globalMinSectionY = 0;
                if (globalMaxSectionY >= sectionsYLocal) globalMaxSectionY = sectionsYLocal - 1;
                int runStone = 0, runSoil = 0;
                for (int sy = globalMinSectionY; sy <= globalMaxSectionY; sy++)
                {
                    runStone += stoneDiff[sy]; stoneFullCoverCount[sy] = runStone;
                    runSoil  += soilDiff[sy]; soilFullCoverCount[sy] = runSoil;
                }
            }

            // =====================================================================
            // 3. Whole-chunk trivial classification & early exits
            // =====================================================================
            if (chunkBaseY > maxSurface) // chunk lies completely above the highest surface across all columns
            {
                AllAirChunk = true; precomputedHeightmap = null; if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false); return;
            }
            if ((uniformFlags & 0b01) != 0 && (uniformFlags & 0b10) == 0) { AllStoneChunk = true; CreateUniformSections(StoneId); }
            else if ((uniformFlags & 0b10) != 0 && (uniformFlags & 0b01) == 0) { AllSoilChunk = true; CreateUniformSections(SoilId); }
            if (AllStoneChunk || AllSoilChunk)
            {
                precomputedHeightmap = null; BuildAllBoundaryPlanesInitial();
                if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ) SetFullyBuried();
                if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false);
                return;
            }

            // =====================================================================
            // 4. Section uniform detection (stone preference) + sparse span emission
            // =====================================================================
            if (globalMaxSectionY >= 0)
            {
                // Classify section uniformity from coverage counts
                for (int sy = globalMinSectionY; sy <= globalMaxSectionY; sy++)
                {
                    bool stoneUniform = stoneFullCoverCount[sy] == columnCount;
                    bool soilUniform  = !stoneUniform && soilFullCoverCount[sy] == columnCount;
                    if (stoneUniform)
                    {
                        sectionUniformStone[sy] = true;
                        for (int sx = 0; sx < sectionsX; sx++)
                        for (int sz = 0; sz < sectionsZ; sz++)
                            if (sections[sx, sy, sz] == null)
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
                        continue;
                    }
                    if (soilUniform)
                    {
                        sectionUniformSoil[sy] = true;
                        for (int sx = 0; sx < sectionsX; sx++)
                        for (int sz = 0; sz < sectionsZ; sz++)
                            if (sections[sx, sy, sz] == null)
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

                // Build uniform skip bitset: 1 means this section is already uniform (skip emission entirely).
                int uniformWordCount = (sectionsYLocal + 63) >> 6;
                Span<ulong> uniformSkipBits = stackalloc ulong[uniformWordCount];
                for (int sy = 0; sy < sectionsYLocal; sy++)
                {
                    if (sectionUniformStone[sy] || sectionUniformSoil[sy])
                    {
                        int w = sy >> 6; int b = sy & 63; uniformSkipBits[w] |= 1UL << b;
                    }
                }

                // Union bitset of columns with any span (stone OR soil). Enables sparse emission.
                Span<ulong> anySpanBits = stackalloc ulong[bitWordCount];
                for (int w = 0; w < bitWordCount; w++) anySpanBits[w] = stonePresentBits[w] | soilPresentBits[w];

                // Unified emission helper (local) – intentionally small for inlining
                static void EmitSpan(ChunkSection secRef, int ox, int oz, int localStart, int localEnd, ushort id)
                {
                    if (localStart <= localEnd) SectionUtils.GenerationAddRun(secRef, ox, oz, localStart, localEnd, id);
                }

                // Enumerate populated columns using trailing zero iteration per 64‑bit word.
                for (int w = 0; w < bitWordCount; w++)
                {
                    ulong word = anySpanBits[w];
                    while (word != 0)
                    {
                        int tz = BitOperations.TrailingZeroCount(word);
                        int colIndex = (w << 6) + tz;
                        word &= word - 1; // clear lowest set bit
                        if (colIndex >= columnCount) break; // safety guard (partial final word)

                        ref ColumnSpans spanRef = ref columns[colIndex];
                        ulong mask = 1UL << tz;
                        bool hasStone = (stonePresentBits[w] & mask) != 0UL;
                        bool hasSoil  = (soilPresentBits[w]  & mask) != 0UL;
                        if (!hasStone && !hasSoil) continue; // defensive

                        int x = colIndex % maxX; int z = colIndex / maxX;
                        int sxIndex = x >> SECTION_SHIFT; int ox = x & sectionMask;
                        int szIndex = z >> SECTION_SHIFT; int oz = z & sectionMask;

                        // Stone emission
                        if (hasStone)
                        {
                            int firstSec = spanRef.stoneFirstSec; int lastSec = spanRef.stoneLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec  > globalMaxSectionY) lastSec  = globalMaxSectionY;
                            int ss = spanRef.stoneStart; int se = spanRef.stoneEnd;
                            for (int sy = firstSec; sy <= lastSec; sy++)
                            {
                                int wSkip = sy >> 6; int bSkip = sy & 63; if ((uniformSkipBits[wSkip] & (1UL << bSkip)) != 0UL) continue;
                                int sectionBase = sectionBaseYArr[sy]; int sectionEnd = sectionEndYArr[sy];
                                if (se < sectionBase || ss > sectionEnd) continue; // no overlap
                                int clippedStart = ss < sectionBase ? sectionBase : ss;
                                int clippedEnd   = se > sectionEnd ? sectionEnd : se;
                                ChunkSection secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null) { secRef = new ChunkSection(); sections[sxIndex, sy, szIndex] = secRef; }
                                EmitSpan(secRef, ox, oz, clippedStart - sectionBase, clippedEnd - sectionBase, StoneId);
                            }
                        }
                        // Soil emission
                        if (hasSoil)
                        {
                            int firstSec = spanRef.soilFirstSec; int lastSec = spanRef.soilLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec  > globalMaxSectionY) lastSec  = globalMaxSectionY;
                            int sols = spanRef.soilStart; int sole = spanRef.soilEnd;
                            for (int sy = firstSec; sy <= lastSec; sy++)
                            {
                                int wSkip = sy >> 6; int bSkip = sy & 63; if ((uniformSkipBits[wSkip] & (1UL << bSkip)) != 0UL) continue;
                                int sectionBase = sectionBaseYArr[sy]; int sectionEnd = sectionEndYArr[sy];
                                if (sole < sectionBase || sols > sectionEnd) continue;
                                int clippedStart = sols < sectionBase ? sectionBase : sols;
                                int clippedEnd   = sole > sectionEnd ? sectionEnd : sole;
                                ChunkSection secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null) { secRef = new ChunkSection(); sections[sxIndex, sy, szIndex] = secRef; }
                                EmitSpan(secRef, ox, oz, clippedStart - sectionBase, clippedEnd - sectionBase, SoilId);
                            }
                        }
                    }
                }

                if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false);
            }
            else if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, false);

            // =====================================================================
            // 5. Whole-chunk single block collapse (post section creation)
            // =====================================================================
            if (!AllAirChunk)
            {
                bool allUniformSame = true; ushort uniformId = 0;
                for (int sx = 0; sx < sectionsX && allUniformSame; sx++)
                for (int sy = 0; sy < sectionsY && allUniformSame; sy++)
                for (int sz = 0; sz < sectionsZ && allUniformSame; sz++)
                {
                    var sec = sections[sx, sy, sz]; if (sec == null) continue;
                    if (sec.Kind != ChunkSection.RepresentationKind.Uniform || sec.UniformBlockId == ChunkSection.AIR) { allUniformSame = false; break; }
                    if (uniformId == 0) uniformId = sec.UniformBlockId; else if (sec.UniformBlockId != uniformId) { allUniformSame = false; break; }
                }
                if (allUniformSame && uniformId != 0)
                {
                    AllOneBlockChunk = true; AllOneBlockBlockId = uniformId;
                    for (int sx = 0; sx < sectionsX; sx++)
                    for (int sy = 0; sy < sectionsY; sy++)
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz]; if (sec == null) continue;
                        if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                        { sec.IdMapDirty = false; sec.StructuralDirty = false; sec.MetadataBuilt = true; }
                    }
                }
            }

            // =====================================================================
            // 6. Finalize non-uniform sections & build boundary planes
            // =====================================================================
            if (!AllOneBlockChunk)
            {
                for (int sx = 0; sx < sectionsX; sx++)
                for (int sy = 0; sy < sectionsY; sy++)
                for (int sz = 0; sz < sectionsZ; sz++)
                { var sec = sections[sx, sy, sz]; if (sec == null) continue; SectionUtils.GenerationFinalizeSection(sec); }
            }

            precomputedHeightmap = null; // release heightmap reference (chunk now self-contained)
            BuildAllBoundaryPlanesInitial();
            // Burial confirmation after verifying all six faces are fully solid
            if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
                SetFullyBuried();
        }
    }
}
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
        /// Packed per-column span info (Array-of-Struct to improve locality vs prior 8 separate int[]).
        /// Each column can have at most two vertical material spans in this terrain phase:
        ///   1. Stone span (stoneStart..stoneEnd)
        ///   2. Soil span  (soilStart..soilEnd) directly above stone when present
        /// Section indices (first/last) are cached to avoid recomputing shifts during emission.
        /// Absence of a span is represented by NOT setting the corresponding presence bit; the short
        /// fields may contain default 0 values that are ignored unless the bitset marks presence.
        private struct ColumnSpans
        {
            // Local Y (0..maxY-1). Short is sufficient (chunk heights << 32k). Values only valid if presence bit set.
            public short stoneStart;
            public short stoneEnd;
            public short soilStart;
            public short soilEnd;
            // Cached section indices for faster emission (derived via >> SECTION_SHIFT during fused pass)
            public short stoneFirstSec;
            public short stoneLastSec;
            public short soilFirstSec;
            public short soilLastSec;
        }

        /// Generates initial voxel data for the chunk.
        /// High level pipeline inside this method:
        ///   1. Fused column pass across (x,z) computing: stone & soil spans, whole-chunk uniform candidates,
        ///      burial candidate, max surface, and per-section full-cover counters (for later uniform section detection).
        ///   2. Decide whole-chunk trivial classifications (all air / all stone / all soil).
        ///   3. Decide uniform sections using coverage counters.
        ///   4. Emit per-column runs only for non-uniform sections (stone first, then soil).
        ///   5. Attempt whole-chunk single block short-circuit (AllOneBlockChunk).
        ///   6. Finalize non-uniform sections (metadata build) & build boundary planes / burial confirmation.
        public void GenerateInitialChunkData()
        {
            // ---- Basic chunk & biome constants ----
            int maxX = dimX; int maxY = dimY; int maxZ = dimZ;
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);
            int chunkBaseY = (int)position.Y;
            int topOfChunk = chunkBaseY + maxY - 1;

            const int LocalBurialMargin = 2; // small offset below very top to reduce false positives on burial detection
            int burialInvalidateThreshold = topOfChunk - LocalBurialMargin;

            const ushort StoneId = (ushort)BaseBlockType.Stone;
            const ushort SoilId  = (ushort)BaseBlockType.Soil;

            int maxSurface = int.MinValue;        // track max surface height encountered (above-chunk all-air fast path)
            candidateFullyBuried = true;          // becomes false if any tall column violates burial criterion

            // Biome-configured vertical constraints / depth specs (hoisted once)
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY  = biome.soilMinYLevel;  int soilMaxY  = biome.soilMaxYLevel;
            int soilMinDepthSpec  = biome.soilMinDepth;  int soilMaxDepthSpec  = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth; int stoneMaxDepthSpec = biome.stoneMaxDepth;

            bool possibleStone = true; // optimistic whole-chunk uniform stone candidate until disproved
            bool possibleSoil  = (topOfChunk >= soilMinY) && (chunkBaseY <= soilMaxY); // whole-chunk uniform soil candidate

            // ---- Section precomputation ----
            int sectionSize = ChunkSection.SECTION_SIZE; // 16
            int sectionMask = sectionSize - 1;
            int sectionsYLocal = sectionsY;
            int columnCount = maxX * maxZ;

            // Precompute base/end Y for every vertical section (avoids multiply in hot loops)
            Span<int> sectionBaseYArr = stackalloc int[sectionsYLocal];
            Span<int> sectionEndYArr  = stackalloc int[sectionsYLocal];
            for (int sy = 0; sy < sectionsYLocal; sy++)
            {
                int baseY = sy << SECTION_SHIFT; // sy * 16
                sectionBaseYArr[sy] = baseY;
                sectionEndYArr[sy]  = baseY | sectionMask; // base + 15
            }

            // Per-section full coverage counters used to decide uniform sections post-fused pass
            Span<int>  stoneFullCoverCount   = stackalloc int[sectionsYLocal]; stoneFullCoverCount.Clear();
            Span<int>  soilFullCoverCount    = stackalloc int[sectionsYLocal]; soilFullCoverCount.Clear();
            Span<bool> sectionUniformStone   = stackalloc bool[sectionsYLocal]; sectionUniformStone.Clear();
            Span<bool> sectionUniformSoil    = stackalloc bool[sectionsYLocal]; sectionUniformSoil.Clear();

            // ---- Span storage (AoS + bit presence) ----
            // Using stackalloc for typical small grids. Fallback to ArrayPool for safety if dimensions grow.
            const int STACKALLOC_COLUMN_THRESHOLD = 512;
            ColumnSpans[] pooledColumns = null;
            Span<ColumnSpans> columns = columnCount <= STACKALLOC_COLUMN_THRESHOLD
                ? stackalloc ColumnSpans[columnCount]
                : (pooledColumns = ArrayPool<ColumnSpans>.Shared.Rent(columnCount)).AsSpan(0, columnCount);

            // Bitsets mark which columns actually have a stone / soil span populated (avoid initializing structs)
            int bitWordCount = (columnCount + 63) >> 6;
            Span<ulong> stonePresentBits = stackalloc ulong[bitWordCount]; stonePresentBits.Clear();
            Span<ulong> soilPresentBits  = stackalloc ulong[bitWordCount]; soilPresentBits.Clear();

            static void SetBit(Span<ulong> bits, int index) => bits[index >> 6] |= 1UL << (index & 63);
            static bool HasBit(Span<ulong> bits, int index) => (bits[index >> 6] & (1UL << (index & 63))) != 0UL;

            // Track min/max vertical section indices actually touched by any span (bands later used to restrict loops)
            int globalMinSectionY = int.MaxValue;
            int globalMaxSectionY = -1;

            // ----------------------------------------------------------------------
            // Difference (prefix) arrays for full coverage counting
            // We accumulate ONLY ranges of fully covered sections using +1/-1 range adds:
            //   stoneDiff[a]++, stoneDiff[b+1]--  (section indices inclusive a..b)
            // After fused pass, prefix sum transforms into per-section coverage count.
            // ----------------------------------------------------------------------
            Span<int> stoneDiff = stackalloc int[sectionsYLocal + 1]; stoneDiff.Clear();
            Span<int> soilDiff  = stackalloc int[sectionsYLocal + 1]; soilDiff.Clear();

            // ======================================================================
            // 1. Fused column pass: span derivation + coverage accumulation
            // ======================================================================
            for (int x = 0; x < maxX; x++)
            {
                // Stride pattern (x + z*maxX) expressed as colIndex = x; colIndex += maxX each z
                int colIndex = x;
                for (int z = 0; z < maxZ; z++, colIndex += maxX)
                {
                    int columnHeight = (int)heightmap[x, z];

                    // Track max surface for above-chunk cull
                    if (columnHeight > maxSurface) maxSurface = columnHeight;

                    // Burial invalidation (any column beneath near-top threshold disqualifies candidate)
                    if (candidateFullyBuried && columnHeight >= burialInvalidateThreshold)
                        candidateFullyBuried = false;

                    // Entire column below this chunk -> contributes only to uniform whole-chunk invalidation
                    if (columnHeight < chunkBaseY)
                    {
                        if (possibleStone || possibleSoil)
                        {
                            possibleStone = false; possibleSoil = false;
                        }
                        continue;
                    }

                    // ---------------- Stone band derivation ----------------
                    // Compute possible vertical band for stone limited by biome range & column surface.
                    int stoneBandStartWorld = stoneMinY > 0 ? stoneMinY : 0; // clamp to non-negative world Y
                    int stoneBandEndWorld   = stoneMaxY < columnHeight ? stoneMaxY : columnHeight;
                    int available = stoneBandEndWorld - stoneBandStartWorld + 1; // inclusive length
                    int stoneDepth = 0;
                    if (available > 0)
                    {
                        // Reserve soil minimum depth (cannot exceed available)
                        int soilMinReserve = Math.Clamp(soilMinDepthSpec, 0, available);
                        int rawStoneDepth  = available - soilMinReserve;
                        // Clamp stone depth within biome-specified depth limits
                        stoneDepth = Math.Min(stoneMaxDepthSpec, Math.Max(stoneMinDepthSpec, rawStoneDepth));
                        if (stoneDepth > available) stoneDepth = available;
                    }
                    int finalStoneTopWorld = stoneBandStartWorld + stoneDepth - 1; // top of stone layer (inclusive)
                    if (stoneDepth == 0) finalStoneTopWorld = stoneBandStartWorld - 1; // makes stone span empty

                    // Whole-chunk uniform stone candidate invalidation
                    if (possibleStone && (available <= 0 || chunkBaseY < stoneBandStartWorld || topOfChunk > finalStoneTopWorld))
                        possibleStone = false;

                    ref ColumnSpans spanRef = ref columns[colIndex]; // reference to packed span struct for this column

                    // Local stone span registration (if non-empty and intersects chunk vertical range)
                    if (stoneDepth > 0)
                    {
                        int localStoneStart = stoneBandStartWorld - chunkBaseY;
                        int localStoneEnd   = finalStoneTopWorld - chunkBaseY;
                        if (localStoneEnd >= 0 && localStoneStart < maxY) // intersects chunk
                        {
                            localStoneStart = Math.Clamp(localStoneStart, 0, maxY - 1);
                            localStoneEnd   = Math.Clamp(localStoneEnd,   0, maxY - 1);
                            if (localStoneStart <= localStoneEnd)
                            {
                                spanRef.stoneStart = (short)localStoneStart;
                                spanRef.stoneEnd   = (short)localStoneEnd;
                                int firstSecTmp = localStoneStart >> SECTION_SHIFT; // divide by 16
                                int lastSecTmp  = localStoneEnd   >> SECTION_SHIFT;
                                spanRef.stoneFirstSec = (short)firstSecTmp;
                                spanRef.stoneLastSec  = (short)lastSecTmp;
                                SetBit(stonePresentBits, colIndex);
                                if (firstSecTmp < globalMinSectionY) globalMinSectionY = firstSecTmp;
                                if (lastSecTmp  > globalMaxSectionY) globalMaxSectionY = lastSecTmp;
                            }
                        }
                    }

                    // ---------------- Soil band derivation ----------------
                    int soilStartWorldLocal = (stoneDepth > 0 ? (finalStoneTopWorld + 1) : stoneBandStartWorld);
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
                                    localSoilStart = Math.Clamp(localSoilStart, 0, maxY - 1);
                                    localSoilEnd   = Math.Clamp(localSoilEnd,   0, maxY - 1);
                                    if (localSoilStart <= localSoilEnd)
                                    {
                                        spanRef.soilStart = (short)localSoilStart;
                                        spanRef.soilEnd   = (short)localSoilEnd;
                                        int firstSecTmp = localSoilStart >> SECTION_SHIFT;
                                        int lastSecTmp  = localSoilEnd   >> SECTION_SHIFT;
                                        spanRef.soilFirstSec = (short)firstSecTmp;
                                        spanRef.soilLastSec  = (short)lastSecTmp;
                                        SetBit(soilPresentBits, colIndex);
                                        if (firstSecTmp < globalMinSectionY) globalMinSectionY = firstSecTmp;
                                        if (lastSecTmp  > globalMaxSectionY) globalMaxSectionY = lastSecTmp;
                                    }
                                }
                            }
                        }
                    }

                    // Whole-chunk uniform soil candidate invalidation
                    if (possibleSoil)
                    {
                        int soilStartWorldCheck = soilStartWorldLocal;
                        bool invalid = soilStartWorldCheck > soilMaxY || soilEndWorldLocal < 0 ||
                                       (chunkBaseY < soilStartWorldCheck || topOfChunk > soilEndWorldLocal ||
                                        chunkBaseY <= (stoneDepth > 0 ? finalStoneTopWorld : (stoneBandStartWorld - 1)));
                        if (invalid) possibleSoil = false;
                    }

                    // ---------------- Per-section coverage accumulation ----------------
                    // Computes contiguous fully covered ranges and apply range-add (+1/-1) to difference arrays.
                    // Rules for full coverage:
                    //  * Stone section fully covered if stoneStart <= sectionBaseY && stoneEnd >= sectionEndY AND (no soil overlaps section)
                    //  * Soil  section fully covered if soilStart  <= sectionBaseY && soilEnd  >= sectionEndY AND (no stone overlaps section)
                    // Edge (first/last) sections can be partial — we exclude them when not fully covered.

                    if (HasBit(stonePresentBits, colIndex))
                    {
                        int sStart = spanRef.stoneStart;
                        int sEnd   = spanRef.stoneEnd;
                        int firstSec = spanRef.stoneFirstSec;
                        int lastSec  = spanRef.stoneLastSec;
                        // Determine if first / last sections are fully covered
                        bool firstFull = sStart <= sectionBaseYArr[firstSec] && sEnd >= sectionEndYArr[firstSec];
                        bool lastFull  = sStart <= sectionBaseYArr[lastSec]  && sEnd >= sectionEndYArr[lastSec];
                        int fullStart = firstSec;
                        int fullEnd   = lastSec;
                        if (!firstFull) fullStart = firstSec + 1;
                        if (!lastFull)  fullEnd   = lastSec - 1;
                        if (firstSec == lastSec)
                        {
                            // Single-section span; require full coverage
                            if (!(firstFull && lastFull)) fullStart = fullEnd + 1; // invalidate
                        }
                        if (fullStart <= fullEnd)
                        {
                            // Soil overlap truncation: if soil present and begins within/at a fully covered section, cut range.
                            if (HasBit(soilPresentBits, colIndex))
                            {
                                int localSoilStart = spanRef.soilStart; // local Y
                                int soilStartSec = localSoilStart >> SECTION_SHIFT;
                                // A section sy overlaps soil if sectionEndY >= soilStartLocal → all sections sy >= soilStartSec
                                if (soilStartSec <= fullEnd)
                                    fullEnd = soilStartSec - 1;
                            }
                            if (fullStart <= fullEnd)
                            {
                                stoneDiff[fullStart]++;
                                if (fullEnd + 1 < stoneDiff.Length) stoneDiff[fullEnd + 1]--;
                            }
                        }
                    }
                    if (HasBit(soilPresentBits, colIndex))
                    {
                        int soStart = spanRef.soilStart;
                        int soEnd   = spanRef.soilEnd;
                        int firstSec = spanRef.soilFirstSec;
                        int lastSec  = spanRef.soilLastSec;
                        bool firstFull = soStart <= sectionBaseYArr[firstSec] && soEnd >= sectionEndYArr[firstSec];
                        bool lastFull  = soStart <= sectionBaseYArr[lastSec]  && soEnd >= sectionEndYArr[lastSec];
                        int fullStart = firstSec;
                        int fullEnd   = lastSec;
                        if (!firstFull) fullStart = firstSec + 1;
                        if (!lastFull)  fullEnd   = lastSec - 1;
                        if (firstSec == lastSec)
                        {
                            if (!(firstFull && lastFull)) fullStart = fullEnd + 1;
                        }
                        if (fullStart <= fullEnd)
                        {
                            // Stone overlap truncation: stone span overlaps soil section if stoneEnd >= sectionBaseY
                            if (HasBit(stonePresentBits, colIndex))
                            {
                                int sEnd   = spanRef.stoneEnd;
                                int stoneOverlapSec = sEnd >> SECTION_SHIFT; // highest section whose baseY <= stoneEnd
                                if (stoneOverlapSec >= fullStart)
                                    fullStart = stoneOverlapSec + 1;
                            }
                            if (fullStart <= fullEnd)
                            {
                                soilDiff[fullStart]++;
                                if (fullEnd + 1 < soilDiff.Length) soilDiff[fullEnd + 1]--;
                            }
                        }
                    }
                }
            }

            // Convert difference arrays to full coverage counts (prefix sums)
            if (globalMaxSectionY >= 0)
            {
                int run = 0;
                for (int sy = globalMinSectionY; sy <= globalMaxSectionY && sy < sectionsYLocal; sy++)
                {
                    run += stoneDiff[sy];
                    stoneFullCoverCount[sy] = run;
                }
                run = 0;
                for (int sy = globalMinSectionY; sy <= globalMaxSectionY && sy < sectionsYLocal; sy++)
                {
                    run += soilDiff[sy];
                    soilFullCoverCount[sy] = run;
                }
            }

            // ======================================================================
            // 2. Whole-chunk trivial classification & early exits
            // ======================================================================
            if (chunkBaseY > maxSurface) // chunk lies completely above surface
            {
                AllAirChunk = true; precomputedHeightmap = null; if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, clearArray: false); return;
            }
            if (possibleStone && !possibleSoil) { AllStoneChunk = true; CreateUniformSections(StoneId); }
            else if (possibleSoil && !possibleStone) { AllSoilChunk = true; CreateUniformSections(SoilId); }
            if (AllStoneChunk || AllSoilChunk)
            {
                precomputedHeightmap = null;
                BuildAllBoundaryPlanesInitial();
                if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ) SetFullyBuried();
                if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, clearArray: false);
                return;
            }

            // ======================================================================
            // 3. Section uniform detection + 4. Mixed span emission
            // ======================================================================
            if (globalMaxSectionY >= 0)
            {
                if (globalMinSectionY < 0) globalMinSectionY = 0;
                if (globalMaxSectionY >= sectionsYLocal) globalMaxSectionY = sectionsYLocal - 1;

                // Decide section-level uniformity (stone preferred over soil if both would apply)
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

                // Emit stone & soil runs only for sections not resolved as uniform
                for (int x = 0; x < maxX; x++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        int colIndex2 = z * maxX + x; // row-major indexing (improves locality during emission)
                        ref ColumnSpans spanRef = ref columns[colIndex2];
                        bool hasStone = HasBit(stonePresentBits, colIndex2);
                        bool hasSoil  = HasBit(soilPresentBits,  colIndex2);
                        if (!hasStone && !hasSoil) continue;

                        int sxIndex = x >> SECTION_SHIFT; int ox = x & sectionMask;
                        int szIndex = z >> SECTION_SHIFT; int oz = z & sectionMask;

                        // Stone span emission
                        if (hasStone)
                        {
                            int firstSec = spanRef.stoneFirstSec;
                            int lastSec  = spanRef.stoneLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec  > globalMaxSectionY) lastSec = globalMaxSectionY;
                            int ss = spanRef.stoneStart; int se = spanRef.stoneEnd;
                            for (int sy = firstSec; sy <= lastSec && sy < sectionsYLocal; sy++)
                            {
                                if (sectionUniformStone[sy] || sectionUniformSoil[sy]) continue; // skip already uniform sections
                                int sectionBaseY = sectionBaseYArr[sy];
                                int sectionEndY  = sectionEndYArr[sy];
                                if (se < sectionBaseY || ss > sectionEndY) continue; // no overlap
                                int clippedStart = ss < sectionBaseY ? sectionBaseY : ss;
                                int clippedEnd   = se > sectionEndY ? sectionEndY : se;
                                int localStart   = clippedStart - sectionBaseY;
                                int localEnd     = clippedEnd   - sectionBaseY;
                                if (localStart > localEnd) continue;
                                ChunkSection secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null)
                                {
                                    secRef = new ChunkSection();
                                    sections[sxIndex, sy, szIndex] = secRef;
                                }
                                SectionUtils.GenerationAddRun(secRef, ox, oz, localStart, localEnd, StoneId);
                            }
                        }
                        // Soil span emission
                        if (hasSoil)
                        {
                            int firstSec = spanRef.soilFirstSec;
                            int lastSec  = spanRef.soilLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec  > globalMaxSectionY) lastSec = globalMaxSectionY;
                            int sols = spanRef.soilStart; int sole = spanRef.soilEnd;
                            for (int sy = firstSec; sy <= lastSec && sy < sectionsYLocal; sy++)
                            {
                                if (sectionUniformStone[sy] || sectionUniformSoil[sy]) continue;
                                int sectionBaseY = sectionBaseYArr[sy];
                                int sectionEndY  = sectionEndYArr[sy];
                                if (sole < sectionBaseY || sols > sectionEndY) continue;
                                int clippedStart = sols < sectionBaseY ? sectionBaseY : sols;
                                int clippedEnd   = sole > sectionEndY ? sectionEndY : sole;
                                int localStart   = clippedStart - sectionBaseY;
                                int localEnd     = clippedEnd   - sectionBaseY;
                                if (localStart > localEnd) continue;
                                ChunkSection secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null)
                                {
                                    secRef = new ChunkSection();
                                    sections[sxIndex, sy, szIndex] = secRef;
                                }
                                SectionUtils.GenerationAddRun(secRef, ox, oz, localStart, localEnd, SoilId);
                            }
                        }
                    }
                }

                if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, clearArray: false);
            }
            else // No spans -> nothing to emit; release pooled storage
            {
                if (pooledColumns != null) ArrayPool<ColumnSpans>.Shared.Return(pooledColumns, clearArray: false);
            }

            // ======================================================================
            // 5. Whole-chunk single block collapse (post section creation)
            // ======================================================================
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
                    // Clear dirty flags; metadata for uniform sections already valid.
                    for (int sx = 0; sx < sectionsX; sx++)
                    for (int sy = 0; sy < sectionsY; sy++)
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz]; if (sec == null) continue;
                        if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                        {
                            sec.IdMapDirty = false; sec.StructuralDirty = false; sec.MetadataBuilt = true;
                        }
                    }
                }
            }

            // ======================================================================
            // 6. Finalize sections & build boundary planes
            // ======================================================================
            if (!AllOneBlockChunk)
            {
                for (int sx = 0; sx < sectionsX; sx++)
                for (int sy = 0; sy < sectionsY; sy++)
                for (int sz = 0; sz < sectionsZ; sz++)
                { var sec = sections[sx, sy, sz]; if (sec == null) continue; SectionUtils.GenerationFinalizeSection(sec); }
            }

            precomputedHeightmap = null; // release heightmap reference (allow GC if shared cache not used)
            BuildAllBoundaryPlanesInitial();

            // Burial confirmation only after boundary planes confirm all six faces solid
            if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
                SetFullyBuried();
        }
    }
}
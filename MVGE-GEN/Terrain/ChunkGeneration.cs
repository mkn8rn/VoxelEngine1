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
        /// Per (x,z) column material spans inside this chunk's vertical slab.
        /// Each column can contain at most one contiguous stone span and one contiguous soil span directly above it.
        /// Section indices (first/last) are cached for emission & coverage calculations.
        /// Presence is indicated via bit flags; default field values are ignored when the span is absent.
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
        ///  1. Column pass (derivation): Using precomputed world BlockColumnProfile spans (columnSpanMap) -> clip to chunk, record spans
        ///     and accumulate fully‑covered section ranges directly into per‑section bitsets (stone / soil) plus per‑column material flags.
        ///     (Difference arrays removed; coverage now tracked by per‑section 256‑bit masks.)
        ///  2. Section uniform & whole chunk trivial classification: Detect AllAir / AllStone / AllSoil & per‑section stone/soil uniform
        ///     by testing full coverage bitsets against FULL mask. (Prefix phase removed.)
        ///  3. Section emission: skip uniform sections; emit remaining partial spans sparsely using recorded spans & material flags.
        ///  4. Whole chunk single block collapse (AllOneBlockChunk) if all sections are uniform with the same id.
        ///  5. Finalization: finalize non‑uniform sections and build boundary planes; confirm burial.
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
            const ushort SoilId = (ushort)BaseBlockType.Soil;

            int maxSurface = int.MinValue;                  // track highest column surface (all‑air early rejection)
            candidateFullyBuried = true;                    // invalidated if any surface approaches chunk top

            // Biome vertical bands (used for early band overlap tests)
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel; int soilMaxY = biome.soilMaxYLevel;

            // ------------------------------------------------------------------
            // Aggregated uniform classification candidates (replaces per-column uniformFlags invalidation logic)
            // ------------------------------------------------------------------
            bool allStoneFullCandidate = true; // remains true only if every column's stone span fully covers the chunk slab (and exists).
            bool allSoilFullCandidate = true;  // remains true only if every column's soil span fully covers the chunk slab AND stone does not intrude.
            if (!(topOfChunk >= soilMinY && chunkBaseY <= soilMaxY))
                allSoilFullCandidate = false; // soil band miss => impossible

            // ------------------------------------------------------------------
            // Section precomputation & storage containers
            // ------------------------------------------------------------------
            int sectionSize = ChunkSection.SECTION_SIZE; // expected 16
            int sectionMask = sectionSize - 1;
            int sectionsYLocal = sectionsY;
            int columnCount = maxX * maxZ; // total vertical columns in this chunk (expected 256)

            // Precompute section base/end Y (retain original comments; used for clipping & span decomposition)
            Span<int> sectionBaseYArr = stackalloc int[sectionsYLocal];
            Span<int> sectionEndYArr = stackalloc int[sectionsYLocal];
            for (int sy = 0; sy < sectionsYLocal; sy++)
            {
                int baseY = sy << SECTION_SHIFT; // faster than sy * 16
                sectionBaseYArr[sy] = baseY;
                sectionEndYArr[sy] = baseY | (sectionSize - 1); // inclusive end
            }

            // Column spans (AoS) – always stackalloc (columnCount fixed at 256). Pooling removed per optimization request.
            Span<ColumnSpans> columns = stackalloc ColumnSpans[columnCount];

            // Per-column material presence flags (bit0=stone, bit1=soil). Replaces separate stone/soil presence bitsets.
            Span<byte> columnMaterial = stackalloc byte[columnCount]; columnMaterial.Clear();

            // Union bitset of any material presence (for sparse iteration later). 256 columns -> 4 words.
            int columnWordCount = (columnCount + 63) >> 6; // expected 4
            Span<ulong> anySpanBits = stackalloc ulong[columnWordCount]; anySpanBits.Clear();

            // Per-section full coverage bitsets (stone / soil). Each section has 256 bits (4 * 64-bit words).
            // Layout: sectionIndex * columnWordCount + wordIndex.
            Span<ulong> sectionStoneFullBits = stackalloc ulong[sectionsYLocal * columnWordCount]; sectionStoneFullBits.Clear();
            Span<ulong> sectionSoilFullBits = stackalloc ulong[sectionsYLocal * columnWordCount]; sectionSoilFullBits.Clear();

            // Track min/max section index touched (for iteration bounds)
            int globalMinSectionY = int.MaxValue;
            int globalMaxSectionY = -1;

            // Helper: set section full coverage bit (stone or soil) for a column
            static void SetSectionFullBit(Span<ulong> arr, int columnWordCount, int sectionIndex, int columnIndex)
            {
                int word = columnIndex >> 6;
                int bit = columnIndex & 63;
                arr[sectionIndex * columnWordCount + word] |= 1UL << bit;
            }

            // Helper: set column presence in union bitset
            static void SetUnionBit(Span<ulong> bits, int columnIndex)
            {
                bits[columnIndex >> 6] |= 1UL << (columnIndex & 63);
            }

            // ------------------------------------------------------------------
            // Phase 1: Column pass (span derivation + direct per‑section full coverage bitset accumulation)
            //          (Stone & soil processed together per column; difference arrays removed.)
            // ------------------------------------------------------------------
            for (int z = 0; z < maxZ; z++)
            {
                int rowOffset = z * maxX;
                for (int x = 0; x < maxX; x++)
                {
                    int colIndex = rowOffset + x; // column linear index (z-major inside row)

                    // World span index from per-block column map: index = localX * maxZ + localZ
                    int spanIndex = x * maxZ + z;
                    if ((uint)spanIndex >= (uint)columnSpanMap.Length)
                        continue; // defensive

                    ref readonly BlockColumnProfile worldCol = ref columnSpanMap[spanIndex];

                    int surface = worldCol.Surface;
                    if (surface > maxSurface) maxSurface = surface; // track highest surface
                    if (candidateFullyBuried & (surface >= burialInvalidateThreshold))
                        candidateFullyBuried = false; // burial invalidation

                    // Extract world spans
                    int wStoneStart = worldCol.StoneStart;
                    int wStoneEnd = worldCol.StoneEnd;
                    int wSoilStart = worldCol.SoilStart;
                    int wSoilEnd = worldCol.SoilEnd;

                    bool hasStone = wStoneStart >= 0 && wStoneEnd >= wStoneStart && wStoneEnd >= chunkBaseY && wStoneStart <= topOfChunk;
                    bool hasSoil = wSoilStart >= 0 && wSoilEnd >= wSoilStart && wSoilEnd >= chunkBaseY && wSoilStart <= topOfChunk;

                    // Structured early exit for trivial whole-chunk candidates (skip further tests once both invalid)
                    if (allStoneFullCandidate)
                    {
                        if (!(hasStone && wStoneStart <= chunkBaseY && wStoneEnd >= topOfChunk))
                            allStoneFullCandidate = false;
                    }
                    if (allSoilFullCandidate)
                    {
                        bool stoneIntrudesSoil = hasStone && wStoneEnd >= chunkBaseY; // stone intrudes soil slab
                        if (!(hasSoil && wSoilStart <= chunkBaseY && wSoilEnd >= topOfChunk && !stoneIntrudesSoil))
                            allSoilFullCandidate = false;
                    }

                    // Early skip if neither material intersects chunk
                    if (!hasStone && !hasSoil) continue;

                    ref ColumnSpans spanRef = ref columns[colIndex];

                    // Clip stone span to chunk-local indices
                    short localStoneStart = 0, localStoneEnd = 0;
                    if (hasStone)
                    {
                        int cs = wStoneStart < chunkBaseY ? chunkBaseY : wStoneStart;
                        int ce = wStoneEnd > topOfChunk ? topOfChunk : wStoneEnd;
                        if (cs <= ce)
                        {
                            localStoneStart = (short)(cs - chunkBaseY);
                            localStoneEnd = (short)(ce - chunkBaseY);
                        }
                        else hasStone = false;
                    }

                    // Clip soil span
                    short localSoilStart = 0, localSoilEnd = 0;
                    if (hasSoil)
                    {
                        int cs = wSoilStart < chunkBaseY ? chunkBaseY : wSoilStart;
                        int ce = wSoilEnd > topOfChunk ? topOfChunk : wSoilEnd;
                        if (cs <= ce)
                        {
                            localSoilStart = (short)(cs - chunkBaseY);
                            localSoilEnd = (short)(ce - chunkBaseY);
                        }
                        else hasSoil = false;
                    }

                    if (!hasStone && !hasSoil) continue; // re-check after clipping

                    // Record spans + section indices for later emission (partial coverage)
                    if (hasStone)
                    {
                        spanRef.stoneStart = localStoneStart;
                        spanRef.stoneEnd = localStoneEnd;
                        short firstSec = (short)(localStoneStart >> SECTION_SHIFT);
                        short lastSec = (short)(localStoneEnd >> SECTION_SHIFT);
                        spanRef.stoneFirstSec = firstSec; spanRef.stoneLastSec = lastSec;
                        if (firstSec < globalMinSectionY) globalMinSectionY = firstSec;
                        if (lastSec > globalMaxSectionY) globalMaxSectionY = lastSec;
                    }
                    if (hasSoil)
                    {
                        spanRef.soilStart = localSoilStart;
                        spanRef.soilEnd = localSoilEnd;
                        short firstSec = (short)(localSoilStart >> SECTION_SHIFT);
                        short lastSec = (short)(localSoilEnd >> SECTION_SHIFT);
                        spanRef.soilFirstSec = firstSec; spanRef.soilLastSec = lastSec;
                        if (firstSec < globalMinSectionY) globalMinSectionY = firstSec;
                        if (lastSec > globalMaxSectionY) globalMaxSectionY = lastSec;
                    }

                    // Mark column material flags & union bit
                    byte matFlags = 0;
                    if (hasStone) matFlags |= 0x1;
                    if (hasSoil) matFlags |= 0x2;
                    columnMaterial[colIndex] = matFlags;
                    SetUnionBit(anySpanBits, colIndex);

                    // --- Per-section full coverage accumulation (stone then soil) ---
                    if (hasStone)
                    {
                        int sStart = spanRef.stoneStart;
                        int sEnd = spanRef.stoneEnd;
                        int firstSec = spanRef.stoneFirstSec;
                        int lastSec = spanRef.stoneLastSec;

                        bool firstFull = sStart <= sectionBaseYArr[firstSec] && sEnd >= sectionEndYArr[firstSec];
                        bool lastFull = sStart <= sectionBaseYArr[lastSec] && sEnd >= sectionEndYArr[lastSec];

                        int fullStart = firstSec;
                        int fullEnd = lastSec;
                        if (!firstFull) fullStart = firstSec + 1;
                        if (!lastFull) fullEnd = lastSec - 1;
                        if (firstSec == lastSec && !(firstFull && lastFull)) fullStart = fullEnd + 1; // no full section

                        int last   = spanRef.stoneLastSec;
                        if (fullStart <= fullEnd && hasSoil)
                        {
                            int soilStartSec = spanRef.soilStart >> SECTION_SHIFT;
                            if (soilStartSec <= fullEnd) fullEnd = soilStartSec - 1; // truncate where soil begins
                        }
                        if (fullStart <= fullEnd)
                        {
                            for (int sy = fullStart; sy <= fullEnd; sy++)
                                SetSectionFullBit(sectionStoneFullBits, columnWordCount, sy, colIndex);
                        }
                    }

                    if (hasSoil)
                    {
                        int soStart = spanRef.soilStart;
                        int soEnd = spanRef.soilEnd;
                        int firstSec = spanRef.soilFirstSec;
                        int lastSec = spanRef.soilLastSec;

                        bool firstFull = soStart <= sectionBaseYArr[firstSec] && soEnd >= sectionEndYArr[firstSec];
                        bool lastFull = soStart <= sectionBaseYArr[lastSec] && soEnd >= sectionEndYArr[lastSec];

                        int fullStart = firstSec;
                        int fullEnd = lastSec;
                        if (!firstFull) fullStart = firstSec + 1;
                        if (!lastFull) fullEnd = lastSec - 1;
                        if (firstSec == lastSec && !(firstFull && lastFull)) fullStart = fullEnd + 1;

                        if (fullStart <= fullEnd && (columnMaterial[colIndex] & 0x1) != 0) // stone present below
                        {
                            int stoneEndSec = spanRef.stoneEnd >> SECTION_SHIFT;
                            if (stoneEndSec >= fullStart) fullStart = stoneEndSec + 1; // shift start above stone end
                        }
                        if (fullStart <= fullEnd)
                        {
                            for (int sy = fullStart; sy <= fullEnd; sy++)
                                SetSectionFullBit(sectionSoilFullBits, columnWordCount, sy, colIndex);
                        }
                    }

                    // Early exit for whole-chunk uniform candidates: once both false no more checks needed
                    if (!allStoneFullCandidate && !allSoilFullCandidate)
                    {
                        // continue normal per-column processing but skip further candidate evaluation (already gated above)
                    }
                }
            }

            // ------------------------------------------------------------------
            // Phase 2: Section uniform & whole‑chunk trivial classification (bitset based)
            // ------------------------------------------------------------------
            if (chunkBaseY > maxSurface)
            {
                AllAirChunk = true;
                return;
            }

            if (allStoneFullCandidate && !allSoilFullCandidate)
            {
                AllStoneChunk = true; CreateUniformSections(StoneId);
                BuildAllBoundaryPlanesInitial();
                if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
                    SetFullyBuried();
                return;
            }
            else if (allSoilFullCandidate && !allStoneFullCandidate)
            {
                AllSoilChunk = true; CreateUniformSections(SoilId);
                BuildAllBoundaryPlanesInitial();
                if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
                    SetFullyBuried();
                return;
            }

            if (globalMaxSectionY >= 0)
            {
                if (globalMinSectionY < 0) globalMinSectionY = 0;
                if (globalMaxSectionY >= sectionsYLocal) globalMaxSectionY = sectionsYLocal - 1;
            }

            Span<bool> sectionUniformStone = stackalloc bool[sectionsYLocal]; sectionUniformStone.Clear();
            Span<bool> sectionUniformSoil = stackalloc bool[sectionsYLocal]; sectionUniformSoil.Clear();

            // Determine per-section uniformity from full coverage bitsets (stone precedence)
            for (int sy = globalMinSectionY; sy <= globalMaxSectionY; sy++)
            {
                bool stoneUniform = true;
                bool soilUniform = true;
                int baseIndex = sy * columnWordCount;
                for (int w = 0; w < columnWordCount; w++)
                {
                    ulong stoneWord = sectionStoneFullBits[baseIndex + w];
                    if (stoneWord != ulong.MaxValue) stoneUniform = false;
                    ulong soilWord = sectionSoilFullBits[baseIndex + w];
                    if (soilWord != ulong.MaxValue) soilUniform = false;
                    if ((!stoneUniform && !soilUniform)) break;
                }

                if (stoneUniform)
                {
                    sectionUniformStone[sy] = true;
                    for (int sx = 0; sx < sectionsX; sx++)
                        for (int sz = 0; sz < sectionsZ; sz++)
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
                    continue; // stone precedence
                }
                if (soilUniform)
                {
                    sectionUniformSoil[sy] = true;
                    for (int sx = 0; sx < sectionsX; sx++)
                        for (int sz = 0; sz < sectionsZ; sz++)
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

            // Build uniform skip bitset (1 => uniform) for fast emission skip
            int uniformWordCount = (sectionsYLocal + 63) >> 6;
            Span<ulong> uniformSkipBits = stackalloc ulong[uniformWordCount]; uniformSkipBits.Clear();
            for (int sy = 0; sy < sectionsYLocal; sy++)
            {
                if (sectionUniformStone[sy] || sectionUniformSoil[sy])
                {
                    int w = sy >> 6; int b = sy & 63; uniformSkipBits[w] |= 1UL << b;
                }
            }

            // ------------------------------------------------------------------
            // Phase 3: Section emission (sparse over columns with any material)
            // ------------------------------------------------------------------
            if (globalMaxSectionY >= 0)
            {
                // Local emission helper
                static void EmitSpan(ChunkSection secRef, int ox, int oz, int localStart, int localEnd, ushort id)
                {
                    if (localStart <= localEnd)
                        SectionUtils.GenerationAddRun(secRef, ox, oz, localStart, localEnd, id);
                }

                for (int w = 0; w < columnWordCount; w++)
                {
                    ulong word = anySpanBits[w];
                    while (word != 0)
                    {
                        int tz = BitOperations.TrailingZeroCount(word);
                        int colIndex = (w << 6) + tz;
                        word &= word - 1; // clear processed bit
                        if (colIndex >= columnCount) break;

                        byte mat = columnMaterial[colIndex];
                        bool hasStone = (mat & 0x1) != 0;
                        bool hasSoil = (mat & 0x2) != 0;
                        if (!hasStone && !hasSoil) continue;

                        ref ColumnSpans spanRef = ref columns[colIndex];

                        int x = colIndex % maxX;
                        int z = colIndex / maxX;
                        int sxIndex = x >> SECTION_SHIFT; int ox = x & sectionMask;
                        int szIndex = z >> SECTION_SHIFT; int oz = z & sectionMask;

                        if (hasStone)
                        {
                            int firstSec = spanRef.stoneFirstSec;
                            int lastSec = spanRef.stoneLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec > globalMaxSectionY) lastSec = globalMaxSectionY;
                            int ss = spanRef.stoneStart; int se = spanRef.stoneEnd;

                            for (int sy = firstSec; sy <= lastSec; sy++)
                            {
                                int wSkip = sy >> 6; int bSkip = sy & 63;
                                if ((uniformSkipBits[wSkip] & (1UL << bSkip)) != 0UL) continue; // uniform section
                                int sectionBase = sectionBaseYArr[sy];
                                int sectionEnd = sectionEndYArr[sy];
                                if (se < sectionBase || ss > sectionEnd) continue;
                                int clippedStart = ss < sectionBase ? sectionBase : ss;
                                int clippedEnd = se > sectionEnd ? sectionEnd : se;
                                var secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null) { secRef = new ChunkSection(); sections[sxIndex, sy, szIndex] = secRef; }
                                EmitSpan(secRef, ox, oz, clippedStart - sectionBase, clippedEnd - sectionBase, StoneId);
                            }
                        }

                        if (hasSoil)
                        {
                            int firstSec = spanRef.soilFirstSec;
                            int lastSec = spanRef.soilLastSec;
                            if (firstSec < globalMinSectionY) firstSec = globalMinSectionY;
                            if (lastSec > globalMaxSectionY) lastSec = globalMaxSectionY;
                            int sols = spanRef.soilStart; int sole = spanRef.soilEnd;

                            for (int sy = firstSec; sy <= lastSec; sy++)
                            {
                                int wSkip = sy >> 6; int bSkip = sy & 63;
                                if ((uniformSkipBits[wSkip] & (1UL << bSkip)) != 0UL) continue;
                                int sectionBase = sectionBaseYArr[sy];
                                int sectionEnd = sectionEndYArr[sy];
                                if (sole < sectionBase || sols > sectionEnd) continue;
                                int clippedStart = sols < sectionBase ? sectionBase : sols;
                                int clippedEnd = sole > sectionEnd ? sectionEnd : sole;
                                var secRef = sections[sxIndex, sy, szIndex];
                                if (secRef == null) { secRef = new ChunkSection(); sections[sxIndex, sy, szIndex] = secRef; }
                                EmitSpan(secRef, ox, oz, clippedStart - sectionBase, clippedEnd - sectionBase, SoilId);
                            }
                        }
                    }
                }
            }

            // ------------------------------------------------------------------
            // Phase 4: Whole‑chunk single block collapse (post section creation)
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
                            if (sec == null) continue; // air
                            if (sec.Kind != ChunkSection.RepresentationKind.Uniform || sec.UniformBlockId == ChunkSection.AIR)
                            {
                                allUniformSame = false; break;
                            }
                            if (uniformId == 0) uniformId = sec.UniformBlockId;
                            else if (uniformId != sec.UniformBlockId) { allUniformSame = false; break; }
                        }

                if (allUniformSame && uniformId != 0)
                {
                    AllOneBlockChunk = true;
                    AllOneBlockBlockId = uniformId;
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
            // Phase 5: Finalize non‑uniform sections & build boundary planes
            // ------------------------------------------------------------------
            if (!AllOneBlockChunk)
            {
                for (int sx = 0; sx < sectionsX; sx++)
                    for (int sy = 0; sy < sectionsY; sy++)
                        for (int sz = 0; sz < sectionsZ; sz++)
                        {
                            var sec = sections[sx, sy, sz];
                            if (sec == null) continue;
                            SectionUtils.GenerationFinalizeSection(sec);
                        }
            }

            BuildAllBoundaryPlanesInitial();

            // Confirm burial if faces fully solid and candidate still holds
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
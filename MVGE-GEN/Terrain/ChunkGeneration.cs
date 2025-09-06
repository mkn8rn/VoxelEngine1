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

        /// Generates initial voxel data for a non‑uniform, non‑air chunk.
        /// Uniform AllAir / AllStone / AllSoil slabs are now preclassified at the quadrant level and bypass this method.
        /// Phases:
        ///  1. Column pass: derive clipped stone / soil spans and accumulate per‑section full‑coverage bitsets.
        ///  2. Section uniform classification (stone precedence over soil) from full‑coverage bitsets.
        ///  3. Emit partial (non-uniform) spans sparsely. ( UPDATED: columns written directly with SectionUtils.DirectSetColumnRuns - avoids incremental GenerationAddRun state machine.)
        ///  4. Whole‑chunk single block collapse if all created sections are uniform with the same id.
        ///  5. Finalize non‑uniform sections & build boundary planes.
        internal void GenerateInitialChunkData(BlockColumnProfile[] columnSpanMap)
        {
            if (columnSpanMap == null)
                throw new InvalidOperationException("columnSpanMap must be provided for non-uniform chunk generation.");

            // ------------------------------------------------------------------
            // Basic chunk constants (hoisted to locals)
            // ------------------------------------------------------------------
            int maxX = dimX, maxY = dimY, maxZ = dimZ;
            int chunkBaseY = (int)position.Y;
            int topOfChunk = chunkBaseY + maxY - 1; // inclusive local top
            const ushort StoneId = (ushort)BaseBlockType.Stone;
            const ushort SoilId = (ushort)BaseBlockType.Soil;

            int sectionSize = ChunkSection.SECTION_SIZE; // expected 16
            int sectionVolume = ChunkSection.VOXELS_PER_SECTION; // expected 4096
            int sectionMask = sectionSize - 1;
            int sectionsYLocal = sectionsY;
            int columnCount = maxX * maxZ; // 256
            int columnWordCount = (columnCount + 63) >> 6; // 4
            int SECTION_SHIFT_LOCAL = SECTION_SHIFT; // hoist constant

            // Precompute section base/end Y for clipping
            Span<int> sectionBaseYArr = stackalloc int[sectionsYLocal];
            Span<int> sectionEndYArr = stackalloc int[sectionsYLocal];
            for (int sy = 0; sy < sectionsYLocal; sy++)
            {
                int baseY = sy << SECTION_SHIFT_LOCAL;
                sectionBaseYArr[sy] = baseY;
                sectionEndYArr[sy] = baseY | (sectionSize - 1);
            }

            // Column span storage
            Span<ColumnSpans> columns = stackalloc ColumnSpans[columnCount];
            Span<byte> columnMaterial = stackalloc byte[columnCount];
            columnMaterial.Clear(); // bit0=stone, bit1=soil

            Span<ulong> anySpanBits = stackalloc ulong[columnWordCount];
            anySpanBits.Clear();

            // Per-section full coverage bitsets (stone/soil)
            Span<ulong> sectionStoneFullBits = stackalloc ulong[sectionsYLocal * columnWordCount];
            sectionStoneFullBits.Clear();
            Span<ulong> sectionSoilFullBits = stackalloc ulong[sectionsYLocal * columnWordCount];
            sectionSoilFullBits.Clear();

            int globalMinSectionY = int.MaxValue;
            int globalMaxSectionY = -1;

            // Precompute per-column helpers to avoid division/mod in hot path of phase 3.
            Span<int> preX = stackalloc int[columnCount];
            Span<int> preZ = stackalloc int[columnCount];
            Span<int> preSx = stackalloc int[columnCount];
            Span<int> preSz = stackalloc int[columnCount];
            Span<int> preOx = stackalloc int[columnCount];
            Span<int> preOz = stackalloc int[columnCount];

            for (int z = 0, idx = 0; z < maxZ; z++)
            {
                for (int x = 0; x < maxX; x++, idx++)
                {
                    preX[idx] = x;
                    preZ[idx] = z;
                    preSx[idx] = x >> SECTION_SHIFT_LOCAL;
                    preOx[idx] = x & sectionMask;
                    preSz[idx] = z >> SECTION_SHIFT_LOCAL;
                    preOz[idx] = z & sectionMask;
                }
            }

            // Inline small utility for setting section full bit (avoid repeated multiplications externally)
            void SetSectionFullBitInline(Span<ulong> arr, int sectionIndex, int colIndex)
            {
                int baseIdx = sectionIndex * columnWordCount;
                int w = colIndex >> 6;
                int b = colIndex & 63;
                arr[baseIdx + w] |= 1UL << b;
            }
            void SetUnionBitInline(Span<ulong> bits, int columnIndex)
            {
                bits[columnIndex >> 6] |= 1UL << (columnIndex & 63);
            }

            // ------------------------------------------------------------------
            // Phase 1: Column pass – derive spans & accumulate full coverage bits
            // ------------------------------------------------------------------
            for (int z = 0, rowOffset = 0; z < maxZ; z++, rowOffset += maxX)
            {
                for (int x = 0; x < maxX; x++)
                {
                    int colIndex = rowOffset + x;
                    int spanIndex = x * maxZ + z; // per-world index layout
                    if ((uint)spanIndex >= (uint)columnSpanMap.Length) continue;

                    ref readonly BlockColumnProfile worldCol = ref columnSpanMap[spanIndex];

                    int wStoneStart = worldCol.StoneStart;
                    int wStoneEnd = worldCol.StoneEnd;
                    int wSoilStart = worldCol.SoilStart;
                    int wSoilEnd = worldCol.SoilEnd;

                    bool hasStone = wStoneStart >= 0 && wStoneEnd >= wStoneStart && wStoneEnd >= chunkBaseY && wStoneStart <= topOfChunk;
                    bool hasSoil = wSoilStart >= 0 && wSoilEnd >= wSoilStart && wSoilEnd >= chunkBaseY && wSoilStart <= topOfChunk;

                    if (!hasStone && !hasSoil) continue;

                    ref ColumnSpans spanRef = ref columns[colIndex];

                    // Clip spans to chunk
                    if (hasStone)
                    {
                        int cs = wStoneStart < chunkBaseY ? chunkBaseY : wStoneStart;
                        int ce = wStoneEnd > topOfChunk ? topOfChunk : wStoneEnd;
                        if (cs <= ce)
                        {
                            spanRef.stoneStart = (short)(cs - chunkBaseY);
                            spanRef.stoneEnd = (short)(ce - chunkBaseY);
                            spanRef.stoneFirstSec = (short)(spanRef.stoneStart >> SECTION_SHIFT_LOCAL);
                            spanRef.stoneLastSec = (short)(spanRef.stoneEnd >> SECTION_SHIFT_LOCAL);
                            if (spanRef.stoneFirstSec < globalMinSectionY) globalMinSectionY = spanRef.stoneFirstSec;
                            if (spanRef.stoneLastSec > globalMaxSectionY) globalMaxSectionY = spanRef.stoneLastSec;
                        }
                        else hasStone = false;
                    }
                    if (hasSoil)
                    {
                        int cs = wSoilStart < chunkBaseY ? chunkBaseY : wSoilStart;
                        int ce = wSoilEnd > topOfChunk ? topOfChunk : wSoilEnd;
                        if (cs <= ce)
                        {
                            spanRef.soilStart = (short)(cs - chunkBaseY);
                            spanRef.soilEnd = (short)(ce - chunkBaseY);
                            spanRef.soilFirstSec = (short)(spanRef.soilStart >> SECTION_SHIFT_LOCAL);
                            spanRef.soilLastSec = (short)(spanRef.soilEnd >> SECTION_SHIFT_LOCAL);
                            if (spanRef.soilFirstSec < globalMinSectionY) globalMinSectionY = spanRef.soilFirstSec;
                            if (spanRef.soilLastSec > globalMaxSectionY) globalMaxSectionY = spanRef.soilLastSec;
                        }
                        else hasSoil = false;
                    }
                    if (!hasStone && !hasSoil) continue;

                    // Mark column
                    byte matFlags = 0; if (hasStone) matFlags |= 0x1; if (hasSoil) matFlags |= 0x2;
                    columnMaterial[colIndex] = matFlags;
                    SetUnionBitInline(anySpanBits, colIndex);

                    // Stone full sections (truncate above soil start if soil present immediately above)
                    if (hasStone)
                    {
                        int sStart = spanRef.stoneStart;
                        int sEnd = spanRef.stoneEnd;
                        int firstSec = spanRef.stoneFirstSec;
                        int lastSec = spanRef.stoneLastSec;

                        bool firstFull = sStart <= sectionBaseYArr[firstSec] && sEnd >= sectionEndYArr[firstSec];
                        bool lastFull = sStart <= sectionBaseYArr[lastSec] && sEnd >= sectionEndYArr[lastSec];

                        int fullStart = firstFull ? firstSec : (firstSec + 1);
                        int fullEnd = lastFull ? lastSec : (lastSec - 1);

                        // special-case when only one section touched and it's not full
                        if (firstSec == lastSec && !(firstFull && lastFull))
                        {
                            fullStart = fullEnd + 1; // empty
                        }

                        if (fullStart <= fullEnd)
                        {
                            if (hasSoil)
                            {
                                int soilStartSec = spanRef.soilStart >> SECTION_SHIFT_LOCAL;
                                if (soilStartSec <= fullEnd) fullEnd = soilStartSec - 1;
                            }

                            for (int sy = fullStart; sy <= fullEnd; sy++)
                                SetSectionFullBitInline(sectionStoneFullBits, sy, colIndex);
                        }
                    }

                    // Soil full sections (clip below stone end)
                    if (hasSoil)
                    {
                        int soStart = spanRef.soilStart;
                        int soEnd = spanRef.soilEnd;
                        int firstSec = spanRef.soilFirstSec;
                        int lastSec = spanRef.soilLastSec;

                        bool firstFull = soStart <= sectionBaseYArr[firstSec] && soEnd >= sectionEndYArr[firstSec];
                        bool lastFull = soStart <= sectionBaseYArr[lastSec] && soEnd >= sectionEndYArr[lastSec];

                        int fullStart = firstFull ? firstSec : (firstSec + 1);
                        int fullEnd = lastFull ? lastSec : (lastSec - 1);

                        if (firstSec == lastSec && !(firstFull && lastFull))
                        {
                            fullStart = fullEnd + 1;
                        }

                        if (fullStart <= fullEnd && (columnMaterial[colIndex] & 0x1) != 0)
                        {
                            int stoneEndSec = spanRef.stoneEnd >> SECTION_SHIFT_LOCAL;
                            if (stoneEndSec >= fullStart) fullStart = stoneEndSec + 1;
                        }

                        if (fullStart <= fullEnd)
                        {
                            for (int sy = fullStart; sy <= fullEnd; sy++)
                                SetSectionFullBitInline(sectionSoilFullBits, sy, colIndex);
                        }
                    }
                }
            }

            if (globalMaxSectionY >= 0)
            {
                if (globalMinSectionY < 0) globalMinSectionY = 0;
                if (globalMaxSectionY >= sectionsYLocal) globalMaxSectionY = sectionsYLocal - 1;
            }

            // ------------------------------------------------------------------
            // Phase 2: Section uniform classification (stone precedence)
            // ------------------------------------------------------------------
            Span<bool> sectionUniformStone = stackalloc bool[sectionsYLocal];
            sectionUniformStone.Clear();
            Span<bool> sectionUniformSoil = stackalloc bool[sectionsYLocal];
            sectionUniformSoil.Clear();

            for (int sy = globalMinSectionY; sy <= globalMaxSectionY; sy++)
            {
                bool stoneUniform = true;
                bool soilUniform = true;
                int baseIndex = sy * columnWordCount;

                // small loop (word count small). compare to ulong.MaxValue
                for (int w = 0; w < columnWordCount; w++)
                {
                    if (sectionStoneFullBits[baseIndex + w] != ulong.MaxValue) stoneUniform = false;
                    if (sectionSoilFullBits[baseIndex + w] != ulong.MaxValue) soilUniform = false;
                    if (!stoneUniform && !soilUniform) break;
                }

                if (stoneUniform)
                {
                    sectionUniformStone[sy] = true;
                    // Fill entire layer with uniform stone shortcuts
                    for (int sx = 0; sx < sectionsX; sx++)
                    {
                        for (int sz = 0; sz < sectionsZ; sz++)
                        {
                            if (sections[sx, sy, sz] == null)
                                sections[sx, sy, sz] = new ChunkSection
                                {
                                    IsAllAir = false,
                                    Kind = ChunkSection.RepresentationKind.Uniform,
                                    UniformBlockId = StoneId,
                                    NonAirCount = sectionVolume,
                                    VoxelCount = sectionVolume,
                                    CompletelyFull = true,
                                    MetadataBuilt = true,
                                    HasBounds = true,
                                    MinLX = 0,
                                    MinLY = 0,
                                    MinLZ = 0,
                                    MaxLX = (byte)sectionMask,
                                    MaxLY = (byte)sectionMask,
                                    MaxLZ = (byte)sectionMask,
                                    StructuralDirty = false,
                                    IdMapDirty = false
                                };
                        }
                    }
                    continue; // stone precedence
                }

                if (soilUniform)
                {
                    sectionUniformSoil[sy] = true;
                    for (int sx = 0; sx < sectionsX; sx++)
                    {
                        for (int sz = 0; sz < sectionsZ; sz++)
                        {
                            if (sections[sx, sy, sz] == null)
                                sections[sx, sy, sz] = new ChunkSection
                                {
                                    IsAllAir = false,
                                    Kind = ChunkSection.RepresentationKind.Uniform,
                                    UniformBlockId = SoilId,
                                    NonAirCount = sectionVolume,
                                    VoxelCount = sectionVolume,
                                    CompletelyFull = true,
                                    MetadataBuilt = true,
                                    HasBounds = true,
                                    MinLX = 0,
                                    MinLY = 0,
                                    MinLZ = 0,
                                    MaxLX = (byte)sectionMask,
                                    MaxLY = (byte)sectionMask,
                                    MaxLZ = (byte)sectionMask,
                                    StructuralDirty = false,
                                    IdMapDirty = false
                                };
                        }
                    }
                }
            }

            int uniformWordCount = (sectionsYLocal + 63) >> 6;
            Span<ulong> uniformSkipBits = stackalloc ulong[uniformWordCount];
            uniformSkipBits.Clear();
            for (int sy = 0; sy < sectionsYLocal; sy++)
            {
                if (sectionUniformStone[sy] || sectionUniformSoil[sy])
                {
                    int w = sy >> 6;
                    uniformSkipBits[w] |= 1UL << (sy & 63);
                }
            }

            // ------------------------------------------------------------------
            // Phase 3: Emit partial spans (iterate only active columns via anySpanBits)
            // ------------------------------------------------------------------
            if (globalMaxSectionY >= 0)
            {
                for (int w = 0; w < columnWordCount; w++)
                {
                    ulong word = anySpanBits[w];
                    while (word != 0)
                    {
                        int tz = BitOperations.TrailingZeroCount(word);
                        int colIndex = (w << 6) + tz;
                        word &= word - 1;
                        if (colIndex >= columnCount) break;

                        byte mat = columnMaterial[colIndex];
                        bool hasStone = (mat & 0x1) != 0;
                        bool hasSoil = (mat & 0x2) != 0;
                        if (!hasStone && !hasSoil) continue;

                        ref ColumnSpans spanRef = ref columns[colIndex];

                        // use precomputed coordinates
                        int x = preX[colIndex];
                        int z = preZ[colIndex];
                        int sxIndex = preSx[colIndex];
                        int ox = preOx[colIndex];
                        int szIndex = preSz[colIndex];
                        int oz = preOz[colIndex];

                        // Determine section range touched by either span.
                        int earliestSec = int.MaxValue;
                        int latestSec = -1;
                        if (hasStone)
                        {
                            if (spanRef.stoneFirstSec < earliestSec) earliestSec = spanRef.stoneFirstSec;
                            if (spanRef.stoneLastSec > latestSec) latestSec = spanRef.stoneLastSec;
                        }
                        if (hasSoil)
                        {
                            if (spanRef.soilFirstSec < earliestSec) earliestSec = spanRef.soilFirstSec;
                            if (spanRef.soilLastSec > latestSec) latestSec = spanRef.soilLastSec;
                        }
                        if (earliestSec == int.MaxValue) continue;
                        if (earliestSec < globalMinSectionY) earliestSec = globalMinSectionY;
                        if (latestSec > globalMaxSectionY) latestSec = globalMaxSectionY;

                        int ci = (oz << SECTION_SHIFT_LOCAL) | ox; // local column index inside section

                        for (int sy = earliestSec; sy <= latestSec; sy++)
                        {
                            // skip uniform sections quickly using bit test
                            int wSkip = sy >> 6;
                            if ((uniformSkipBits[wSkip] & (1UL << (sy & 63))) != 0UL) continue;

                            int sectionBase = sectionBaseYArr[sy];
                            int sectionEnd = sectionEndYArr[sy];

                            // Compute local stone segment inside this section if present.
                            int localStoneStart = -1, localStoneEnd = -1;
                            if (hasStone)
                            {
                                int ss = spanRef.stoneStart;
                                int se = spanRef.stoneEnd;
                                if (!(se < sectionBase || ss > sectionEnd))
                                {
                                    int clippedStart = ss < sectionBase ? sectionBase : ss;
                                    int clippedEnd = se > sectionEnd ? sectionEnd : se;
                                    localStoneStart = clippedStart - sectionBase;
                                    localStoneEnd = clippedEnd - sectionBase;
                                }
                            }

                            // Compute local soil segment inside this section if present.
                            int localSoilStart = -1, localSoilEnd = -1;
                            if (hasSoil)
                            {
                                int sols = spanRef.soilStart;
                                int sole = spanRef.soilEnd;
                                if (!(sole < sectionBase || sols > sectionEnd))
                                {
                                    int clippedStart = sols < sectionBase ? sectionBase : sols;
                                    int clippedEnd = sole > sectionEnd ? sectionEnd : sole;
                                    localSoilStart = clippedStart - sectionBase;
                                    localSoilEnd = clippedEnd - sectionBase;
                                }
                            }

                            if (localStoneStart < 0 && localSoilStart < 0) continue;

                            var secRef = sections[sxIndex, sy, szIndex];
                            if (secRef == null)
                            {
                                secRef = new ChunkSection();
                                sections[sxIndex, sy, szIndex] = secRef;
                            }

                            var scratch = SectionUtils.EnsureScratch(secRef);

                            // Two runs (stone beneath soil) - preserve ordering/clamping
                            if (localStoneStart >= 0 && localSoilStart >= 0)
                            {
                                if (localStoneEnd < localSoilStart)
                                {
                                    SectionUtils.DirectSetColumnRuns(scratch, ci, StoneId, (byte)localStoneStart, (byte)localStoneEnd, SoilId, (byte)localSoilStart, (byte)localSoilEnd);
                                }
                                else if (localSoilEnd < localStoneStart)
                                {
                                    SectionUtils.DirectSetColumnRuns(scratch, ci, SoilId, (byte)localSoilStart, (byte)localSoilEnd, StoneId, (byte)localStoneStart, (byte)localStoneEnd);
                                }
                                else
                                {
                                    // Overlap: prefer soil above stone. Clamp lower run.
                                    if (localStoneStart <= localSoilStart)
                                    {
                                        int sEndAdj = Math.Min(localStoneEnd, localSoilStart - 1);
                                        if (sEndAdj >= localStoneStart)
                                            SectionUtils.DirectSetColumnRuns(scratch, ci, StoneId, (byte)localStoneStart, (byte)sEndAdj, SoilId, (byte)localSoilStart, (byte)localSoilEnd);
                                        else
                                            SectionUtils.DirectSetColumnRun1(scratch, ci, SoilId, (byte)localSoilStart, (byte)localSoilEnd);
                                    }
                                    else
                                    {
                                        int soilEndAdj = Math.Min(localSoilEnd, localStoneStart - 1);
                                        if (soilEndAdj >= localSoilStart)
                                            SectionUtils.DirectSetColumnRuns(scratch, ci, SoilId, (byte)localSoilStart, (byte)soilEndAdj, StoneId, (byte)localStoneStart, (byte)localStoneEnd);
                                        else
                                            SectionUtils.DirectSetColumnRun1(scratch, ci, StoneId, (byte)localStoneStart, (byte)localStoneEnd);
                                    }
                                }
                            }
                            else if (localStoneStart >= 0)
                            {
                                SectionUtils.DirectSetColumnRun1(scratch, ci, StoneId, (byte)localStoneStart, (byte)localStoneEnd);
                            }
                            else // only soil
                            {
                                SectionUtils.DirectSetColumnRun1(scratch, ci, SoilId, (byte)localSoilStart, (byte)localSoilEnd);
                            }
                        }
                    }
                }
            }

            // ------------------------------------------------------------------
            // Phase 4: Whole-chunk single block collapse
            // ------------------------------------------------------------------
            bool allUniformSame = true; ushort uniformId = 0;
            for (int sx = 0; sx < sectionsX && allUniformSame; sx++)
            {
                for (int sy = 0; sy < sectionsY && allUniformSame; sy++)
                {
                    for (int sz = 0; sz < sectionsZ && allUniformSame; sz++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null) continue;
                        if (sec.Kind != ChunkSection.RepresentationKind.Uniform || sec.UniformBlockId == ChunkSection.AIR)
                        {
                            allUniformSame = false;
                            break;
                        }
                        if (uniformId == 0) uniformId = sec.UniformBlockId;
                        else if (uniformId != sec.UniformBlockId)
                        {
                            allUniformSame = false;
                            break;
                        }
                    }
                }
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
                            {
                                sec.IdMapDirty = false; sec.StructuralDirty = false; sec.MetadataBuilt = true;
                            }
                        }
            }

            // ------------------------------------------------------------------
            // Phase 5: Finalize & boundary planes
            // ------------------------------------------------------------------
            for (int sx = 0; sx < sectionsX; sx++)
                for (int sy = 0; sy < sectionsY; sy++)
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz]; if (sec == null) continue; SectionUtils.GenerationFinalizeSection(sec);
                    }

            BuildAllBoundaryPlanesInitial();
            // Burial classification removed here; can be determined later using neighbor context if required.
        }
    }
}
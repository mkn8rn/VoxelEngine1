using MVGE_INF.Generation.Models;
using MVGE_INF.Models.Generation;
using MVGE_INF.Models.Terrain;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace MVGE_GEN.Utils
{
    internal partial class SectionUtils
    {
        /// Batched multi-rule application using precompiled rules and section-level bucket indices.
        /// Phase 1: fold rules over distinct ids (caches base types, builds change masks)
        /// Phase 2: apply only to columns that actually contain changing ids using per-id column bitsets.
        public static void SectionApplySimpleReplacementRules(
            ChunkSection sec,
            int sectionWorldY0,
            int sectionWorldY1,
            CompiledSimpleReplacementRule[] compiled,
            int[] bucketRuleIndices,
            Func<ushort, BaseBlockType> baseTypeGetter)
        {
            if (sec == null || bucketRuleIndices == null || bucketRuleIndices.Length == 0)
            {
                return;
            }

            // Fast uniform/packed single-id path if full cover only rules present
            bool allFullCover = true;
            foreach (var idx in bucketRuleIndices)
            {
                var r = compiled[idx];
                if (!r.FullyCoversSection(sectionWorldY0, sectionWorldY1))
                {
                    allFullCover = false;
                    break;
                }
            }

            if (allFullCover)
            {
                if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                {
                    ushort cur = sec.UniformBlockId;
                    BaseBlockType bt = baseTypeGetter(cur);
                    foreach (var ri in bucketRuleIndices)
                    {
                        var cr = compiled[ri];
                        if (cr.Matches(cur, bt))
                        {
                            if (cur != cr.ReplacementId)
                            {
                                sec.UniformBlockId = cr.ReplacementId;
                                sec.IdMapDirty = true; // only id changed
                            }
                            cur = sec.UniformBlockId;
                            bt = baseTypeGetter(cur);
                        }
                    }
                    return;
                }
                if (sec.Kind == ChunkSection.RepresentationKind.Packed &&
                    sec.Palette != null &&
                    sec.Palette.Count == 2 &&
                    sec.Palette[0] == ChunkSection.AIR)
                {
                    ushort cur = sec.Palette[1];
                    BaseBlockType bt = baseTypeGetter(cur);
                    ushort final = cur;
                    foreach (var ri in bucketRuleIndices)
                    {
                        var cr = compiled[ri];
                        if (cr.Matches(final, bt))
                        {
                            final = cr.ReplacementId;
                            bt = baseTypeGetter(final);
                        }
                    }
                    if (final != cur && final != ChunkSection.AIR)
                    {
                        sec.Palette[1] = final;
                        if (sec.PaletteLookup != null)
                        {
                            sec.PaletteLookup.Remove(cur);
                            sec.PaletteLookup[final] = 1;
                        }
                        sec.IdMapDirty = true; // occupancy unchanged
                    }
                    return;
                }
            }

            // Ensure scratch to access distinct ids / runs
            var scratch = GetScratch(sec);
            int d = scratch.DistinctCount;
            if (d == 0)
            {
                return; // nothing to affect
            }

            Span<ushort> originalSpan = stackalloc ushort[d];
            Span<ushort> finalIdSpan = stackalloc ushort[d];
            Span<ushort> sliceMaskSpan = stackalloc ushort[d]; // 0 => none
            Span<bool> isFullCoverSpan = stackalloc bool[d];
            Span<bool> changedSpan = stackalloc bool[d];
            Span<BaseBlockType> curBaseTypeSpan = stackalloc BaseBlockType[d];

            for (int i = 0; i < d; i++)
            {
                ushort id = scratch.Distinct[i];
                originalSpan[i] = id;
                finalIdSpan[i] = id;
                sliceMaskSpan[i] = 0;
                isFullCoverSpan[i] = false;
                changedSpan[i] = false;
                curBaseTypeSpan[i] = baseTypeGetter(id);
            }

            bool anyChange = false;
            bool anyPartial = false;

            // Phase 1: fold rules
            foreach (var ri in bucketRuleIndices)
            {
                var cr = compiled[ri];
                bool ruleFullCover = cr.FullyCoversSection(sectionWorldY0, sectionWorldY1);
                int sliceStart = Math.Max(cr.MinY, sectionWorldY0);
                int sliceEnd = Math.Min(cr.MaxY, sectionWorldY1);
                if (sliceEnd < sliceStart)
                {
                    continue; // safety
                }
                int localStart = sliceStart - sectionWorldY0; // 0..15
                int localEnd = sliceEnd - sectionWorldY0;     // 0..15
                ushort localMask = (ushort)(((1 << (localEnd - localStart + 1)) - 1) << localStart);

                for (int i = 0; i < d; i++)
                {
                    if (finalIdSpan[i] == ChunkSection.AIR)
                    {
                        continue; // skip air
                    }
                    BaseBlockType bt = curBaseTypeSpan[i];
                    if (!cr.Matches(finalIdSpan[i], bt))
                    {
                        continue;
                    }
                    if (ruleFullCover)
                    {
                        if (finalIdSpan[i] != cr.ReplacementId)
                        {
                            finalIdSpan[i] = cr.ReplacementId;
                            curBaseTypeSpan[i] = baseTypeGetter(finalIdSpan[i]);
                            changedSpan[i] = true;
                            anyChange = true;
                        }
                        isFullCoverSpan[i] = true; // overrides any partial slices
                        sliceMaskSpan[i] = 0;      // clear partial (full supersedes)
                    }
                    else
                    {
                        sliceMaskSpan[i] |= localMask;
                        if (cr.ReplacementId != finalIdSpan[i])
                        {
                            finalIdSpan[i] = cr.ReplacementId;
                            curBaseTypeSpan[i] = baseTypeGetter(finalIdSpan[i]);
                            changedSpan[i] = true;
                            anyChange = true;
                        }
                        anyPartial = true;
                    }
                }
            }

            if (!anyChange)
            {
                return; // no id changed at all
            }

            bool onlyFullCoverChanges = !anyPartial;

            // Fast exit: all changes are full cover, no partial slices
            bool allChangedFullCover = true;
            for (int i = 0; i < d; i++)
            {
                if (changedSpan[i] && !isFullCoverSpan[i])
                {
                    allChangedFullCover = false;
                    break;
                }
            }

            // Early multi-id uniformization or direct distinct mutate when all full-cover
            if (allChangedFullCover && onlyFullCoverChanges)
            {
                // Build a compact mapping of original -> final for changed ids
                Span<ushort> mapFrom = stackalloc ushort[d];
                Span<ushort> mapTo = stackalloc ushort[d];
                int mapCount = 0;
                for (int i = 0; i < d; i++)
                {
                    if (changedSpan[i])
                    {
                        mapFrom[mapCount] = originalSpan[i];
                        mapTo[mapCount] = finalIdSpan[i];
                        scratch.Distinct[i] = finalIdSpan[i]; // mutate distinct list in place
                        mapCount++;
                    }
                }

                // Rewrite column run ids so later finalize builds correct palette
                if (mapCount > 0)
                {
                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.GetWritableColumn(ci);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;
                        if (rc == 255)
                        {
                            var arr = col.Escalated;
                            if (arr != null)
                            {
                                for (int y = 0; y < S; y++)
                                {
                                    ushort id = arr[y];
                                    if (id == 0) continue;
                                    for (int m = 0; m < mapCount; m++) if (id == mapFrom[m]) { arr[y] = mapTo[m]; break; }
                                }
                            }
                        }
                        else
                        {
                            if (rc >= 1)
                            {
                                for (int m = 0; m < mapCount; m++) if (col.Id0 == mapFrom[m]) { col.Id0 = mapTo[m]; break; }
                            }
                            if (rc == 2)
                            {
                                for (int m = 0; m < mapCount; m++) if (col.Id1 == mapFrom[m]) { col.Id1 = mapTo[m]; break; }
                            }
                        }
                    }
                }

                scratch.DistinctDirty = false; // avoid rebuild later
                sec.IdMapDirty = true;          // ids changed but geometry did not
                return;
            }

            // Phase 2 (refactored): per-id iteration over membership bitsets.
            // Helper local static to attempt partial run split for prefix/suffix replacement on single-run columns.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool TrySplitSingleRun(ref ColumnData col, ushort targetId, ushort newId, ushort sliceMask)
            {
                // Preconditions: RunCount==1, col.Id0 == targetId, partial overlap already verified.
                int runStart = col.Y0Start;
                int runEnd = col.Y0End;
                int runLen = runEnd - runStart + 1;
                ushort runMask = (ushort)(((1 << runLen) - 1) << runStart);
                ushort overlap = (ushort)(runMask & sliceMask);
                if (overlap == 0 || overlap == runMask) return false; // not partial

                // Extract contiguous overlap boundaries
                int firstBit = BitOperations.TrailingZeroCount(overlap);
                int lastBit = 15 - BitOperations.LeadingZeroCount(overlap);
                bool prefix = firstBit == runStart && lastBit < runEnd; // bottom segment replaced
                bool suffix = firstBit > runStart && lastBit == runEnd; // top segment replaced
                if (!(prefix || suffix)) return false; // interior -> would produce 3 segments

                if (prefix)
                {
                    // New run becomes first (replacement), original becomes second
                    col.Id1 = col.Id0; col.Y1Start = (byte)(lastBit + 1); col.Y1End = (byte)runEnd;
                    col.Id0 = newId; col.Y0Start = (byte)runStart; col.Y0End = (byte)lastBit; col.RunCount = 2;
                }
                else // suffix
                {
                    // Keep original as first, add replacement as second
                    col.Id1 = newId; col.Y1Start = (byte)firstBit; col.Y1End = (byte)runEnd; col.RunCount = 2;
                    // shrink first run
                    col.Y0End = (byte)(firstBit - 1);
                }
                return true;
            }

            bool touchedAnyColumn = false;

            for (int di = 0; di < d; di++)
            {
                ushort originalId = originalSpan[di];
                ushort newId = finalIdSpan[di];
                bool fullCover = isFullCoverSpan[di];
                ushort sliceMask = sliceMaskSpan[di];
                bool changed = changedSpan[di];
                if (!changed && sliceMask == 0 && !fullCover) continue; // unaffected

                // Column membership bitsets
                ulong m0 = scratch.IdColumnBits[di, 0];
                ulong m1 = scratch.IdColumnBits[di, 1];
                ulong m2 = scratch.IdColumnBits[di, 2];
                ulong m3 = scratch.IdColumnBits[di, 3];

                if (m0 == 0 && m1 == 0 && m2 == 0 && m3 == 0)
                {
                    continue; // no columns recorded (safety)
                }

                void ProcessWord(ulong word, int wordIndex)
                {
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int ci = (wordIndex << 6) + bit;
                        ref var col = ref scratch.GetWritableColumn(ci);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;

                        if (rc == 255)
                        {
                            var arr = col.Escalated;
                            if (arr == null) continue;
                            if (fullCover)
                            {
                                for (int y = 0; y < S; y++) if (arr[y] == originalId) arr[y] = newId;
                            }
                            else
                            {
                                ushort mask = sliceMask;
                                while (mask != 0)
                                {
                                    int y = BitOperations.TrailingZeroCount(mask);
                                    mask &= (ushort)(mask - 1);
                                    if (arr[y] == originalId) arr[y] = newId;
                                }
                            }
                            touchedAnyColumn = true;
                            continue;
                        }

                        // compact columns (runs)
                        if (rc >= 1 && col.Id0 == originalId)
                        {
                            if (fullCover)
                            {
                                col.Id0 = newId;
                                touchedAnyColumn = true;
                            }
                            else if (sliceMask != 0)
                            {
                                int runStart = col.Y0Start;
                                int runEnd = col.Y0End;
                                int len = runEnd - runStart + 1;
                                ushort runMask = (ushort)(((1 << len) - 1) << runStart);
                                ushort overlap = (ushort)(runMask & sliceMask);
                                if (overlap != 0)
                                {
                                    if (overlap == runMask)
                                    {
                                        col.Id0 = newId;
                                        touchedAnyColumn = true;
                                    }
                                    else if (TrySplitSingleRun(ref col, originalId, newId, sliceMask))
                                    {
                                        touchedAnyColumn = true;
                                    }
                                    else
                                    {
                                        // escalate and patch only overlapping y
                                        var arr = col.Escalated ?? new ushort[S];
                                        if (col.Escalated == null)
                                        {
                                            for (int y = runStart; y <= runEnd; y++) arr[y] = originalId;
                                            if (rc == 2)
                                            {
                                                for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                                            }
                                            col.Escalated = arr;
                                            col.RunCount = 255;
                                        }
                                        ushort mask = overlap;
                                        while (mask != 0)
                                        {
                                            int y = BitOperations.TrailingZeroCount(mask);
                                            mask &= (ushort)(mask - 1);
                                            if (arr[y] == originalId) arr[y] = newId;
                                        }
                                        touchedAnyColumn = true;
                                    }
                                }
                            }
                        }
                        if (rc == 2 && col.Id1 == originalId)
                        {
                            if (fullCover)
                            {
                                col.Id1 = newId;
                                touchedAnyColumn = true;
                            }
                            else if (sliceMask != 0)
                            {
                                int runStart = col.Y1Start;
                                int runEnd = col.Y1End;
                                int len = runEnd - runStart + 1;
                                ushort runMask = (ushort)(((1 << len) - 1) << runStart);
                                ushort overlap = (ushort)(runMask & sliceMask);
                                if (overlap != 0)
                                {
                                    if (overlap == runMask)
                                    {
                                        col.Id1 = newId;
                                        touchedAnyColumn = true;
                                    }
                                    else
                                    {
                                        // escalate (simpler for second run)
                                        var arr = col.Escalated ?? new ushort[S];
                                        if (col.Escalated == null)
                                        {
                                            for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                                            for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                                            col.Escalated = arr;
                                            col.RunCount = 255;
                                        }
                                        ushort mask = overlap;
                                        while (mask != 0)
                                        {
                                            int y = BitOperations.TrailingZeroCount(mask);
                                            mask &= (ushort)(mask - 1);
                                            if (arr[y] == originalId) arr[y] = newId;
                                        }
                                        touchedAnyColumn = true;
                                    }
                                }
                            }
                        }
                    }
                }

                ProcessWord(m0, 0); ProcessWord(m1, 1); ProcessWord(m2, 2); ProcessWord(m3, 3);
            }

            if (!touchedAnyColumn && allChangedFullCover)
            {
                // Fallback: full scan because membership bits missing (very rare safety path)
                for (int ci = 0; ci < COLUMN_COUNT; ci++)
                {
                    ref var col = ref scratch.GetWritableColumn(ci);
                    byte rc = col.RunCount;
                    if (rc == 0) continue;
                    if (rc == 255)
                    {
                        var arr = col.Escalated; if (arr == null) continue;
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y];
                            for (int di = 0; di < d; di++)
                            {
                                if (isFullCoverSpan[di] && id == originalSpan[di] && finalIdSpan[di] != id)
                                {
                                    arr[y] = finalIdSpan[di];
                                    break;
                                }
                            }
                        }
                        continue;
                    }
                    if (rc >= 1)
                    {
                        for (int di = 0; di < d; di++) if (isFullCoverSpan[di] && col.Id0 == originalSpan[di]) { col.Id0 = finalIdSpan[di]; break; }
                    }
                    if (rc == 2)
                    {
                        for (int di = 0; di < d; di++) if (isFullCoverSpan[di] && col.Id1 == originalSpan[di]) { col.Id1 = finalIdSpan[di]; break; }
                    }
                }
            }

            scratch.DistinctDirty = true; // allow finalize to rebuild accurately (handles removed ids, new id insertion)
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RangesOverlap(int a0, int a1, int b0, int b1)
            => a0 <= b1 && b0 <= a1;

        // Popcount over occupancy array (uses hardware intrinsic when available).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PopCountBatch(ulong[] occ)
        {
            int total = 0;
            if (Popcnt.X64.IsSupported)
            {
                for (int i = 0; i < occ.Length; i++)
                {
                    total += (int)Popcnt.X64.PopCount(occ[i]);
                }
                return total;
            }
            for (int i = 0; i < occ.Length; i++)
            {
                total += BitOperations.PopCount(occ[i]);
            }
            return total;
        }

        // Length of intersection of two inclusive integer ranges (returns 0 when disjoint).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int OverlapLen(int a0, int a1, int b0, int b1)
        {
            int s = a0 > b0 ? a0 : b0;
            int e = a1 < b1 ? a1 : b1;
            int len = e - s + 1;
            return len > 0 ? len : 0;
        }
    }
}

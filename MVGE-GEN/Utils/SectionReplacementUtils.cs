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

            // Build union column bitset of affected columns using per-id membership
            ulong u0 = 0;
            ulong u1 = 0;
            ulong u2 = 0;
            ulong u3 = 0;
            for (int i = 0; i < d; i++)
            {
                if (!changedSpan[i] && sliceMaskSpan[i] == 0 && !isFullCoverSpan[i])
                {
                    continue; // unaffected
                }
                u0 |= scratch.IdColumnBits[i, 0];
                u1 |= scratch.IdColumnBits[i, 1];
                u2 |= scratch.IdColumnBits[i, 2];
                u3 |= scratch.IdColumnBits[i, 3];
            }

            ushort[] original = originalSpan.ToArray();
            ushort[] finalId = finalIdSpan.ToArray();
            ushort[] sliceMask = sliceMaskSpan.ToArray();
            bool[] isFullCover = isFullCoverSpan.ToArray();
            bool[] changed = changedSpan.ToArray();

            // Helper: map id -> distinct index (linear search small d)
            static int FindDistinctIndex(ushort[] arr, int count, ushort id)
            {
                for (int i = 0; i < count; i++)
                {
                    if (arr[i] == id)
                    {
                        return i;
                    }
                }
                return -1;
            }

            // Phase 2: apply
            void ProcessColumn(int ci)
            {
                ref var col = ref scratch.GetWritableColumn(ci);
                byte rc = col.RunCount;
                if (rc == 0)
                {
                    return;
                }

                if (rc == 255)
                {
                    var arr = col.Escalated;
                    if (arr == null)
                    {
                        return;
                    }
                    if (allChangedFullCover)
                    {
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y];
                            if (id == ChunkSection.AIR) continue;
                            int di = FindDistinctIndex(original, d, id);
                            if (di < 0) continue;
                            if (isFullCover[di] && finalId[di] != id)
                            {
                                arr[y] = finalId[di];
                            }
                        }
                        return;
                    }
                    for (int y = 0; y < S; y++)
                    {
                        ushort id = arr[y];
                        if (id == ChunkSection.AIR) continue;
                        int di = FindDistinctIndex(original, d, id);
                        if (di < 0) continue;
                        if (!changed[di] && sliceMask[di] == 0 && !isFullCover[di]) continue;
                        if (isFullCover[di])
                        {
                            if (finalId[di] != id)
                            {
                                arr[y] = finalId[di];
                            }
                        }
                        else if ((sliceMask[di] & (1 << y)) != 0 && finalId[di] != id)
                        {
                            arr[y] = finalId[di];
                        }
                    }
                    return;
                }

                // compact runs
                if (rc >= 1)
                {
                    int di0 = FindDistinctIndex(original, d, col.Id0);
                    if (di0 >= 0 && (changed[di0] || sliceMask[di0] != 0 || isFullCover[di0]))
                    {
                        if (isFullCover[di0])
                        {
                            if (finalId[di0] != col.Id0)
                            {
                                col.Id0 = finalId[di0];
                            }
                        }
                        else if (sliceMask[di0] != 0)
                        {
                            ushort runMask = (ushort)(((1 << (col.Y0End - col.Y0Start + 1)) - 1) << col.Y0Start);
                            ushort overlap = (ushort)(runMask & sliceMask[di0]);
                            if (overlap != 0)
                            {
                                if (overlap == runMask)
                                {
                                    if (finalId[di0] != col.Id0)
                                    {
                                        col.Id0 = finalId[di0];
                                    }
                                }
                                else
                                {
                                    var arr = col.Escalated ?? new ushort[S];
                                    if (col.Escalated == null)
                                    {
                                        for (int y = col.Y0Start; y <= col.Y0End; y++)
                                        {
                                            arr[y] = col.Id0;
                                        }
                                        if (rc == 2)
                                        {
                                            for (int y = col.Y1Start; y <= col.Y1End; y++)
                                            {
                                                arr[y] = col.Id1;
                                            }
                                        }
                                    }
                                    ushort mask = overlap;
                                    while (mask != 0)
                                    {
                                        int y = BitOperations.TrailingZeroCount(mask);
                                        mask &= (ushort)(mask - 1);
                                        if (finalId[di0] != arr[y])
                                        {
                                            arr[y] = finalId[di0];
                                        }
                                    }
                                    col.Escalated = arr;
                                    col.RunCount = 255;
                                    return;
                                }
                            }
                        }
                    }
                }

                if (rc == 2)
                {
                    int di1 = FindDistinctIndex(original, d, col.Id1);
                    if (di1 >= 0 && (changed[di1] || sliceMask[di1] != 0 || isFullCover[di1]))
                    {
                        if (isFullCover[di1])
                        {
                            if (finalId[di1] != col.Id1)
                            {
                                col.Id1 = finalId[di1];
                            }
                        }
                        else if (sliceMask[di1] != 0)
                        {
                            ushort runMask = (ushort)(((1 << (col.Y1End - col.Y1Start + 1)) - 1) << col.Y1Start);
                            ushort overlap = (ushort)(runMask & sliceMask[di1]);
                            if (overlap != 0)
                            {
                                if (overlap == runMask)
                                {
                                    if (finalId[di1] != col.Id1)
                                    {
                                        col.Id1 = finalId[di1];
                                    }
                                }
                                else
                                {
                                    var arr = col.Escalated ?? new ushort[S];
                                    if (col.Escalated == null)
                                    {
                                        for (int y = col.Y0Start; y <= col.Y0End; y++)
                                        {
                                            arr[y] = col.Id0;
                                        }
                                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                                        {
                                            arr[y] = col.Id1;
                                        }
                                    }
                                    ushort mask = overlap;
                                    while (mask != 0)
                                    {
                                        int y = BitOperations.TrailingZeroCount(mask);
                                        mask &= (ushort)(mask - 1);
                                        if (finalId[di1] != arr[y])
                                        {
                                            arr[y] = finalId[di1];
                                        }
                                    }
                                    col.Escalated = arr;
                                    col.RunCount = 255;
                                }
                            }
                        }
                    }
                }
            }

            if (allChangedFullCover && u0 == 0 && u1 == 0 && u2 == 0 && u3 == 0)
            {
                // Fallback: full scan but only swap ids (no partial logic) because we lack membership bits
                for (int ci = 0; ci < COLUMN_COUNT; ci++)
                {
                    ProcessColumn(ci);
                }
            }
            else
            {
                void IterateWord(ulong word, int wordIndex)
                {
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int ci = (wordIndex << 6) + bit; // 0..255
                        word &= word - 1;
                        ProcessColumn(ci);
                    }
                }
                IterateWord(u0, 0);
                IterateWord(u1, 1);
                IterateWord(u2, 2);
                IterateWord(u3, 3);
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

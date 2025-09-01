using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_INF.Models.Terrain;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using System.Buffers;
using MVGE_INF.Models.Generation;
using MVGE_INF.Generation.Models;

namespace MVGE_GEN.Utils
{
    internal static partial class SectionUtils
    {
        private const int S = ChunkSection.SECTION_SIZE;          // Section linear dimension (16)
        private const int AIR = ChunkSection.AIR;                  // Block id representing empty space
        private const int COLUMN_COUNT = S * S;                    // 256 vertical columns (z * S + x)
        private const int SPARSE_MASK_BUILD_MIN = 33;              // Threshold for building face masks in sparse form

        // -------------------------------------------------------------------------------------------------
        // Array pooling: reduces allocation pressure for frequently reused temporary structures.
        //   Occupancy: 4096 bits (64 * ulong) used when building face masks or packed/sparse data.
        //   Dense:     4096 ushorts storing full per‑voxel ids (8 KB) for DenseExpanded or fallback.
        // Objects are cleared before returning to pool to avoid leaking data across uses.
        // -------------------------------------------------------------------------------------------------
        private static readonly ConcurrentBag<ulong[]> _occupancyPool = new();
        private static readonly ConcurrentBag<ushort[]> _densePool = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] RentOccupancy() => _occupancyPool.TryTake(out var a) ? a : new ulong[64];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnOccupancy(ulong[] arr)
        {
            if (arr == null || arr.Length != 64) return;
            Array.Clear(arr);            // ensure no stale bits leak
            _occupancyPool.Add(arr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort[] RentDense() => _densePool.TryTake(out var a) ? a : new ushort[4096];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnDense(ushort[] arr)
        {
            if (arr == null || arr.Length != 4096) return;
            Array.Clear(arr);
            _densePool.Add(arr);
        }

        // -------------------------------------------------------------------------------------------------
        // Scratch pooling: Each in‑progress section maintains a SectionBuildScratch instance holding
        // per‑column run data + small distinct id tracking. Pooling avoids churn when many sections
        // are generated frame to frame.
        // -------------------------------------------------------------------------------------------------
        private static readonly ConcurrentBag<SectionBuildScratch> _scratchPool = new();

        private static SectionBuildScratch RentScratch()
        {
            if (_scratchPool.TryTake(out var sc))
            {
                sc.Reset();
                return sc;
            }
            var created = new SectionBuildScratch();
            created.Reset();
            return created;
        }

        private static void ReturnScratch(SectionBuildScratch sc)
        {
            if (sc == null) return;
            _scratchPool.Add(sc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SectionBuildScratch GetScratch(ChunkSection sec)
            => sec.BuildScratch as SectionBuildScratch ?? (SectionBuildScratch)(sec.BuildScratch = RentScratch());

        // -------------------------------------------------------------------------------------------------
        // TrackDistinct: Maintains up to 8 distinct non‑air block ids encountered while building.
        // If more are encountered than fit, we simply stop adding (cap). This heuristic still allows
        // rapid detection of single‑id or low‑variety sections without needing a larger structure.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void TrackDistinct(SectionBuildScratch sc, ushort id)
        {
            if (id == AIR) return;
            int dc = sc.DistinctCount;
            for (int i = 0; i < dc; i++)
            {
                if (sc.Distinct[i] == id) return; // already tracked
            }
            if (dc < sc.Distinct.Length)
            {
                sc.Distinct[dc] = id;
                sc.DistinctCount = dc + 1;
            }
            else
            {
                // Capacity reached: silently ignore further unique ids.
                sc.DistinctCount = dc;
            }
        }

        // -------------------------------------------------------------------------------------------------
        // ConvertUniformSectionToScratch:
        // Turns a finalized Uniform section back into scratch run form so that subsequent mutation
        // passes (e.g. replacements) can operate using the same incremental path as during initial
        // generation. All 256 columns become a single full‑height run with the uniform id.
        // -------------------------------------------------------------------------------------------------
        public static void ConvertUniformSectionToScratch(ChunkSection sec)
        {
            if (sec == null || sec.Kind != ChunkSection.RepresentationKind.Uniform) return;
            var scratch = GetScratch(sec);
            ushort id = sec.UniformBlockId;

            for (int z = 0; z < S; z++)
            {
                for (int x = 0; x < S; x++)
                {
                    int ci = z * S + x;
                    ref var col = ref scratch.GetWritableColumn(ci);
                    col.RunCount = 1;
                    col.Id0 = id;
                    col.Y0Start = 0;
                    col.Y0End = 15;
                    col.Escalated = null;      // ensure clean column
                    col.OccMask = 0xFFFF;      // every y filled
                    col.NonAir = 16;           // 16 voxels
                    col.AdjY = 15;             // 15 vertical adjacency pairs in a full stack
                }
            }

            TrackDistinct(scratch, id);
            scratch.AnyNonAir = true;

            // Reset the section so finalization re‑evaluates representation later.
            sec.Kind = ChunkSection.RepresentationKind.Empty;
            sec.MetadataBuilt = false;
            sec.UniformBlockId = 0;
            sec.CompletelyFull = false;
        }

        // -------------------------------------------------------------------------------------------------
        // AddRun:
        // Adds a contiguous vertical run [yStart, yEnd] with the provided blockId inside the column
        // specified by (localX, localZ). Columns start empty (RunCount = 0).
        // Representation transitions per column:
        //   0 -> 1 run -> possibly 2 runs -> escalated (RunCount == 255) when fragmentation appears
        // or partial overlaps make the compact form impractical.
        // While runs are added we incrementally update:
        //   - OccMask: 16 bits marking filled y positions
        //   - NonAir:  count of set bits
        //   - AdjY:    count of vertical adjacency pairs (set bits touching vertically)
        // This incremental metadata allows finalization to compute exposure and counts quickly.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddRun(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (blockId == AIR || yEnd < yStart) return; // reject empty or inverted intervals
            var scratch = GetScratch(sec);
            scratch.AnyNonAir = true;

            int ci = localZ * S + localX;
            ref var col = ref scratch.GetWritableColumn(ci);

            // Local helper: apply occupancy bits for the interval, adjusting counts and adjacency.
            static void ApplyMask(ref ColumnData c, int ys, int ye)
            {
                int len = ye - ys + 1;
                ushort segment = (ushort)(((1 << len) - 1) << ys); // bit field for the run
                ushort prev = c.OccMask;
                ushort added = (ushort)(segment & ~prev);          // newly set bits only
                if (added == 0)
                {
                    // No new occupancy -> adjacency unchanged (ids may be overwritten higher up but occupancy stable)
                    return;
                }
                c.OccMask = (ushort)(prev | segment);
                c.NonAir += (byte)BitOperations.PopCount((uint)added);

                // Incremental adjacency maintenance.
                // Only pairs that lie completely inside [ys-1, ye] can change (due to new bits).
                int regionStart = ys > 0 ? ys - 1 : ys;
                int regionEnd = ye < 15 ? ye : 14; // last y that can start a pair y,y+1
                if (regionStart <= regionEnd)
                {
                    int regionBitsLen = regionEnd - regionStart + 2; // include y+1 of last pair
                    ushort regionMask = (ushort)(((1 << regionBitsLen) - 1) << regionStart);

                    ushort prevRegionBits = (ushort)(prev & regionMask);
                    ushort newRegionBits = (ushort)(c.OccMask & regionMask);
                    int prevAdjRegion = BitOperations.PopCount((uint)(prevRegionBits & (prevRegionBits << 1)));
                    int newAdjRegion = BitOperations.PopCount((uint)(newRegionBits & (newRegionBits << 1)));
                    int delta = newAdjRegion - prevAdjRegion;
                    if (delta > 0)
                    {
                        c.AdjY = (byte)(c.AdjY + delta);
                    }
                    // (delta should never be negative because occupancy only gains bits.)
                }
                else
                {
                    // Fallback (should rarely hit) – maintain full adjacency (original method):
                    ushort m = c.OccMask;
                    c.AdjY = (byte)BitOperations.PopCount((uint)(m & (m << 1)));
                }
            }

            // Escalated column: directly write per‑voxel values.
            if (col.RunCount == 255)
            {
                var arr = col.Escalated ??= new ushort[S];
                for (int y = yStart; y <= yEnd; y++)
                {
                    // Occupancy counters rely on mask differences, so we just overwrite; changes
                    // in id do not affect NonAir if the cell was already non‑air.
                    arr[y] = blockId;
                }
                ApplyMask(ref col, yStart, yEnd);
                TrackDistinct(scratch, blockId);
                return;
            }

            // Empty column -> first run.
            if (col.RunCount == 0)
            {
                col.Id0 = blockId;
                col.Y0Start = (byte)yStart;
                col.Y0End = (byte)yEnd;
                col.RunCount = 1;
                col.Escalated = null;
                ApplyMask(ref col, yStart, yEnd);
                TrackDistinct(scratch, blockId);
                return;
            }

            // Single run column: attempt extension or creation of a second run.
            if (col.RunCount == 1)
            {
                // Append contiguous run of same id at the top.
                if (blockId == col.Id0 && yStart == col.Y0End + 1)
                {
                    col.Y0End = (byte)yEnd;
                    ApplyMask(ref col, yStart, yEnd);
                    return;
                }
                // Non‑overlapping new run higher than first run.
                if (yStart > col.Y0End)
                {
                    col.Id1 = blockId;
                    col.Y1Start = (byte)yStart;
                    col.Y1End = (byte)yEnd;
                    col.RunCount = 2;
                    ApplyMask(ref col, yStart, yEnd);
                    TrackDistinct(scratch, blockId);
                    return;
                }
            }

            // Two run column: attempt to extend second run only.
            if (col.RunCount == 2)
            {
                if (blockId == col.Id1 && yStart == col.Y1End + 1)
                {
                    col.Y1End = (byte)yEnd;
                    ApplyMask(ref col, yStart, yEnd);
                    return;
                }
            }

            // Escalation path: runs overlap or fragmentation appeared.
            scratch.AnyEscalated = true;
            var full = col.Escalated ?? new ushort[S];

            // Keep previous occupancy metadata for potential incremental update.
            ushort prevMaskAll = col.OccMask;
            byte prevNonAirAll = col.NonAir;
            byte prevAdjAll = col.AdjY;

            // Replay existing compact runs into the per‑voxel buffer (only once per escalation).
            if (col.RunCount != 255)
            {
                if (col.RunCount >= 1)
                {
                    for (int y = col.Y0Start; y <= col.Y0End; y++) full[y] = col.Id0;
                    if (col.RunCount == 2)
                        for (int y = col.Y1Start; y <= col.Y1End; y++) full[y] = col.Id1;
                }
            }

            // Apply new interval (may overlap existing writes).
            for (int y = yStart; y <= yEnd; y++) full[y] = blockId;
            col.Escalated = full;
            col.RunCount = 255; // sentinel for escalated

            int newLen = yEnd - yStart + 1;
            bool canIncremental = prevMaskAll != 0 && newLen < 16; // if not full rewrite
            if (canIncremental)
            {
                // Compute added bits only from the explicitly provided interval (safe upper bound; overlap removed next).
                ushort segment = (ushort)(((1 << newLen) - 1) << yStart);
                ushort added = (ushort)(segment & ~prevMaskAll);
                if (added == 0)
                {
                    // Only overwrote ids, occupancy unchanged.
                    col.OccMask = prevMaskAll;
                    col.NonAir = prevNonAirAll;
                    col.AdjY = prevAdjAll;
                }
                else
                {
                    ushort newMask = (ushort)(prevMaskAll | segment);
                    byte newNonAir = (byte)(prevNonAirAll + BitOperations.PopCount((uint)added));

                    // Adjacency delta localized to [yStart-1, yEnd].
                    int regionStart = yStart > 0 ? yStart - 1 : yStart;
                    int regionEnd = yEnd < 15 ? yEnd : 14;
                    if (regionStart <= regionEnd)
                    {
                        int regionBitsLen = regionEnd - regionStart + 2; // include y+1 for last pair
                        ushort regionMask = (ushort)(((1 << regionBitsLen) - 1) << regionStart);
                        ushort prevRegionBits = (ushort)(prevMaskAll & regionMask);
                        ushort newRegionBits = (ushort)(newMask & regionMask);
                        int prevAdjRegion = BitOperations.PopCount((uint)(prevRegionBits & (prevRegionBits << 1)));
                        int newAdjRegion = BitOperations.PopCount((uint)(newRegionBits & (newRegionBits << 1)));
                        int delta = newAdjRegion - prevAdjRegion;
                        col.AdjY = (byte)(prevAdjAll + (delta > 0 ? delta : 0));
                    }
                    else
                    {
                        col.AdjY = prevAdjAll;
                    }
                    col.OccMask = newMask;
                    col.NonAir = newNonAir;
                }
            }
            else
            {
                // Rebuild occupancy + adjacency from escalated data (16 iterations only).
                ushort mask = 0;
                byte nonAir = 0;
                byte adj = 0;
                ushort prevBit = 0;
                for (int y = 0; y < S; y++)
                {
                    if (full[y] != AIR)
                    {
                        mask |= (ushort)(1 << y);
                        nonAir++;
                        if (prevBit == 1) adj++;
                        prevBit = 1;
                    }
                    else
                    {
                        prevBit = 0;
                    }
                }
                col.OccMask = mask;
                col.NonAir = nonAir;
                col.AdjY = adj;
            }

            TrackDistinct(scratch, blockId);
        }

        /// Batched multi-rule application using precompiled rules and section-level bucket indices.
        /// For each distinct id we compute the final replacement id plus a partial y mask (16-bit) for slice edits.
        /// Falls back to legacy per-rule path when scratch absent and a simple fast path cannot be used.
        public static void BatchApplyCompiledSimpleReplacementRules(
            ChunkSection sec,
            int sectionWorldY0,
            int sectionWorldY1,
            CompiledSimpleReplacementRule[] compiled,
            int[] bucketRuleIndices,
            Func<ushort, BaseBlockType> baseTypeGetter)
        {
            if (sec == null || bucketRuleIndices == null || bucketRuleIndices.Length == 0) return;
            // Fast uniform/packed single-id path if full cover only rules present
            bool allFullCover = true;
            foreach (var idx in bucketRuleIndices)
            {
                var r = compiled[idx];
                if (!r.FullyCoversSection(sectionWorldY0, sectionWorldY1)) { allFullCover = false; break; }
            }
            if (allFullCover)
            {
                // examine current representation for single id fast swap
                if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                {
                    ushort cur = sec.UniformBlockId; BaseBlockType bt = baseTypeGetter(cur);
                    foreach (var ri in bucketRuleIndices)
                    {
                        var cr = compiled[ri];
                        if (cr.Matches(cur, bt))
                        {
                            if (cur != cr.ReplacementId) sec.UniformBlockId = cr.ReplacementId;
                            // continue allowing later rules to override
                            cur = sec.UniformBlockId; bt = baseTypeGetter(cur);
                        }
                    }
                    return;
                }
                if (sec.Kind == ChunkSection.RepresentationKind.Packed && sec.Palette != null && sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR)
                {
                    ushort cur = sec.Palette[1]; BaseBlockType bt = baseTypeGetter(cur);
                    ushort final = cur;
                    foreach (var ri in bucketRuleIndices)
                    {
                        var cr = compiled[ri];
                        if (cr.Matches(final, bt)) { final = cr.ReplacementId; bt = baseTypeGetter(final); }
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
            if (d == 0) return; // nothing to affect

            Span<ushort> original = stackalloc ushort[d];
            Span<ushort> finalId = stackalloc ushort[d];
            Span<ushort> partialMask = stackalloc ushort[d]; // 0 => none, else bits inside column y (only used when not full cover)
            for (int i=0;i<d;i++) { original[i] = scratch.Distinct[i]; finalId[i] = original[i]; partialMask[i] = 0; }

            bool anyChange = false;
            bool anyPartial = false;

            // Fold rules in order
            foreach (var ri in bucketRuleIndices)
            {
                var cr = compiled[ri];
                // vertical overlap already guaranteed by bucket; compute local mask
                bool fullCover = cr.FullyCoversSection(sectionWorldY0, sectionWorldY1);
                int sliceStart = Math.Max(cr.MinY, sectionWorldY0);
                int sliceEnd = Math.Min(cr.MaxY, sectionWorldY1);
                if (sliceEnd < sliceStart) continue; // safety
                int localStart = sliceStart - sectionWorldY0; // 0..15
                int localEnd = sliceEnd - sectionWorldY0;     // 0..15
                ushort sliceMask = (ushort)(((1 << (localEnd - localStart + 1)) - 1) << localStart);

                for (int i=0;i<d;i++)
                {
                    ushort cur = finalId[i]; if (cur == ChunkSection.AIR) continue;
                    var bt = baseTypeGetter(cur);
                    if (!cr.Matches(cur, bt)) continue;
                    if (fullCover)
                    {
                        finalId[i] = cr.ReplacementId;
                        partialMask[i] = 0xFFFF; // flag as full
                        anyChange |= finalId[i] != original[i];
                    }
                    else
                    {
                        partialMask[i] |= sliceMask;
                        anyPartial = true;
                        // For partial we treat only bits of mask as replaced; runs may escalate later.
                        if (cr.ReplacementId != cur) anyChange = true;
                        // Store target id for replaced bits: we carry per-id final target; outside mask keep previous id.
                        finalId[i] = cr.ReplacementId;
                    }
                }
            }

            if (!anyChange) return;

            // Apply changes single pass columns
            for (int z=0; z<S; z++)
            {
                int zBase = z * S;
                for (int x=0; x<S; x++)
                {
                    ref var col = ref scratch.GetWritableColumn(zBase + x);
                    byte rc = col.RunCount; if (rc == 0) continue;

                    if (rc == 255)
                    {
                        var arr = col.Escalated; if (arr == null) continue;
                        for (int y=0;y<S;y++)
                        {
                            ushort id = arr[y]; if (id == ChunkSection.AIR) continue;
                            // map
                            for (int i=0;i<d;i++) if (original[i]==id)
                            {
                                if (partialMask[i]==0) { if (finalId[i]!=id) arr[y]=finalId[i]; }
                                else if ((partialMask[i] & (1<<y))!=0) { arr[y]=finalId[i]; }
                                break;
                            }
                        }
                        continue;
                    }

                    // compact runs
                    if (rc >= 1)
                    {
                        ushort id0 = col.Id0;
                        for (int i=0;i<d;i++) if (original[i]==id0)
                        {
                            if (partialMask[i]==0xFFFF) { col.Id0 = finalId[i]; }
                            else if (partialMask[i]!=0) // partial edit
                            {
                                ushort runMask = (ushort)(((1 << (col.Y0End - col.Y0Start + 1)) -1) << col.Y0Start);
                                ushort overlap = (ushort)(runMask & partialMask[i]);
                                if (overlap!=0)
                                {
                                    if (overlap==runMask)
                                    {
                                        col.Id0 = finalId[i];
                                    }
                                    else
                                    {
                                        // escalate and rewrite targeted y only
                                        var arr = col.Escalated ?? new ushort[S];
                                        if (col.Escalated==null)
                                        {
                                            for (int y=col.Y0Start;y<=col.Y0End;y++) arr[y]=col.Id0;
                                            if (rc==2) for (int y=col.Y1Start;y<=col.Y1End;y++) arr[y]=col.Id1;
                                        }
                                        ushort mask = overlap;
                                        while(mask!=0){int y = BitOperations.TrailingZeroCount(mask); mask &= (ushort)(mask-1); arr[y]=finalId[i]; }
                                        col.Escalated = arr; col.RunCount = 255; break; // column escalated; skip second run logic via continue outer
                                    }
                                }
                            }
                            break;
                        }
                        if (col.RunCount==255) continue; // escalated inside first run logic
                    }
                    if (rc==2)
                    {
                        ushort id1 = col.Id1;
                        for (int i=0;i<d;i++) if (original[i]==id1)
                        {
                            if (partialMask[i]==0xFFFF) { col.Id1 = finalId[i]; }
                            else if (partialMask[i]!=0)
                            {
                                ushort runMask = (ushort)(((1 << (col.Y1End - col.Y1Start + 1)) -1) << col.Y1Start);
                                ushort overlap = (ushort)(runMask & partialMask[i]);
                                if (overlap!=0)
                                {
                                    if (overlap==runMask)
                                    {
                                        col.Id1 = finalId[i];
                                    }
                                    else
                                    {
                                        var arr = col.Escalated ?? new ushort[S];
                                        if (col.Escalated==null)
                                        {
                                            for (int y=col.Y0Start;y<=col.Y0End;y++) arr[y]=col.Id0;
                                            for (int y=col.Y1Start;y<=col.Y1End;y++) arr[y]=col.Id1;
                                        }
                                        ushort mask = overlap;
                                        while(mask!=0){int y = BitOperations.TrailingZeroCount(mask); mask &= (ushort)(mask-1); arr[y]=finalId[i]; }
                                        col.Escalated = arr; col.RunCount = 255;
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
            }

            // Rebuild distinct list after batch (cheap)
            scratch.DistinctDirty = true; // allow finalize to rebuild accurately (handles removed ids, new id insertion)
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RangesOverlap(int a0, int a1, int b0, int b1) => a0 <= b1 && b0 <= a1;

        // Popcount over occupancy array (uses hardware intrinsic when available).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PopCountBatch(ulong[] occ)
        {
            int total = 0;
            if (Popcnt.X64.IsSupported)
            {
                for (int i = 0; i < occ.Length; i++)
                    total += (int)Popcnt.X64.PopCount(occ[i]);
                return total;
            }
            for (int i = 0; i < occ.Length; i++)
                total += BitOperations.PopCount(occ[i]);
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

        // SetRunBits:
        // Marks [yStart, yEnd] for a specific column in a 4096‑bit occupancy array (64 * ulong).
        // Each column consists of 16 consecutive bits (one per y) laid out by linear index (ci << 4).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetRunBits(ulong[] occ, int columnIndex, int yStart, int yEnd)
        {
            int baseLi = (columnIndex << 4) + yStart;  // first linear index in run
            int endLi = (columnIndex << 4) + yEnd;     // last linear index in run
            int startWord = baseLi >> 6;
            int endWord = endLi >> 6;
            int startBit = baseLi & 63;
            int len = endLi - baseLi + 1;

            if (startWord == endWord)
            {
                // Entire run resides inside a single 64‑bit word.
                occ[startWord] |= ((1UL << len) - 1) << startBit;
            }
            else
            {
                // Run crosses a 64‑bit boundary (at most once because length <= 16).
                int firstLen = 64 - startBit;
                occ[startWord] |= ((1UL << firstLen) - 1) << startBit;
                int remaining = len - firstLen;
                occ[endWord] |= (1UL << remaining) - 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteColumnMask(ulong[] occ, int columnIndex, ushort mask)
        {
            if (mask == 0) return;
            int baseLi = columnIndex << 4; // 16 voxels per column
            int w0 = baseLi >> 6;          // starting 64-bit word index
            int bit = baseLi & 63;         // starting bit within word
            ulong shifted = (ulong)mask << bit;
            occ[w0] |= shifted;
            int spill = bit + 16 - 64;    // how many bits overflow into next word (if any)
            if (spill > 0)
            {
                occ[w0 + 1] |= (ulong)mask >> (16 - spill);
            }
        }
    }
}

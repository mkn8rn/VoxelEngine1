using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GEN.Models;
using MVGE_INF.Models.Terrain;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using System.Buffers;

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
                if (added == 0) return;                            // nothing new
                c.OccMask = (ushort)(prev | segment);
                c.NonAir += (byte)BitOperations.PopCount((uint)added);

                ushort m = c.OccMask;
                c.AdjY = (byte)BitOperations.PopCount((uint)(m & (m << 1))); // vertical adjacency count
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

            // Apply new interval.
            for (int y = yStart; y <= yEnd; y++) full[y] = blockId;
            col.Escalated = full;
            col.RunCount = 255; // sentinel for escalated

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
            TrackDistinct(scratch, blockId);
        }

        // -------------------------------------------------------------------------------------------------
        // ApplyReplacement:
        // Replaces block ids satisfying a predicate within an inclusive vertical slice [lyStart, lyEnd].
        // Strategy:
        //   1. Inspect the small distinct id list to determine which ids are targeted. If none, exit.
        //   2. For full vertical cover (slice spans the entire 0..15 range) we can modify runs directly
        //      without escalation unless partial overlaps force per‑voxel edits.
        //   3. For partial slices, only escalate columns that require partial run edits; full run
        //      replacement is done in place.
        // DistinctDirty is set when ids change so finalization can rebuild the distinct list.
        // -------------------------------------------------------------------------------------------------
        public static void ApplyReplacement(
            ChunkSection sec,
            int lyStart,
            int lyEnd,
            bool fullCover,
            Func<ushort, BaseBlockType> baseTypeGetter,
            Func<ushort, BaseBlockType, bool> predicate,
            ushort replacementId)
        {
            if (sec == null) return;

            // Early fast-paths when there is no scratch attached: avoid creating scratch and unnecessary escalations.
            var existingScratch = sec.BuildScratch as SectionBuildScratch;
            if (existingScratch == null)
            {
                // If the replacement covers the full vertical range of the section, we can do some safe in-place substitutions
                // without converting to scratch.
                if (fullCover)
                {
                    // Uniform section -> change the uniform block id directly if predicate matches.
                    if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                    {
                        ushort currentId = sec.UniformBlockId;
                        if (currentId != replacementId)
                        {
                            var bt = baseTypeGetter(currentId);
                            if (predicate(currentId, bt))
                            {
                                sec.UniformBlockId = replacementId;
                                // metadata (counts, exposure) unaffected by id change
                                // keep MetadataBuilt true
                                return;
                            }
                        }
                        return;
                    }

                    // Packed section with palette [AIR, id] (single non-air id). We can change the id in the palette
                    // without modifying occupancy bitsets or allocating scratch, provided replacementId is non-air.
                    if (sec.Kind == ChunkSection.RepresentationKind.Packed && sec.Palette != null && sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR)
                    {
                        ushort oldId = sec.Palette[1];
                        if (oldId != replacementId)
                        {
                            // Only handle non-air replacement here; replacing to AIR would require clearing occupancy.
                            if (replacementId != ChunkSection.AIR)
                            {
                                var bt = baseTypeGetter(oldId);
                                if (predicate(oldId, bt))
                                {
                                    // Update palette and lookup safely.
                                    sec.Palette[1] = replacementId;
                                    if (sec.PaletteLookup != null)
                                    {
                                        // Remove old mapping if present, and set new mapping to index 1.
                                        if (sec.PaletteLookup.ContainsKey(oldId)) sec.PaletteLookup.Remove(oldId);
                                        sec.PaletteLookup[replacementId] = 1;
                                    }
                                    // Other metadata (OccupancyBits, NonAirCount, InternalExposure, Face masks) remain valid.
                                    return;
                                }
                            }
                        }
                        return;
                    }
                }
            }

            // Fallback: use scratch-based replacement logic.
            var scratch = GetScratch(sec);
            int distinctCount = scratch.DistinctCount;
            if (distinctCount == 0) return;

            Span<ushort> ids = stackalloc ushort[distinctCount];
            Span<bool> targeted = stackalloc bool[distinctCount];
            bool anyTarget = false;

            // Build targeted map from distinct list.
            for (int i = 0; i < distinctCount; i++)
            {
                ushort id = scratch.Distinct[i];
                ids[i] = id;
                if (id == replacementId)
                {
                    targeted[i] = false; // avoid self replacement
                    continue;
                }
                var bt = baseTypeGetter(id);
                bool shouldReplace = predicate(id, bt);
                targeted[i] = shouldReplace;
                if (shouldReplace) anyTarget = true;
            }
            if (!anyTarget) return; // nothing qualifies

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IdMatches(ushort id, Span<ushort> idsLocal, Span<bool> targetedLocal)
            {
                for (int i = 0; i < idsLocal.Length; i++)
                    if (idsLocal[i] == id) return targetedLocal[i];
                return false;
            }

            bool anyChange = false;

            // Full vertical cover: attempt in‑place run id substitution.
            if (fullCover)
            {
                for (int z = 0; z < S; z++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        ref var col = ref scratch.GetWritableColumn(z * S + x);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;

                        if (rc == 255)
                        {
                            var arr = col.Escalated;
                            for (int y = 0; y < S; y++)
                            {
                                ushort id = arr[y];
                                if (id == AIR || id == replacementId) continue;
                                if (IdMatches(id, ids, targeted))
                                {
                                    arr[y] = replacementId;
                                    anyChange = true;
                                }
                            }
                            continue;
                        }
                        if (rc >= 1 && col.Id0 != replacementId && IdMatches(col.Id0, ids, targeted))
                        {
                            col.Id0 = replacementId;
                            anyChange = true;
                        }
                        if (rc == 2 && col.Id1 != replacementId && IdMatches(col.Id1, ids, targeted))
                        {
                            col.Id1 = replacementId;
                            anyChange = true;
                        }
                    }
                }
                if (anyChange) scratch.DistinctDirty = true;
                return;
            }

            // Partial vertical slice: only escalate columns that require editing a subset of a run.
            for (int z = 0; z < S; z++)
            {
                for (int x = 0; x < S; x++)
                {
                    ref var col = ref scratch.GetWritableColumn(z * S + x);
                    byte rc = col.RunCount;
                    if (rc == 0) continue;

                    // Escalated: directly iterate targeted y span.
                    if (rc == 255)
                    {
                        var arr = col.Escalated;
                        for (int y = lyStart; y <= lyEnd; y++)
                        {
                            ushort id = arr[y];
                            if (id == AIR || id == replacementId) continue;
                            if (IdMatches(id, ids, targeted))
                            {
                                arr[y] = replacementId;
                                anyChange = true;
                            }
                        }
                        continue;
                    }

                    // First run overlap.
                    if (rc >= 1 && RangesOverlap(col.Y0Start, col.Y0End, lyStart, lyEnd) && col.Id0 != replacementId && IdMatches(col.Id0, ids, targeted))
                    {
                        bool runContained = lyStart <= col.Y0Start && lyEnd >= col.Y0End;
                        if (runContained)
                        {
                            col.Id0 = replacementId; // whole run replaced
                            anyChange = true;
                        }
                        else
                        {
                            // Partial run edit forces escalation.
                            var arr = col.Escalated ?? new ushort[S];
                            if (col.Escalated == null)
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                                if (rc == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                            }
                            int ys = Math.Max(col.Y0Start, lyStart);
                            int ye = Math.Min(col.Y0End, lyEnd);
                            for (int y = ys; y <= ye; y++) arr[y] = replacementId;
                            col.Escalated = arr;
                            col.RunCount = 255;

                            // Rebuild fast metadata (16 iterations).
                            ushort mask = 0; byte nonAir = 0; byte adj = 0; ushort prevBit = 0;
                            for (int y = 0; y < S; y++)
                            {
                                if (arr[y] != AIR)
                                {
                                    mask |= (ushort)(1 << y);
                                    nonAir++;
                                    if (prevBit == 1) adj++;
                                    prevBit = 1;
                                }
                                else prevBit = 0;
                            }
                            col.OccMask = mask; col.NonAir = nonAir; col.AdjY = adj;
                            anyChange = true;
                            continue; // move to next column
                        }
                    }

                    // Second run overlap.
                    if (rc == 2 && RangesOverlap(col.Y1Start, col.Y1End, lyStart, lyEnd) && col.Id1 != replacementId && IdMatches(col.Id1, ids, targeted))
                    {
                        bool runContained = lyStart <= col.Y1Start && lyEnd >= col.Y1End;
                        if (runContained)
                        {
                            col.Id1 = replacementId;
                            anyChange = true;
                        }
                        else
                        {
                            var arr = col.Escalated ?? new ushort[S];
                            if (col.Escalated == null)
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                                for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                            }
                            int ys = Math.Max(col.Y1Start, lyStart);
                            int ye = Math.Min(col.Y1End, lyEnd);
                            for (int y = ys; y <= ye; y++) arr[y] = replacementId;
                            col.Escalated = arr;
                            col.RunCount = 255;

                            // Rebuild fast metadata (16 iterations).
                            ushort mask = 0; byte nonAir = 0; byte adj = 0; ushort prevBit = 0;
                            for (int y = 0; y < S; y++)
                            {
                                if (arr[y] != AIR)
                                {
                                    mask |= (ushort)(1 << y);
                                    nonAir++;
                                    if (prevBit == 1) adj++;
                                    prevBit = 1;
                                }
                                else prevBit = 0;
                            }
                            col.OccMask = mask; col.NonAir = nonAir; col.AdjY = adj;
                            anyChange = true;
                        }
                    }
                }
            }

            if (anyChange)
                scratch.DistinctDirty = true; // distinct set must be recomputed at finalize
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

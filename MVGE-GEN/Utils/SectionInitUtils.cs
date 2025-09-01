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
        private static void TrackDistinct(SectionBuildScratch sc, ushort id, int columnIndex = -1)
        {
            if (id == AIR) return;
            int dc = sc.DistinctCount;
            for (int i = 0; i < dc; i++)
            {
                if (sc.Distinct[i] == id)
                {
                    if (columnIndex >= 0)
                    {
                        int word = columnIndex >> 6; int bit = columnIndex & 63;
                        sc.IdColumnBits[i, word] |= 1UL << bit;
                    }
                    return; // already tracked
                }
            }
            if (dc < sc.Distinct.Length)
            {
                sc.Distinct[dc] = id;
                if (columnIndex >= 0)
                {
                    int word = columnIndex >> 6; int bit = columnIndex & 63;
                    sc.IdColumnBits[dc, word] |= 1UL << bit;
                }
                sc.DistinctCount = dc + 1;
            }
            else
            {
                sc.DistinctCount = dc; // ignore overflow
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
                    TrackDistinct(scratch, id, ci);
                }
            }

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
            if (blockId == AIR || (uint)yStart > 15 || (uint)yEnd > 15 || yEnd < yStart) return;

            var scratch = GetScratch(sec);
            scratch.AnyNonAir = true;

            int ci = localZ * S + localX;
            ref var col = ref scratch.GetWritableColumn(ci);

            // Fast full-column insertion
            if (yStart == 0 && yEnd == 15)
            {
                if (col.RunCount == 0)
                {
                    col.RunCount = 1;
                    col.Id0 = blockId;
                    col.Y0Start = 0; col.Y0End = 15;
                    col.OccMask = 0xFFFF;
                    col.NonAir = 16;
                    col.AdjY = 15;
                    TrackDistinct(scratch, blockId, ci);
                    return;
                }
                if (col.RunCount == 1 && col.Y0Start == 0 && col.Y0End == 15)
                {
                    if (col.Id0 != blockId)
                    {
                        col.Id0 = blockId;
                        TrackDistinct(scratch, blockId, ci);
                    }
                    return;
                }
                // Escalated or fragmented: overwrite escalated buffer directly
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort MaskRange(int ys, int ye)
                => (ushort)((0xFFFFu >> (15 - ye)) & (0xFFFFu << ys));

            // ApplyMask with quick-path adjacency
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool ApplyMask(ref ColumnData c, int ys, int ye, ushort segMask)
            {
                ushort prev = c.OccMask;
                ushort added = (ushort)(segMask & ~prev);
                if (added == 0) return false;

                c.OccMask = (ushort)(prev | segMask);
                c.NonAir += (byte)BitOperations.PopCount(added);

                // Quick contiguous insertion test
                if (added == segMask)
                {
                    int len = ye - ys + 1;
                    int delta = len - 1;
                    if (ys > 0 && ((prev >> (ys - 1)) & 1) != 0) delta++;
                    if (ye < 15 && ((prev >> (ye + 1)) & 1) != 0) delta++;
                    c.AdjY = (byte)(c.AdjY + delta);
                }
                else
                {
                    // Fallback region recomputation (localized)
                    int regionStart = ys > 0 ? ys - 1 : ys;
                    int regionEnd = ye < 15 ? ye : 14;
                    int bitsLen = regionEnd - regionStart + 2;
                    ushort regionMask = (ushort)(((1 << bitsLen) - 1) << regionStart);
                    ushort prevRegion = (ushort)(prev & regionMask);
                    ushort newRegion = (ushort)(c.OccMask & regionMask);
                    int prevAdj = BitOperations.PopCount((uint)(prevRegion & (prevRegion << 1)));
                    int newAdj = BitOperations.PopCount((uint)(newRegion & (newRegion << 1)));
                    c.AdjY = (byte)(c.AdjY + (newAdj - prevAdj));
                }
                return true;
            }

            // Escalated path first (hot after replacements)
            if (col.RunCount == 255)
            {
                var arr = col.Escalated ??= new ushort[S];
                ushort segMask = MaskRange(yStart, yEnd);
                // Overwrite voxel ids
                for (int y = yStart; y <= yEnd; y++) arr[y] = blockId;
                bool added = ApplyMask(ref col, yStart, yEnd, segMask);
                if (added) TrackDistinct(scratch, blockId, ci);
                return;
            }

            // Empty
            if (col.RunCount == 0)
            {
                col.RunCount = 1;
                col.Id0 = blockId;
                col.Y0Start = (byte)yStart; col.Y0End = (byte)yEnd;
                ushort segMask = MaskRange(yStart, yEnd);
                ApplyMask(ref col, yStart, yEnd, segMask);
                TrackDistinct(scratch, blockId, ci);
                return;
            }

            // Single run: extend or add second
            if (col.RunCount == 1)
            {
                if (blockId == col.Id0 && yStart == col.Y0End + 1)
                {
                    col.Y0End = (byte)yEnd;
                    ushort segMask = MaskRange(yStart, yEnd);
                    ApplyMask(ref col, yStart, yEnd, segMask);
                    return;
                }
                if (yStart > col.Y0End)
                {
                    col.RunCount = 2;
                    col.Id1 = blockId;
                    col.Y1Start = (byte)yStart; col.Y1End = (byte)yEnd;
                    ushort segMask = MaskRange(yStart, yEnd);
                    ApplyMask(ref col, yStart, yEnd, segMask);
                    TrackDistinct(scratch, blockId, ci);
                    return;
                }
                // Overlap / fragmentation -> escalate
            }
            else if (col.RunCount == 2)
            {
                if (blockId == col.Id1 && yStart == col.Y1End + 1)
                {
                    col.Y1End = (byte)yEnd;
                    ushort segMask = MaskRange(yStart, yEnd);
                    ApplyMask(ref col, yStart, yEnd, segMask);
                    return;
                }
                // Any overlap with either run triggers escalation
            }

            // Escalate (overlap / interior insertion)
            scratch.AnyEscalated = true;
            var full = col.Escalated ?? new ushort[S];

            if (col.RunCount != 255) // replay existing runs once
            {
                for (int y = col.Y0Start; y <= col.Y0End; y++) full[y] = col.Id0;
                if (col.RunCount == 2)
                    for (int y = col.Y1Start; y <= col.Y1End; y++) full[y] = col.Id1;
            }
            for (int y = yStart; y <= yEnd; y++) full[y] = blockId;
            col.Escalated = full;
            col.RunCount = 255;

            // Recompute mask & adjacency cheaply (16 cells)
            ushort mask = 0;
            byte nonAir = 0, adj = 0;
            bool prevSet = false;
            for (int y = 0; y < S; y++)
            {
                if (full[y] != AIR)
                {
                    mask |= (ushort)(1 << y);
                    nonAir++;
                    if (prevSet) adj++;
                    prevSet = true;
                }
                else prevSet = false;
            }
            col.OccMask = mask;
            col.NonAir = nonAir;
            col.AdjY = adj;

            TrackDistinct(scratch, blockId, ci);
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

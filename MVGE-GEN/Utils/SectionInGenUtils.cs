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
        // Precomputed inclusive mask table for y ranges [ys,ye] (ys,ye in 0..15, ys<=ye). Saves bit shifts.
        // -------------------------------------------------------------------------------------------------
        private static readonly ushort[,] _maskTable = BuildMaskTable();
        private static ushort[,] BuildMaskTable()
        {
            var tbl = new ushort[16,16];
            for (int ys = 0; ys < 16; ys++)
            {
                for (int ye = ys; ye < 16; ye++)
                {
                    int len = ye - ys + 1;
                    tbl[ys, ye] = (ushort)(((1 << len) - 1) << ys);
                }
            }
            return tbl;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort MaskRange(int ys, int ye) => _maskTable[ys, ye];

        // -------------------------------------------------------------------------------------------------
        // Array pooling: reduces allocation pressure for frequently reused temporary structures.
        //   Occupancy: 4096 bits (64 * ulong) used when building face masks or packed/sparse data.
        //   Dense:     4096 ushorts storing full per‑voxel ids (8 KB) for DenseExpanded or fallback.
        // Objects are cleared before returning to pool to avoid leaking data across uses.
        // -------------------------------------------------------------------------------------------------
        private static readonly ConcurrentBag<ulong[]> _occupancyPool = new();
        private static readonly ConcurrentBag<ushort[]> _densePool = new();
        // Pool for escalated per-column 16-length voxel arrays
        private static readonly ConcurrentBag<ushort[]> _escalatedColumnPool = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort[] RentEscalatedColumn()
        {
            if (_escalatedColumnPool.TryTake(out var a))
            {
                Array.Clear(a); // ensure clean (16 cells)
                return a;
            }
            return new ushort[16];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnEscalatedColumn(ushort[] arr)
        {
            if (arr == null || arr.Length != 16) return;
            Array.Clear(arr);
            _escalatedColumnPool.Add(arr);
        }

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
        // GenerationAddRun (fast path during initial world gen)
        // Assumptions (current terrain generator):
        //   * Runs are added in strictly non-overlapping, non-descending Y order per column.
        //   * At most two runs per column today (stone then soil). Future design allows up to 8.
        //   * No in-generation overwrites or partial overlaps.
        // Behavior:
        //   * Handles 0 -> 1 run, 1 -> 2 runs, and in-run extension (contiguous merge) cheaply.
        //   * If a third disjoint run is ever encountered (future), we fall back to general AddRun().
        //   * Never escalates to per-voxel unless forwarded to AddRun.
        // Rationale:
        //   * Avoids the heavier overlap / escalation logic in the general runtime AddRun path.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GenerationAddRun(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (sec == null || blockId == AIR || (uint)yStart > 15 || (uint)yEnd > 15 || yEnd < yStart)
                return;

            var scratch = GetScratch(sec);
            scratch.AnyNonAir = true;
            sec.StructuralDirty = true;

            int ci = localZ * S + localX;
            ref var col = ref scratch.GetWritableColumn(ci);

            // Full-height single run shortcut
            if (yStart == 0 && yEnd == 15)
            {
                if (col.RunCount == 0 ||
                    (col.RunCount == 1 && col.Y0Start == 0 && col.Y0End == 15) ||
                    (col.RunCount == 2 && col.Y0Start == 0 && col.Y0End == 15)) // (rare path)
                {
                    col.RunCount = 1;
                    col.Id0 = blockId;
                    col.Y0Start = 0; col.Y0End = 15;
                    col.OccMask = 0xFFFF;
                    col.NonAir = 16;
                    col.AdjY = 15;
                    col.Id1 = 0; col.Y1Start = col.Y1End = 0;
                    TrackDistinct(scratch, blockId, ci);
                    return;
                }
            }

            ushort segMask = MaskRange(yStart, yEnd);

            // Empty column
            if (col.RunCount == 0)
            {
                col.RunCount = 1;
                col.Id0 = blockId;
                col.Y0Start = (byte)yStart; col.Y0End = (byte)yEnd;
                int spanLen = yEnd - yStart + 1;
                col.OccMask = segMask;
                col.NonAir = (byte)spanLen;
                col.AdjY = (byte)(spanLen - 1);
                TrackDistinct(scratch, blockId, ci);
                return;
            }

            // Single run present
            if (col.RunCount == 1)
            {
                // Same id contiguous top
                if (blockId == col.Id0 && yStart == col.Y0End + 1)
                {
                    int addLen = yEnd - yStart + 1;
                    col.Y0End = (byte)yEnd;
                    col.NonAir += (byte)addLen;
                    col.AdjY += (byte)addLen; // (addLen-1 internal) + 1 bridging
                    col.OccMask = MaskRange(col.Y0Start, col.Y0End);
                    return;
                }
                // Same id contiguous bottom
                if (blockId == col.Id0 && yEnd == col.Y0Start - 1)
                {
                    int addLen = yEnd - yStart + 1;
                    col.Y0Start = (byte)yStart;
                    col.NonAir += (byte)addLen;
                    col.AdjY += (byte)addLen;
                    col.OccMask = MaskRange(col.Y0Start, col.Y0End);
                    return;
                }
                // Non-overlapping disjoint second run (expected stone->soil case)
                if (yStart > col.Y0End + 1)
                {
                    col.RunCount = 2;
                    col.Id1 = blockId;
                    col.Y1Start = (byte)yStart; col.Y1End = (byte)yEnd;
                    int spanLen = yEnd - yStart + 1;
                    col.OccMask |= segMask;
                    col.NonAir += (byte)spanLen;
                    int internalPairs = spanLen - 1;
                    int bridging = (yStart == col.Y0End + 1) ? 1 : 0;
                    col.AdjY += (byte)(internalPairs + bridging);
                    TrackDistinct(scratch, blockId, ci);
                    return;
                }

                // Overlap / unexpected pattern (future terrain complexity) -> defer to full logic
                AddRun(sec, localX, localZ, yStart, yEnd, blockId);
                return;
            }

            // Two runs present (current generator never adds a third; future => fallback)
            if (col.RunCount == 2)
            {
                // Attempt cheap contiguous extension of second run
                if (blockId == col.Id1 && yStart == col.Y1End + 1)
                {
                    int addLen = yEnd - yStart + 1;
                    col.Y1End = (byte)yEnd;
                    col.NonAir += (byte)addLen;
                    col.AdjY += (byte)addLen;
                    col.OccMask |= segMask;
                    return;
                }

                // No third-run support in fast path yet – fallback to general AddRun
                AddRun(sec, localX, localZ, yStart, yEnd, blockId);
                return;
            }

            // Any other (escalated or unexpected) -> use general path (should not occur in generation fast path)
            AddRun(sec, localX, localZ, yStart, yEnd, blockId);
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

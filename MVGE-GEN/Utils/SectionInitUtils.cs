using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GEN.Models;
using MVGE_INF.Models.Terrain;
using static MVGE_GEN.Models.ChunkSection; // added for BaseBlockType
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using System.Buffers;

namespace MVGE_GEN.Utils
{
    internal static partial class SectionUtils
    {
        public static bool EnableFastSectionClassification = true;
        private const int S = ChunkSection.SECTION_SIZE;
        private const int AIR = ChunkSection.AIR;
        private const int COLUMN_COUNT = S * S; // 256 columns per section (z * S + x)

        // ---------------------------------------------------------------------
        // Array pools to reduce allocation / GC pressure.
        //  Occupancy: 64 * 8 bytes = 512 bytes (4096 bits) per section when needed.
        //  Dense: 4096 ushorts (8 KB) for DenseExpanded or escalated fallback.
        // ---------------------------------------------------------------------
        private static readonly ConcurrentBag<ulong[]> _occupancyPool = new();
        private static readonly ConcurrentBag<ushort[]> _densePool = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] RentOccupancy() => _occupancyPool.TryTake(out var a) ? a : new ulong[64];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnOccupancy(ulong[] arr)
        {
            if (arr == null || arr.Length != 64) return;
            // Clear for safety (small) then return to pool.
            Array.Clear(arr);
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

        // ---------------- Scratch pooling ----------------
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
                // Clamp (we do not add beyond capacity; large variety will push dense anyway)
                sc.DistinctCount = dc; // unchanged
            }
        }

        // ---------------------------------------------------------------------
        // Convert a Uniform section to scratch-based run representation so
        // later mutation / replacement logic can proceed uniformly.
        // ---------------------------------------------------------------------
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
                    col.Escalated = null; // ensure not carrying previous allocation
                }
            }

            TrackDistinct(scratch, id);
            scratch.AnyNonAir = true;

            // Reset the section so finalization will choose the new representation.
            sec.Kind = ChunkSection.RepresentationKind.Empty;
            sec.MetadataBuilt = false;
            sec.UniformBlockId = 0;
            sec.CompletelyFull = false;
        }

        // ---------------------------------------------------------------------
        // AddRun: adds a vertical run [yStart,yEnd] of a block inside a column.
        // Escalates to per-voxel (RunCount==255) only when necessary.
        // ---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddRun(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (blockId == AIR || yEnd < yStart) return; // trivial rejects
            var scratch = GetScratch(sec);
            scratch.AnyNonAir = true;

            int ci = localZ * S + localX;
            ref var col = ref scratch.GetWritableColumn(ci);

            if (col.RunCount == 255)
            {
                // Already escalated -> just write the cells.
                var arr = col.Escalated ??= new ushort[S];
                for (int y = yStart; y <= yEnd; y++)
                    arr[y] = blockId;
                TrackDistinct(scratch, blockId);
                return;
            }

            if (col.RunCount == 0)
            {
                col.Id0 = blockId;
                col.Y0Start = (byte)yStart;
                col.Y0End = (byte)yEnd;
                col.RunCount = 1;
                col.Escalated = null;
                TrackDistinct(scratch, blockId);
                return;
            }

            if (col.RunCount == 1)
            {
                // Try extend first run or add second distinct run.
                if (blockId == col.Id0 && yStart == col.Y0End + 1)
                {
                    col.Y0End = (byte)yEnd;
                    return;
                }
                if (yStart > col.Y0End)
                {
                    col.Id1 = blockId;
                    col.Y1Start = (byte)yStart;
                    col.Y1End = (byte)yEnd;
                    col.RunCount = 2;
                    TrackDistinct(scratch, blockId);
                    return;
                }
            }

            if (col.RunCount == 2)
            {
                // Try extend second run.
                if (blockId == col.Id1 && yStart == col.Y1End + 1)
                {
                    col.Y1End = (byte)yEnd;
                    return;
                }
            }

            // Escalate to per-voxel storage (rare path)
            scratch.AnyEscalated = true;
            var full = col.Escalated ?? new ushort[S];

            if (col.RunCount != 255)
            {
                // Replay existing runs into escalated storage once.
                if (col.RunCount >= 1)
                {
                    for (int y = col.Y0Start; y <= col.Y0End; y++)
                        full[y] = col.Id0;
                    if (col.RunCount == 2)
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                            full[y] = col.Id1;
                    }
                }
            }

            // Write the new run cells.
            for (int y = yStart; y <= yEnd; y++)
                full[y] = blockId;

            col.Escalated = full;
            col.RunCount = 255;
            TrackDistinct(scratch, blockId);
        }

        // ---------------------------------------------------------------------
        // ApplyReplacement: replaces targeted block ids within a vertical slice.
        // Distinct palette scanned first for predicate to reduce per-voxel checks.
        // ---------------------------------------------------------------------
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
            var scratch = GetScratch(sec);
            int distinctCount = scratch.DistinctCount;
            if (distinctCount == 0) return;

            Span<ushort> ids = stackalloc ushort[distinctCount];
            Span<bool> targeted = stackalloc bool[distinctCount];
            bool anyTarget = false;

            // Build targeted map.
            for (int i = 0; i < distinctCount; i++)
            {
                ushort id = scratch.Distinct[i];
                ids[i] = id;
                if (id == replacementId)
                {
                    targeted[i] = false; // Avoid replacing with itself
                    continue;
                }
                var bt = baseTypeGetter(id);
                bool shouldReplace = predicate(id, bt);
                targeted[i] = shouldReplace;
                if (shouldReplace) anyTarget = true;
            }
            if (!anyTarget) return;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IdMatches(ushort id, Span<ushort> idsLocal, Span<bool> targetedLocal)
            {
                for (int i = 0; i < idsLocal.Length; i++)
                    if (idsLocal[i] == id) return targetedLocal[i];
                return false;
            }

            bool anyChange = false;

            // Full cover: we know the vertical interval matches whole section height, so we can mutate run ids directly when possible.
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

            // Partial slice cover: escalate only when partial overlap forces per-voxel edits.
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

                    // First run partial or full overlap.
                    if (rc >= 1 && RangesOverlap(col.Y0Start, col.Y0End, lyStart, lyEnd) && col.Id0 != replacementId && IdMatches(col.Id0, ids, targeted))
                    {
                        bool runContained = lyStart <= col.Y0Start && lyEnd >= col.Y0End;
                        if (runContained)
                        {
                            col.Id0 = replacementId;
                            anyChange = true;
                        }
                        else
                        {
                            // Escalate to per-voxel for partial modification.
                            var arr = col.Escalated ?? new ushort[S];
                            if (col.Escalated == null)
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                    arr[y] = col.Id0;
                                if (rc == 2)
                                {
                                    for (int y = col.Y1Start; y <= col.Y1End; y++)
                                        arr[y] = col.Id1;
                                }
                            }
                            int ys = Math.Max(col.Y0Start, lyStart);
                            int ye = Math.Min(col.Y0End, lyEnd);
                            for (int y = ys; y <= ye; y++)
                                arr[y] = replacementId;
                            col.Escalated = arr;
                            col.RunCount = 255;
                            anyChange = true;
                            continue; // move to next column
                        }
                    }

                    // Second run partial or full overlap.
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
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                    arr[y] = col.Id0;
                                for (int y = col.Y1Start; y <= col.Y1End; y++)
                                    arr[y] = col.Id1;
                            }
                            int ys = Math.Max(col.Y1Start, lyStart);
                            int ye = Math.Min(col.Y1End, lyEnd);
                            for (int y = ys; y <= ye; y++)
                                arr[y] = replacementId;
                            col.Escalated = arr;
                            col.RunCount = 255;
                            anyChange = true;
                        }
                    }
                }
            }

            if (anyChange)
                scratch.DistinctDirty = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RangesOverlap(int a0, int a1, int b0, int b1) => a0 <= b1 && b0 <= a1;

        // Popcount over occupancy (hardware accelerated if available)
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int OverlapLen(int a0, int a1, int b0, int b1)
        {
            int s = a0 > b0 ? a0 : b0;
            int e = a1 < b1 ? a1 : b1;
            int len = e - s + 1;
            return len > 0 ? len : 0;
        }

        // Set contiguous y bits for column 'columnIndex' in occupancy bitset.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetRunBits(ulong[] occ, int columnIndex, int yStart, int yEnd)
        {
            int baseLi = (columnIndex << 4) + yStart;     // first linear index in run
            int endLi = (columnIndex << 4) + yEnd;        // last linear index in run
            int startWord = baseLi >> 6;
            int endWord = endLi >> 6;
            int startBit = baseLi & 63;
            int len = endLi - baseLi + 1;

            if (startWord == endWord)
            {
                // All bits in one 64-bit word
                occ[startWord] |= ((1UL << len) - 1) << startBit;
            }
            else
            {
                // Spans exactly two words (since run <=16 and word boundary alignment)
                int firstLen = 64 - startBit;
                occ[startWord] |= ((1UL << firstLen) - 1) << startBit;
                int remaining = len - firstLen;
                occ[endWord] |= (1UL << remaining) - 1;
            }
        }

        // ---------------------------------------------------------------------
        // FinalizeSection: builds final representation + metadata from scratch runs.
        // Fast path avoids allocating occupancy if we can decide representation
        // directly (uniform / sparse / dense w/o faces yet needed).
        // ---------------------------------------------------------------------
        public static void FinalizeSection(ChunkSection sec)
        {
            if (sec == null) return;
            var scratch = sec.BuildScratch as SectionBuildScratch;

            // Empty / all‑air quick exit
            if (scratch == null || !scratch.AnyNonAir)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty;
                sec.IsAllAir = true;
                sec.MetadataBuilt = true;
                sec.VoxelCount = S * S * S;
                if (scratch != null)
                {
                    sec.BuildScratch = null;
                    ReturnScratch(scratch);
                }
                return;
            }

            // FAST PATH: no escalated (per‑voxel) columns so all columns have at most two runs.
            if (!scratch.AnyEscalated)
            {
                // Rebuild distinct id list if marked dirty. Only iterates run metadata (no per‑voxel scan).
                if (scratch.DistinctDirty)
                {
                    Span<ushort> tmp = stackalloc ushort[8]; // temp unique buffer (max 8 distinct tracked)
                    int count = 0;
                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;

                        // First run
                        if (rc >= 1 && col.Id0 != AIR)
                        {
                            bool found = false;
                            for (int i = 0; i < count; i++)
                            {
                                if (tmp[i] == col.Id0) { found = true; break; }
                            }
                            if (!found && count < tmp.Length)
                            {
                                tmp[count++] = col.Id0;
                            }
                        }
                        // Second run
                        if (rc == 2 && col.Id1 != AIR)
                        {
                            bool found = false;
                            for (int i = 0; i < count; i++)
                            {
                                if (tmp[i] == col.Id1) { found = true; break; }
                            }
                            if (!found && count < tmp.Length)
                            {
                                tmp[count++] = col.Id1;
                            }
                        }
                    }
                    scratch.DistinctCount = count;
                    for (int i = 0; i < count; i++) scratch.Distinct[i] = tmp[i];
                    scratch.DistinctDirty = false;
                }

                sec.VoxelCount = S * S * S;
                ulong[] occ = null; // allocated only if we end up needing occupancy (Packed single‑id path)

                // ------------------------------------------------------------------
                // Pass 1: aggregate counts, internal vertical adjacency, bounds
                // ------------------------------------------------------------------
                int nonAir = 0;
                int adjY = 0;               // vertical adjacency inside runs
                int adjX = 0, adjZ = 0;     // horizontal adjacency (computed later)
                bool boundsInit = false;
                byte minX = 0, maxX = 0;
                byte minY = 0, maxY = 0;
                byte minZ = 0, maxZ = 0;

                for (int z = 0; z < S; z++)
                {
                    int zBase = z * S;
                    for (int x = 0; x < S; x++)
                    {
                        int columnIndex = zBase + x;
                        ref var col = ref scratch.GetReadonlyColumn(columnIndex);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;

                        // First run statistics
                        if (rc >= 1)
                        {
                            int len0 = col.Y0End - col.Y0Start + 1;
                            nonAir += len0;
                            adjY += len0 - 1; // each contiguous pair inside run contributes one internal vertical adjacency

                            if (!boundsInit)
                            {
                                minX = maxX = (byte)x;
                                minZ = maxZ = (byte)z;
                                minY = col.Y0Start;
                                maxY = col.Y0End;
                                boundsInit = true;
                            }
                            else
                            {
                                if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                                if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;
                                if (col.Y0Start < minY) minY = col.Y0Start; if (col.Y0End > maxY) maxY = col.Y0End;
                            }
                        }

                        // Second run statistics
                        if (rc == 2)
                        {
                            int len1 = col.Y1End - col.Y1Start + 1;
                            nonAir += len1;
                            adjY += len1 - 1;

                            if (!boundsInit)
                            {
                                minX = maxX = (byte)x;
                                minZ = maxZ = (byte)z;
                                minY = col.Y1Start;
                                maxY = col.Y1End;
                                boundsInit = true;
                            }
                            else
                            {
                                if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                                if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;
                                if (col.Y1Start < minY) minY = col.Y1Start; if (col.Y1End > maxY) maxY = col.Y1End;
                            }
                        }
                    }
                }

                sec.NonAirCount = nonAir;
                if (!boundsInit)
                {
                    // All air despite AnyNonAir flag (defensive)
                    sec.Kind = ChunkSection.RepresentationKind.Empty;
                    sec.IsAllAir = true;
                    sec.MetadataBuilt = true;
                    ReturnScratch(scratch);
                    sec.BuildScratch = null;
                    return;
                }

                // Persist bounds
                sec.HasBounds = true;
                sec.MinLX = minX; sec.MaxLX = maxX;
                sec.MinLY = minY; sec.MaxLY = maxY;
                sec.MinLZ = minZ; sec.MaxLZ = maxZ;

                // ------------------------------------------------------------------
                // Pass 2: horizontal adjacency inside +X direction (same z row)
                // ------------------------------------------------------------------
                for (int z = 0; z < S; z++)
                {
                    int zBase = z * S;
                    for (int x = 0; x < S - 1; x++)
                    {
                        ref var a = ref scratch.GetReadonlyColumn(zBase + x);
                        ref var b = ref scratch.GetReadonlyColumn(zBase + x + 1);
                        if (a.RunCount == 0 || b.RunCount == 0) continue;

                        // Overlaps between runs produce internal shared faces along X
                        if (a.RunCount >= 1 && b.RunCount >= 1)
                            adjX += OverlapLen(a.Y0Start, a.Y0End, b.Y0Start, b.Y0End);
                        if (a.RunCount >= 1 && b.RunCount == 2)
                            adjX += OverlapLen(a.Y0Start, a.Y0End, b.Y1Start, b.Y1End);
                        if (a.RunCount == 2 && b.RunCount >= 1)
                            adjX += OverlapLen(a.Y1Start, a.Y1End, b.Y0Start, b.Y0End);
                        if (a.RunCount == 2 && b.RunCount == 2)
                            adjX += OverlapLen(a.Y1Start, a.Y1End, b.Y1Start, b.Y1End);
                    }
                }

                // ------------------------------------------------------------------
                // Pass 3: horizontal adjacency inside +Z direction (between z slices)
                // ------------------------------------------------------------------
                for (int z = 0; z < S - 1; z++)
                {
                    int zBase = z * S;
                    int zBaseNext = (z + 1) * S;
                    for (int x = 0; x < S; x++)
                    {
                        ref var a = ref scratch.GetReadonlyColumn(zBase + x);
                        ref var b = ref scratch.GetReadonlyColumn(zBaseNext + x);
                        if (a.RunCount == 0 || b.RunCount == 0) continue;

                        if (a.RunCount >= 1 && b.RunCount >= 1)
                            adjZ += OverlapLen(a.Y0Start, a.Y0End, b.Y0Start, b.Y0End);
                        if (a.RunCount >= 1 && b.RunCount == 2)
                            adjZ += OverlapLen(a.Y0Start, a.Y0End, b.Y1Start, b.Y1End);
                        if (a.RunCount == 2 && b.RunCount >= 1)
                            adjZ += OverlapLen(a.Y1Start, a.Y1End, b.Y0Start, b.Y0End);
                        if (a.RunCount == 2 && b.RunCount == 2)
                            adjZ += OverlapLen(a.Y1Start, a.Y1End, b.Y1Start, b.Y1End);
                    }
                }

                // Internal exposure formula: 6 * N - 2 * (adjX + adjY + adjZ)
                int exposure = 6 * nonAir - 2 * (adjX + adjY + adjZ);
                sec.InternalExposure = exposure;

                // ------------------------------------------------------------------
                // Representation decision (uniform / sparse / packed single id / dense multi id)
                // ------------------------------------------------------------------
                if (nonAir == S * S * S && scratch.DistinctCount == 1)
                {
                    // Entire section filled by one block id
                    sec.Kind = ChunkSection.RepresentationKind.Uniform;
                    sec.UniformBlockId = scratch.Distinct[0];
                    sec.IsAllAir = false;
                    sec.CompletelyFull = true;
                }
                else if (nonAir <= 128)
                {
                    // Sparse representation: materialize parallel index & block arrays.
                    int count = nonAir;
                    int[] idxArr = new int[count];
                    ushort[] blkArr = new ushort[count];
                    int p = 0;
                    ushort singleId = scratch.DistinctCount == 1 ? scratch.Distinct[0] : (ushort)0;

                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;

                        // First run
                        if (rc >= 1)
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++)
                            {
                                int li = (ci << 4) + y;
                                idxArr[p] = li;
                                blkArr[p] = singleId != 0 ? singleId : col.Id0;
                                p++;
                            }
                        }
                        // Second run
                        if (rc == 2)
                        {
                            for (int y = col.Y1Start; y <= col.Y1End; y++)
                            {
                                int li = (ci << 4) + y;
                                idxArr[p] = li;
                                blkArr[p] = singleId != 0 ? singleId : col.Id1;
                                p++;
                            }
                        }
                        // Safety: escalated should not appear here but handle defensively
                        if (rc == 255)
                        {
                            var arr = col.Escalated;
                            if (arr != null)
                            {
                                for (int y = 0; y < S; y++)
                                {
                                    ushort id = arr[y];
                                    if (id == AIR) continue;
                                    int li = (ci << 4) + y;
                                    idxArr[p] = li;
                                    blkArr[p] = singleId != 0 ? singleId : id;
                                    p++;
                                }
                            }
                        }
                    }
                    sec.Kind = ChunkSection.RepresentationKind.Sparse;
                    sec.SparseIndices = idxArr;
                    sec.SparseBlocks = blkArr;
                    sec.IsAllAir = false;
                }
                else
                {
                    if (scratch.DistinctCount == 1)
                    {
                        // Single non‑air id but not full volume -> Packed (with occupancy created via runs)
                        sec.Kind = ChunkSection.RepresentationKind.Packed;
                        sec.Palette = new List<ushort> { AIR, scratch.Distinct[0] };
                        sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { scratch.Distinct[0], 1 } };
                        sec.BitsPerIndex = 1;
                        sec.BitData = new uint[(4096 + 31) / 32]; // 128 uints

                        var occSingle = RentOccupancy();
                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            byte rc = col.RunCount;
                            if (rc == 0) continue;
                            if (rc == 255)
                            {
                                // Defensive only (should not happen without AnyEscalated)
                                var arr = col.Escalated;
                                if (arr != null)
                                {
                                    for (int y = 0; y < S; y++)
                                    {
                                        if (arr[y] == AIR) continue;
                                        int li = (ci << 4) + y;
                                        occSingle[li >> 6] |= 1UL << (li & 63);
                                    }
                                }
                            }
                            else
                            {
                                if (rc >= 1) SetRunBits(occSingle, ci, col.Y0Start, col.Y0End);
                                if (rc == 2) SetRunBits(occSingle, ci, col.Y1Start, col.Y1End);
                            }
                        }
                        sec.OccupancyBits = occSingle;

                        // Fill packed BitData bits (palette index 1 indicates solid)
                        for (int w = 0; w < occSingle.Length; w++)
                        {
                            ulong val = occSingle[w];
                            while (val != 0)
                            {
                                int bit = BitOperations.TrailingZeroCount(val);
                                int li = (w << 6) + bit; // 64 bits per word
                                int bw = li >> 5;         // 32-bit word index
                                int bo = li & 31;         // bit offset within that word
                                sec.BitData[bw] |= 1u << bo;
                                val &= val - 1;          // clear lowest set bit
                            }
                        }
                        sec.IsAllAir = false;
                        BuildFaceMasks(sec, occSingle);
                    }
                    else
                    {
                        // Dense multi‑id: expand to per‑voxel dense array + occupancy in one pass
                        sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                        var dense = RentDense();
                        var occDense = RentOccupancy();

                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            byte rc = col.RunCount;
                            if (rc == 0) continue;
                            int baseLi = ci << 4; // start linear index for column (y=0)

                            if (rc == 255)
                            {
                                var arr = col.Escalated; // defensive
                                if (arr != null)
                                {
                                    for (int y = 0; y < S; y++)
                                    {
                                        ushort id = arr[y];
                                        if (id == AIR) continue;
                                        int li = baseLi + y;
                                        dense[li] = id;
                                        occDense[li >> 6] |= 1UL << (li & 63);
                                    }
                                }
                            }
                            else
                            {
                                if (rc >= 1)
                                {
                                    for (int y = col.Y0Start; y <= col.Y0End; y++)
                                    {
                                        int li = baseLi + y;
                                        dense[li] = col.Id0;
                                        occDense[li >> 6] |= 1UL << (li & 63);
                                    }
                                }
                                if (rc == 2)
                                {
                                    for (int y = col.Y1Start; y <= col.Y1End; y++)
                                    {
                                        int li = baseLi + y;
                                        dense[li] = col.Id1;
                                        occDense[li >> 6] |= 1UL << (li & 63);
                                    }
                                }
                            }
                        }
                        sec.ExpandedDense = dense;
                        sec.OccupancyBits = occDense;
                        sec.IsAllAir = false;
                        BuildFaceMasks(sec, occDense);
                    }
                }

                sec.MetadataBuilt = true;
                sec.BuildScratch = null;
                ReturnScratch(scratch);
                return;
            }

            // Slow path: some columns escalated to per‑voxel arrays; build dense + occupancy directly
            FinalizeSectionFallBack(sec, scratch);
        }

        // Escalated fallback path: build dense representation + metadata using pooled dense array.
        private static void FinalizeSectionFallBack(ChunkSection sec, SectionBuildScratch scratch)
        {
            sec.VoxelCount = S * S * S;
            var dense = RentDense();
            var occ = RentOccupancy(); // occupancy constructed alongside dense fill

            int nonAir = 0;
            byte minX = 255, minY = 255, minZ = 255;
            byte maxX = 0,   maxY = 0,   maxZ = 0;
            bool bounds = false;

            int adjX = 0, adjY = 0, adjZ = 0;

            // Writes a voxel value if first time encountered; updates bounds, occupancy & counts.
            void SetVoxel(int ci, int x, int z, int y, ushort id)
            {
                if (id == AIR) return;
                int li = (ci << 4) + y;
                if (dense[li] != 0) return; // already set by another overlapping run (should not happen but safe)
                dense[li] = id;
                nonAir++;
                occ[li >> 6] |= 1UL << (li & 63);
                if (!bounds)
                {
                    bounds = true;
                    minX = maxX = (byte)x;
                    minY = maxY = (byte)y;
                    minZ = maxZ = (byte)z;
                }
                else
                {
                    if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                    if (y < minY) minY = (byte)y; else if (y > maxY) maxY = (byte)y;
                    if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;
                }
            }

            // Populate dense + vertical adjacency
            for (int ci = 0; ci < COLUMN_COUNT; ci++)
            {
                ref var col = ref scratch.GetReadonlyColumn(ci);
                byte rc = col.RunCount;
                if (rc == 0) continue;
                int x = ci % S;
                int z = ci / S;

                if (rc == 255)
                {
                    // Escalated column – copy whole escalated array
                    var arr = col.Escalated;
                    ushort prev = 0;
                    for (int y = 0; y < S; y++)
                    {
                        ushort id = arr[y];
                        if (id != AIR)
                        {
                            SetVoxel(ci, x, z, y, id);
                            if (prev != 0)
                            {
                                // previous voxel in column also solid -> vertical adjacency pair
                                adjY++;
                            }
                            prev = id;
                        }
                        else
                        {
                            prev = 0; // reset run tracking
                        }
                    }
                }
                else
                {
                    // First run
                    if (rc >= 1)
                    {
                        for (int y = col.Y0Start; y <= col.Y0End; y++)
                        {
                            SetVoxel(ci, x, z, y, col.Id0);
                            if (y > col.Y0Start)
                            {
                                adjY++; // internal adjacency inside first run
                            }
                        }
                    }
                    // Second run
                    if (rc == 2)
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                        {
                            SetVoxel(ci, x, z, y, col.Id1);
                            if (y > col.Y1Start)
                            {
                                adjY++; // internal adjacency inside second run
                            }
                        }
                    }
                }
            }

            // Horizontal adjacency: +X (delta 16) and +Z (delta 256)
            const int DeltaX = 16;
            const int DeltaZ = 256;
            for (int z = 0; z < S; z++)
            {
                for (int x = 0; x < S; x++)
                {
                    int ci = z * S + x;
                    for (int y = 0; y < S; y++)
                    {
                        int li = (ci << 4) + y;
                        if (dense[li] == 0) continue;
                        // +X neighbor
                        if (x + 1 < S && dense[li + DeltaX] != 0) adjX++;
                        // +Z neighbor
                        if (z + 1 < S && dense[li + DeltaZ] != 0) adjZ++;
                    }
                }
            }

            // Finalize section metadata
            sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
            sec.ExpandedDense = dense;
            sec.NonAirCount = nonAir;
            if (bounds)
            {
                sec.HasBounds = true;
                sec.MinLX = minX; sec.MaxLX = maxX;
                sec.MinLY = minY; sec.MaxLY = maxY;
                sec.MinLZ = minZ; sec.MaxLZ = maxZ;
            }
            long internalAdj = (long)adjX + adjY + adjZ;
            sec.InternalExposure = (int)(6L * nonAir - 2L * internalAdj);
            sec.IsAllAir = nonAir == 0;
            sec.OccupancyBits = occ;
            BuildFaceMasks(sec, occ);
            sec.MetadataBuilt = true;
            sec.BuildScratch = null;
            ReturnScratch(scratch);
        }
    }
}

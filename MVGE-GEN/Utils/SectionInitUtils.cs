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

            // ---------------------- FAST PATH (no escalated columns) ----------------------
            if (!scratch.AnyEscalated)
            {
                // Rebuild distinct list if dirty using only run data (no per-voxel scan).
                if (scratch.DistinctDirty)
                {
                    Span<ushort> tmp = stackalloc ushort[8];
                    int count = 0;
                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;
                        if (rc >= 1 && col.Id0 != AIR)
                        {
                            bool found = false;
                            for (int i = 0; i < count; i++) if (tmp[i] == col.Id0) { found = true; break; }
                            if (!found && count < 8) tmp[count++] = col.Id0;
                        }
                        if (rc == 2 && col.Id1 != AIR)
                        {
                            bool found = false;
                            for (int i = 0; i < count; i++) if (tmp[i] == col.Id1) { found = true; break; }
                            if (!found && count < 8) tmp[count++] = col.Id1;
                        }
                    }
                    scratch.DistinctCount = count;
                    for (int i = 0; i < count; i++) scratch.Distinct[i] = tmp[i];
                    scratch.DistinctDirty = false;
                }

                sec.VoxelCount = S * S * S;
                ulong[] occ = null; // Occupancy allocated lazily only if needed (Packed single-id path)

                int nonAir = 0;
                int adjY = 0; // vertical adjacency inside columns
                int adjX = 0; // horizontal adjacency along +X direction (between neighboring columns in same z row)
                int adjZ = 0; // horizontal adjacency along +Z direction (between z rows)

                bool boundsInit = false;
                byte minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;

                // Pass 1: accumulate counts, vertical adjacency and bounds (no allocation yet)
                for (int z = 0; z < S; z++)
                {
                    int zBase = z * S;
                    for (int x = 0; x < S; x++)
                    {
                        int columnIndex = zBase + x;
                        ref var col = ref scratch.GetReadonlyColumn(columnIndex);
                        byte rc = col.RunCount;
                        if (rc == 0) continue;

                        if (rc >= 1)
                        {
                            int len0 = col.Y0End - col.Y0Start + 1;
                            nonAir += len0;
                            adjY += len0 - 1; // internal vertical adjacency inside first run
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
                                if (x < minX) minX = (byte)x; if (x > maxX) maxX = (byte)x;
                                if (z < minZ) minZ = (byte)z; if (z > maxZ) maxZ = (byte)z;
                                if (col.Y0Start < minY) minY = col.Y0Start; if (col.Y0End > maxY) maxY = col.Y0End;
                            }
                        }
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
                                if (x < minX) minX = (byte)x; if (x > maxX) maxX = (byte)x;
                                if (z < minZ) minZ = (byte)z; if (z > maxZ) maxZ = (byte)z;
                                if (col.Y1Start < minY) minY = col.Y1Start; if (col.Y1End > maxY) maxY = col.Y1End;
                            }
                        }
                    }
                }

                sec.NonAirCount = nonAir;
                if (!boundsInit)
                {
                    // All air – trivial finalize.
                    sec.Kind = ChunkSection.RepresentationKind.Empty;
                    sec.IsAllAir = true;
                    sec.MetadataBuilt = true;
                    ReturnScratch(scratch);
                    sec.BuildScratch = null;
                    return;
                }

                sec.HasBounds = true;
                sec.MinLX = minX; sec.MaxLX = maxX;
                sec.MinLY = minY; sec.MaxLY = maxY;
                sec.MinLZ = minZ; sec.MaxLZ = maxZ;

                // Horizontal adjacency (face sharing) along +X inside each Z row.
                for (int z = 0; z < S; z++)
                {
                    int zBase = z * S;
                    for (int x = 0; x < S - 1; x++)
                    {
                        ref var a = ref scratch.GetReadonlyColumn(zBase + x);
                        ref var b = ref scratch.GetReadonlyColumn(zBase + x + 1);
                        if (a.RunCount == 0 || b.RunCount == 0) continue;

                        // Overlap between combinations of runs (at most 2 each).
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

                // Horizontal adjacency along +Z between neighboring Z slices.
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

                int exposure = 6 * nonAir - 2 * (adjX + adjY + adjZ);
                sec.InternalExposure = exposure;

                // Representation decision (still no occupancy allocated):
                if (nonAir == S * S * S && scratch.DistinctCount == 1)
                {
                    sec.Kind = ChunkSection.RepresentationKind.Uniform;
                    sec.UniformBlockId = scratch.Distinct[0];
                    sec.IsAllAir = false;
                    sec.CompletelyFull = true;
                }
                else if (nonAir <= 128)
                {
                    // Direct sparse emission (no bitset build necessary).
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

                        if (rc >= 1)
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++)
                            {
                                int li = (ci << 4) + y; // column-major index
                                idxArr[p] = li;
                                blkArr[p] = singleId != 0 ? singleId : col.Id0;
                                p++;
                            }
                        }
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
                        if (rc == 255)
                        {
                            var arr = col.Escalated;
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

                    sec.Kind = ChunkSection.RepresentationKind.Sparse;
                    sec.SparseIndices = idxArr;
                    sec.SparseBlocks = blkArr;
                    sec.IsAllAir = false;
                }
                else
                {
                    if (scratch.DistinctCount == 1)
                    {
                        // Packed single-id representation (need occupancy for face metadata later).
                        sec.Kind = ChunkSection.RepresentationKind.Packed;
                        sec.Palette = new List<ushort> { AIR, scratch.Distinct[0] };
                        sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { scratch.Distinct[0], 1 } };
                        sec.BitsPerIndex = 1;
                        sec.BitData = new uint[(4096 + 31) / 32];
                        occ = RentOccupancy();

                        // Build occupancy bits directly from runs.
                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            byte rc = col.RunCount;
                            if (rc == 0) continue;
                            if (rc == 255)
                            {
                                var arr = col.Escalated;
                                for (int y = 0; y < S; y++)
                                {
                                    if (arr[y] != AIR)
                                    {
                                        int li = (ci << 4) + y;
                                        occ[li >> 6] |= 1UL << (li & 63);
                                    }
                                }
                            }
                            else
                            {
                                if (rc >= 1) SetRunBits(occ, ci, col.Y0Start, col.Y0End);
                                if (rc == 2) SetRunBits(occ, ci, col.Y1Start, col.Y1End);
                            }
                        }

                        sec.OccupancyBits = occ;

                        // Convert occupancy into packed bitData (palette index bit = 1 means non-air -> palette[1]).
                        for (int word = 0; word < occ.Length; word++)
                        {
                            ulong w = occ[word];
                            while (w != 0)
                            {
                                int bit = BitOperations.TrailingZeroCount(w);
                                int li = (word << 6) + bit;
                                int bw = li >> 5; // 32-bit word index in BitData
                                int bo = li & 31; // bit offset inside that word
                                sec.BitData[bw] |= 1u << bo;
                                w &= w - 1; // clear lowest set bit
                            }
                        }
                        sec.IsAllAir = false;
                        // Build boundary face bitsets for this single-id packed section.
                        // This allows the renderer to skip per-voxel neighbor checks and do bitset tests instead.
                        if (sec.FaceNegXBits == null || sec.FacePosXBits == null ||
                            sec.FaceNegYBits == null || sec.FacePosYBits == null ||
                            sec.FaceNegZBits == null || sec.FacePosZBits == null)
                        {
                            BuildFaceMasks(sec, occ);
                        }
                    }
                    else
                    {
                        // Dense expanded (pooled array used)
                        sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                        var dense = RentDense();

                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            byte rc = col.RunCount;
                            if (rc == 0) continue;

                            if (rc == 255)
                            {
                                var arr = col.Escalated;
                                for (int y = 0; y < S; y++)
                                {
                                    ushort id = arr[y];
                                    if (id != AIR)
                                        dense[(ci << 4) + y] = id;
                                }
                            }
                            else
                            {
                                if (rc >= 1)
                                {
                                    for (int y = col.Y0Start; y <= col.Y0End; y++)
                                        dense[(ci << 4) + y] = col.Id0;
                                }
                                if (rc == 2)
                                {
                                    for (int y = col.Y1Start; y <= col.Y1End; y++)
                                        dense[(ci << 4) + y] = col.Id1;
                                }
                            }
                        }
                        sec.ExpandedDense = dense;
                        sec.IsAllAir = false;
                    }
                }

                sec.MetadataBuilt = true;
                sec.BuildScratch = null;
                ReturnScratch(scratch);
                return;
            }

            FinalizeSectionFallBack(sec, scratch);
        }

        // Escalated fallback path: build dense representation + metadata using pooled dense array.
        private static void FinalizeSectionFallBack(ChunkSection sec, SectionBuildScratch scratch)
        {
            sec.VoxelCount = S * S * S;
            var dense = RentDense();

            int nonAir = 0;
            byte minX = 255, minY = 255, minZ = 255;
            byte maxX = 0, maxY = 0, maxZ = 0;
            bool bounds = false;

            int adjX = 0; // adjacency across +X faces
            int adjY = 0; // vertical adjacency
            int adjZ = 0; // adjacency across +Z faces

            // Local function to set a voxel cell and update bounds.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SetVoxel(int ci, int x, int z, int y, ushort id)
            {
                if (id == AIR) return;
                int li = (ci << 4) + y;
                if (dense[li] == 0)
                {
                    dense[li] = id;
                    nonAir++;
                    if (!bounds)
                    {
                        minX = maxX = (byte)x;
                        minY = maxY = (byte)y;
                        minZ = maxZ = (byte)z;
                        bounds = true;
                    }
                    else
                    {
                        if (x < minX) minX = (byte)x; if (x > maxX) maxX = (byte)x;
                        if (y < minY) minY = (byte)y; if (y > maxY) maxY = (byte)y;
                        if (z < minZ) minZ = (byte)z; if (z > maxZ) maxZ = (byte)z;
                    }
                }
            }

            // Populate dense array and vertical adjacency.
            for (int ci = 0; ci < COLUMN_COUNT; ci++)
            {
                ref var col = ref scratch.GetReadonlyColumn(ci);
                byte rc = col.RunCount;
                if (rc == 0) continue;
                int x = ci % S;
                int z = ci / S;

                if (rc == 255)
                {
                    var arr = col.Escalated;
                    ushort prev = 0;
                    for (int y = 0; y < S; y++)
                    {
                        ushort id = arr[y];
                        if (id != AIR)
                        {
                            SetVoxel(ci, x, z, y, id);
                            if (prev != 0) adjY++; // vertical adjacency
                            prev = id;
                        }
                        else
                        {
                            prev = 0;
                        }
                    }
                }
                else
                {
                    if (rc >= 1)
                    {
                        for (int y = col.Y0Start; y <= col.Y0End; y++)
                        {
                            SetVoxel(ci, x, z, y, col.Id0);
                            if (y > col.Y0Start) adjY++; // vertical adjacency inside first run
                        }
                    }
                    if (rc == 2)
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                        {
                            SetVoxel(ci, x, z, y, col.Id1);
                            if (y > col.Y1Start) adjY++; // vertical adjacency inside second run
                        }
                    }
                }
            }

            // Horizontal adjacency across +X and +Z using linear index deltas.
            // For column-major layout: li = (ci << 4) + y
            //  +X neighbor within same z: deltaCI = +1 => deltaLI = +16
            //  +Z neighbor: deltaCI = +S (16) => deltaLI = + (S << 4) = 256
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

                        // +X adjacency
                        if (x + 1 < S && dense[li + DeltaX] != 0)
                            adjX++;
                        // +Z adjacency
                        if (z + 1 < S && dense[li + DeltaZ] != 0)
                            adjZ++;
                    }
                }
            }

            // Finalize section metadata.
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
            long internalAdj = adjX + adjY + adjZ;
            sec.InternalExposure = (int)(6L * nonAir - 2L * internalAdj);
            sec.IsAllAir = nonAir == 0;
            sec.MetadataBuilt = true;
            sec.BuildScratch = null;
            ReturnScratch(scratch);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ResolveBlockId(ref ColumnData col, int li)
        {
            int y = li & 15; // low 4 bits -> y
            if (col.RunCount == 255) return col.Escalated[y];
            if (col.RunCount == 0) return AIR;
            if (col.RunCount >= 1 && y >= col.Y0Start && y <= col.Y0End) return col.Id0;
            if (col.RunCount == 2 && y >= col.Y1Start && y <= col.Y1End) return col.Id1;
            return AIR;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GEN.Models;
using MVGE_INF.Models.Terrain;
using static MVGE_GEN.Models.ChunkSection; // added for BaseBlockType
using System.Numerics;
using System.Runtime.Intrinsics.X86;

namespace MVGE_GEN.Utils
{
    public static partial class SectionUtils
    {
        public static bool EnableFastSectionClassification = true;
        private const int S = ChunkSection.SECTION_SIZE;
        private const int AIR = ChunkSection.AIR;
        private const int COLUMN_COUNT = S * S; // 256

        // --- Precomputed column bit placement tables ---
        // For index idx = z*S + x (0..255): word offset inside a given y-slice (0..3) and bit mask.
        // A full occupancy array has 16 slices * 4 words = 64 words.
        private static readonly int[] ColSliceWord = new int[COLUMN_COUNT];
        private static readonly ulong[] ColBitMask = new ulong[COLUMN_COUNT];
        static SectionUtils()
        {
            for (int z = 0; z < S; z++)
            for (int x = 0; x < S; x++)
            {
                int idx = z * S + x;
                int sliceWord = idx >> 6;              // 0..3 inside a 256-bit slice (64-bit groups)
                int bit = idx & 63;                    // bit inside that 64-bit word
                ColSliceWord[idx] = sliceWord;
                ColBitMask[idx] = 1UL << bit;
            }
        }

        // ---------------- Scratch pooling ----------------
        private static readonly ConcurrentBag<SectionBuildScratch> _scratchPool = new();
        private static SectionBuildScratch RentScratch()
        {
            if (_scratchPool.TryTake(out var sc)) { sc.Reset(); return sc; }
            return new SectionBuildScratch();
        }
        private static void ReturnScratch(SectionBuildScratch sc)
        {
            if (sc == null) return;
            _scratchPool.Add(sc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SectionBuildScratch GetScratch(ChunkSection sec)
        {
            return sec.BuildScratch as SectionBuildScratch ?? (SectionBuildScratch)(sec.BuildScratch = RentScratch());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void TrackDistinct(SectionBuildScratch sc, ushort id)
        {
            if (id == AIR) return;
            int dc = sc.DistinctCount;
            for (int i = 0; i < dc; i++) if (sc.Distinct[i] == id) return;
            if (dc < sc.Distinct.Length) sc.Distinct[dc] = id;
            sc.DistinctCount = dc < sc.Distinct.Length ? dc + 1 : dc; // clamp when full (we do not expand here)
        }

        // Public helper to convert a uniform section into scratch runs (used when partial replacement rules break uniformity)
        public static void ConvertUniformSectionToScratch(ChunkSection sec)
        {
            if (sec == null || sec.Kind != ChunkSection.RepresentationKind.Uniform) return;
            var scratch = GetScratch(sec);
            ushort id = sec.UniformBlockId;
            for (int z = 0; z < S; z++)
            for (int x = 0; x < S; x++)
            {
                int ci = z * S + x; ref var col = ref scratch.Columns[ci];
                col.RunCount = 1; col.Id0 = id; col.Y0Start = 0; col.Y0End = 15;
            }
            TrackDistinct(scratch, id);
            scratch.AnyNonAir = true;
            sec.Kind = ChunkSection.RepresentationKind.Empty; // will be decided at finalize
            sec.MetadataBuilt = false;
            sec.UniformBlockId = 0;
            sec.CompletelyFull = false;
        }

        // New lightweight run append used during generation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddRun(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (blockId == AIR || yEnd < yStart) return;
            var scratch = GetScratch(sec);
            scratch.AnyNonAir = true;
            int ci = localZ * S + localX;
            ref var col = ref scratch.Columns[ci];

            if (col.RunCount == 255)
            {
                var arr = col.Escalated ??= new ushort[S];
                for (int y = yStart; y <= yEnd; y++) arr[y] = blockId;
                TrackDistinct(scratch, blockId);
                return;
            }
            if (col.RunCount == 0)
            {
                col.Id0 = blockId; col.Y0Start = (byte)yStart; col.Y0End = (byte)yEnd; col.RunCount = 1;
                TrackDistinct(scratch, blockId); return;
            }
            if (col.RunCount == 1)
            {
                // merge or add second
                if (blockId == col.Id0 && yStart == col.Y0End + 1)
                { col.Y0End = (byte)yEnd; return; }
                if (yStart > col.Y0End)
                { col.Id1 = blockId; col.Y1Start = (byte)yStart; col.Y1End = (byte)yEnd; col.RunCount = 2; TrackDistinct(scratch, blockId); return; }
            }
            if (col.RunCount == 2)
            {
                // try extend last
                if (blockId == col.Id1 && yStart == col.Y1End + 1) { col.Y1End = (byte)yEnd; return; }
            }
            // escalate (rare path)
            scratch.AnyEscalated = true; var full = col.Escalated ?? new ushort[S];
            // replay existing (only once on first escalation)
            if (col.RunCount != 255)
            {
                if (col.RunCount >= 1)
                {
                    for (int y = col.Y0Start; y <= col.Y0End; y++) full[y] = col.Id0;
                    if (col.RunCount == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) full[y] = col.Id1;
                }
            }
            for (int y = yStart; y <= yEnd; y++) full[y] = blockId;
            col.Escalated = full; col.RunCount = 255; TrackDistinct(scratch, blockId);
        }

        // Apply replacement rule against scratch columns for a vertical slice [lyStart, lyEnd] (inclusive)
        // Optimized to avoid per-column escalation when a run is fully contained in the slice even if not full section cover.
        public static void ApplyReplacement(ChunkSection sec, int lyStart, int lyEnd, bool fullCover, Func<ushort, BaseBlockType> baseTypeGetter, Func<ushort, BaseBlockType, bool> predicate, ushort replacementId)
        {
            if (sec == null) return;
            var scratch = GetScratch(sec);
            int distinctCount = scratch.DistinctCount;
            if (distinctCount == 0) return;

            Span<ushort> ids = stackalloc ushort[distinctCount];
            Span<bool> targeted = stackalloc bool[distinctCount];
            bool anyTarget = false;
            for (int i = 0; i < distinctCount; i++)
            {
                ushort id = scratch.Distinct[i];
                ids[i] = id;
                if (id == replacementId) { targeted[i] = false; continue; }
                var bt = baseTypeGetter(id);
                bool t = predicate(id, bt);
                targeted[i] = t;
                if (t) anyTarget = true;
            }
            if (!anyTarget) return;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IdMatches(ushort id, Span<ushort> idsLocal, Span<bool> targetedLocal)
            {
                for (int i = 0; i < idsLocal.Length; i++)
                {
                    if (idsLocal[i] == id) return targetedLocal[i];
                }
                return false;
            }

            bool anyChange = false;
            if (fullCover)
            {
                for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                {
                    ref var col = ref scratch.Columns[z * S + x];
                    byte rc = col.RunCount; if (rc == 0) continue;
                    if (rc == 255)
                    {
                        var arr = col.Escalated;
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y]; if (id == AIR || id == replacementId) continue;
                            if (IdMatches(id, ids, targeted)) { arr[y] = replacementId; anyChange = true; }
                        }
                        continue;
                    }
                    if (rc >= 1 && col.Id0 != replacementId && IdMatches(col.Id0, ids, targeted)) { col.Id0 = replacementId; anyChange = true; }
                    if (rc == 2 && col.Id1 != replacementId && IdMatches(col.Id1, ids, targeted)) { col.Id1 = replacementId; anyChange = true; }
                }
                if (anyChange) scratch.DistinctDirty = true; return;
            }

            for (int z = 0; z < S; z++)
            for (int x = 0; x < S; x++)
            {
                ref var col = ref scratch.Columns[z * S + x];
                byte rc = col.RunCount; if (rc == 0) continue;
                if (rc == 255)
                {
                    var arr = col.Escalated;
                    for (int y = lyStart; y <= lyEnd; y++)
                    {
                        ushort id = arr[y]; if (id == AIR || id == replacementId) continue;
                        if (IdMatches(id, ids, targeted)) { arr[y] = replacementId; anyChange = true; }
                    }
                    continue;
                }
                if (rc >= 1 && RangesOverlap(col.Y0Start, col.Y0End, lyStart, lyEnd) && col.Id0 != replacementId && IdMatches(col.Id0, ids, targeted))
                {
                    bool runContained = lyStart <= col.Y0Start && lyEnd >= col.Y0End;
                    if (runContained)
                    {
                        col.Id0 = replacementId; anyChange = true;
                    }
                    else
                    {
                        var arr = col.Escalated ?? new ushort[S];
                        if (col.Escalated == null)
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                            if (rc == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                        }
                        int ys = Math.Max(col.Y0Start, lyStart); int ye = Math.Min(col.Y0End, lyEnd);
                        for (int y = ys; y <= ye; y++) arr[y] = replacementId;
                        col.Escalated = arr; col.RunCount = 255; anyChange = true; continue;
                    }
                }
                if (rc == 2 && RangesOverlap(col.Y1Start, col.Y1End, lyStart, lyEnd) && col.Id1 != replacementId && IdMatches(col.Id1, ids, targeted))
                {
                    bool runContained = lyStart <= col.Y1Start && lyEnd >= col.Y1End;
                    if (runContained)
                    {
                        col.Id1 = replacementId; anyChange = true;
                    }
                    else
                    {
                        var arr = col.Escalated ?? new ushort[S];
                        if (col.Escalated == null)
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                            for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                        }
                        int ys = Math.Max(col.Y1Start, lyStart); int ye = Math.Min(col.Y1End, lyEnd);
                        for (int y = ys; y <= ye; y++) arr[y] = replacementId;
                        col.Escalated = arr; col.RunCount = 255; anyChange = true;
                    }
                }
            }
            if (anyChange) scratch.DistinctDirty = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RangesOverlap(int a0, int a1, int b0, int b1) => a0 <= b1 && b0 <= a1;

        // SIMD / HW popcount batch (falls back to BitOperations) over 4096-bit occupancy array
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PopCountBatch(ulong[] occ)
        {
            int total = 0;
            if (Popcnt.X64.IsSupported)
            {
                for (int i = 0; i < occ.Length; i++) total += (int)Popcnt.X64.PopCount(occ[i]);
                return total;
            }
            for (int i = 0; i < occ.Length; i++) total += BitOperations.PopCount(occ[i]);
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

        // Finalize after generation + replacements
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
                if (scratch != null) { sec.BuildScratch = null; ReturnScratch(scratch); }
                return;
            }

            // --- Fast path: no escalated columns (RunCount only 0/1/2) ---
            if (!scratch.AnyEscalated)
            {
                // Rebuild distinct if dirty via runs only (no per-voxel scan)
                if (scratch.DistinctDirty)
                {
                    Span<ushort> tmp = stackalloc ushort[8]; int count = 0;
                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.Columns[ci];
                        byte rc = col.RunCount; if (rc == 0) continue;
                        if (rc >= 1 && col.Id0 != AIR)
                        {
                            bool f = false; for (int i = 0; i < count; i++) if (tmp[i] == col.Id0) { f = true; break; }
                            if (!f && count < 8) tmp[count++] = col.Id0;
                        }
                        if (rc == 2 && col.Id1 != AIR)
                        {
                            bool f = false; for (int i = 0; i < count; i++) if (tmp[i] == col.Id1) { f = true; break; }
                            if (!f && count < 8) tmp[count++] = col.Id1;
                        }
                    }
                    scratch.DistinctCount = count;
                    for (int i = 0; i < count; i++) scratch.Distinct[i] = tmp[i];
                    scratch.DistinctDirty = false;
                }

                sec.VoxelCount = S * S * S;
                // Build occupancy using precomputed word/mask tables (optimization 3.1)
                ulong[] occ = sec.OccupancyBits = new ulong[64];
                int nonAir = 0; int adjY = 0; int adjX = 0; int adjZ = 0;
                bool boundsInit = false;
                byte minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;

                // First pass: fill occupancy & vertical adjacency & bounds & nonAir
                for (int z = 0; z < S; z++)
                {
                    int zBase = z * S;
                    for (int x = 0; x < S; x++)
                    {
                        ref var col = ref scratch.Columns[zBase + x];
                        byte rc = col.RunCount; if (rc == 0) continue;

                        if (rc >= 1)
                        {
                            int len0 = col.Y0End - col.Y0Start + 1;
                            nonAir += len0;
                            adjY += len0 - 1; // internal vertical adjacency within first run
                            for (int y = col.Y0Start; y <= col.Y0End; y++)
                            {
                                int idx = z * S + x; // 0..255
                                int word = (y << 2) + ColSliceWord[idx];
                                occ[word] |= ColBitMask[idx];
                                if (!boundsInit)
                                { minX = maxX = (byte)x; minZ = maxZ = (byte)z; minY = maxY = (byte)y; boundsInit = true; }
                                else
                                {
                                    if (x < minX) minX = (byte)x; if (x > maxX) maxX = (byte)x;
                                    if (z < minZ) minZ = (byte)z; if (z > maxZ) maxZ = (byte)z;
                                    if (y < minY) minY = (byte)y; if (y > maxY) maxY = (byte)y;
                                }
                            }
                        }
                        if (rc == 2)
                        {
                            int len1 = col.Y1End - col.Y1Start + 1;
                            nonAir += len1;
                            adjY += len1 - 1;
                            for (int y = col.Y1Start; y <= col.Y1End; y++)
                            {
                                int idx = z * S + x;
                                int word = (y << 2) + ColSliceWord[idx];
                                occ[word] |= ColBitMask[idx];
                                if (!boundsInit)
                                { minX = maxX = (byte)x; minZ = maxZ = (byte)z; minY = maxY = (byte)y; boundsInit = true; }
                                else
                                {
                                    if (x < minX) minX = (byte)x; if (x > maxX) maxX = (byte)x;
                                    if (z < minZ) minZ = (byte)z; if (z > maxZ) maxZ = (byte)z;
                                    if (y < minY) minY = (byte)y; if (y > maxY) maxY = (byte)y;
                                }
                            }
                        }
                    }
                }

                sec.NonAirCount = nonAir;
                if (!boundsInit)
                {
                    sec.Kind = ChunkSection.RepresentationKind.Empty; sec.IsAllAir = true; sec.MetadataBuilt = true; ReturnScratch(scratch); sec.BuildScratch = null; return;
                }
                sec.HasBounds = true; sec.MinLX = minX; sec.MaxLX = maxX; sec.MinLY = minY; sec.MaxLY = maxY; sec.MinLZ = minZ; sec.MaxLZ = maxZ;

                // Horizontal adjacency via run overlap (optimization 3.2)
                for (int z = 0; z < S; z++)
                {
                    int zBase = z * S;
                    for (int x = 0; x < S - 1; x++)
                    {
                        ref var a = ref scratch.Columns[zBase + x];
                        ref var b = ref scratch.Columns[zBase + x + 1];
                        if (a.RunCount == 0 || b.RunCount == 0) continue;
                        if (a.RunCount == 255 || b.RunCount == 255) { goto FALLBACK_FULL; } // safety: escalated unexpected
                        // Compare runs (up to 2 each)
                        // a run0 vs b run0
                        if (a.RunCount >= 1 && b.RunCount >= 1) adjX += OverlapLen(a.Y0Start, a.Y0End, b.Y0Start, b.Y0End);
                        if (a.RunCount >= 1 && b.RunCount == 2) adjX += OverlapLen(a.Y0Start, a.Y0End, b.Y1Start, b.Y1End);
                        if (a.RunCount == 2 && b.RunCount >= 1) adjX += OverlapLen(a.Y1Start, a.Y1End, b.Y0Start, b.Y0End);
                        if (a.RunCount == 2 && b.RunCount == 2) adjX += OverlapLen(a.Y1Start, a.Y1End, b.Y1Start, b.Y1End);
                    }
                }
                for (int z = 0; z < S - 1; z++)
                {
                    int zBase = z * S;
                    int zBaseNext = (z + 1) * S;
                    for (int x = 0; x < S; x++)
                    {
                        ref var a = ref scratch.Columns[zBase + x];
                        ref var b = ref scratch.Columns[zBaseNext + x];
                        if (a.RunCount == 0 || b.RunCount == 0) continue;
                        if (a.RunCount == 255 || b.RunCount == 255) { goto FALLBACK_FULL; }
                        if (a.RunCount >= 1 && b.RunCount >= 1) adjZ += OverlapLen(a.Y0Start, a.Y0End, b.Y0Start, b.Y0End);
                        if (a.RunCount >= 1 && b.RunCount == 2) adjZ += OverlapLen(a.Y0Start, a.Y0End, b.Y1Start, b.Y1End);
                        if (a.RunCount == 2 && b.RunCount >= 1) adjZ += OverlapLen(a.Y1Start, a.Y1End, b.Y0Start, b.Y0End);
                        if (a.RunCount == 2 && b.RunCount == 2) adjZ += OverlapLen(a.Y1Start, a.Y1End, b.Y1Start, b.Y1End);
                    }
                }

                int exposure = 6 * nonAir - 2 * (adjX + adjY + adjZ);
                sec.InternalExposure = exposure;

                // Representation decision (reuse original logic but skip popcount path)
                if (nonAir == S * S * S && scratch.DistinctCount == 1)
                {
                    sec.Kind = ChunkSection.RepresentationKind.Uniform;
                    sec.UniformBlockId = scratch.Distinct[0];
                    sec.IsAllAir = false; sec.CompletelyFull = true;
                }
                else if (nonAir <= 128)
                {
                    int[] idx = new int[nonAir]; ushort[] blocks = new ushort[nonAir];
                    int p = 0; ushort singleId = scratch.DistinctCount == 1 ? scratch.Distinct[0] : (ushort)0;
                    // Enumerate bits via occupancy words (already built)
                    for (int word = 0; word < occ.Length; word++)
                    {
                        ulong w = occ[word];
                        while (w != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(w); int li = (word << 6) + bit; idx[p] = li; blocks[p] = singleId != 0 ? singleId : ResolveBlockId(ref scratch.Columns[(li & 255)], li); p++; w &= w - 1;
                        }
                    }
                    sec.Kind = ChunkSection.RepresentationKind.Sparse;
                    sec.SparseIndices = idx; sec.SparseBlocks = blocks; sec.IsAllAir = false;
                }
                else
                {
                    if (scratch.DistinctCount == 1)
                    {
                        sec.Kind = ChunkSection.RepresentationKind.Packed;
                        sec.Palette = new List<ushort> { AIR, scratch.Distinct[0] };
                        sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { scratch.Distinct[0], 1 } };
                        sec.BitsPerIndex = 1; sec.BitData = new uint[(4096 + 31) / 32];
                        for (int word = 0; word < occ.Length; word++)
                        {
                            ulong w = occ[word];
                            while (w != 0) { int bit = BitOperations.TrailingZeroCount(w); int li = (word << 6) + bit; int bw = li >> 5; int bo = li & 31; sec.BitData[bw] |= 1u << bo; w &= w - 1; }
                        }
                        sec.IsAllAir = false;
                    }
                    else
                    {
                        sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                        var dense = sec.ExpandedDense = new ushort[4096];
                        for (int z = 0; z < S; z++)
                        for (int x = 0; x < S; x++)
                        {
                            ref var col = ref scratch.Columns[z * S + x];
                            byte rc = col.RunCount; if (rc == 0) continue;
                            if (rc >= 1) for (int y = col.Y0Start; y <= col.Y0End; y++) dense[(y * 256) + (z * S) + x] = col.Id0;
                            if (rc == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) dense[(y * 256) + (z * S) + x] = col.Id1;
                        }
                        sec.IsAllAir = false;
                    }
                }
                sec.MetadataBuilt = true;
                sec.BuildScratch = null; ReturnScratch(scratch); // release back to pool
                return;
            }

        FALLBACK_FULL:
            // --- Original path (with escalated columns or fallback) ---
            if (scratch.DistinctDirty)
            {
                Span<ushort> tmp2 = stackalloc ushort[8]; int count2 = 0;
                for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                {
                    ref var col = ref scratch.Columns[z * S + x];
                    byte rc = col.RunCount; if (rc == 0) continue;
                    if (rc == 255)
                    {
                        var arr = col.Escalated;
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y]; if (id == AIR) continue; bool found = false; for (int i = 0; i < count2; i++) if (tmp2[i] == id) { found = true; break; } if (!found && count2 < 8) tmp2[count2++] = id;
                        }
                        continue;
                    }
                    if (rc >= 1 && col.Id0 != AIR)
                    { bool f = false; for (int i = 0; i < count2; i++) if (tmp2[i] == col.Id0) { f = true; break; } if (!f && count2 < 8) tmp2[count2++] = col.Id0; }
                    if (rc == 2 && col.Id1 != AIR)
                    { bool f = false; for (int i = 0; i < count2; i++) if (tmp2[i] == col.Id1) { f = true; break; } if (!f && count2 < 8) tmp2[count2++] = col.Id1; }
                }
                scratch.DistinctCount = count2;
                for (int i = 0; i < count2; i++) scratch.Distinct[i] = tmp2[i];
                scratch.DistinctDirty = false;
            }
            sec.VoxelCount = S * S * S;
            ulong[] occFull = sec.OccupancyBits = new ulong[64]; // 4096 bits
            bool boundsInitFull = false;
            byte minXf = 0, maxXf = 0, minYf = 0, maxYf = 0, minZf = 0, maxZf = 0;
            int adjXf = 0, adjYf = 0, adjZf = 0;

            // Build occupancy bits; compute vertical adjacency (adjY) using run lengths; avoid per-voxel nonAir increments (will popcount later)
            for (int z = 0; z < S; z++)
            {
                int zOffset = z * S;
                for (int x = 0; x < S; x++)
                {
                    ref var col = ref scratch.Columns[zOffset + x];
                    byte rc = col.RunCount; if (rc == 0) continue;
                    if (rc == 255)
                    {
                        var arr = col.Escalated;
                        ushort prev = 0;
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y]; if (id == AIR) { prev = 0; continue; }
                            int li = y * 256 + z * S + x; occFull[li >> 6] |= 1UL << (li & 63);
                            if (!boundsInitFull) { minXf = maxXf = (byte)x; minYf = maxYf = (byte)y; minZf = maxZf = (byte)z; boundsInitFull = true; } else { if (x < minXf) minXf = (byte)x; if (x > maxXf) maxXf = (byte)x; if (y < minYf) minYf = (byte)y; if (y > maxYf) maxYf = (byte)y; if (z < minZf) minZf = (byte)z; if (z > maxZf) maxZf = (byte)z; }
                            if (prev != 0) adjYf++; prev = id;
                        }
                        continue;
                    }
                    if (rc >= 1)
                    {
                        int y0s = col.Y0Start; int y0e = col.Y0End; int len0 = y0e - y0s + 1;
                        for (int y = y0s; y <= y0e; y++)
                        {
                            int li = y * 256 + z * S + x; occFull[li >> 6] |= 1UL << (li & 63);
                            if (!boundsInitFull) { minXf = maxXf = (byte)x; minYf = maxYf = (byte)y; minZf = maxZf = (byte)z; boundsInitFull = true; } else { if (x < minXf) minXf = (byte)x; if (x > maxXf) maxXf = (byte)x; if (y < minYf) minYf = (byte)y; if (y > maxYf) maxYf = (byte)y; if (z < minZf) minZf = (byte)z; if (z > maxZf) maxZf = (byte)z; }
                        }
                        adjYf += len0 - 1;
                    }
                    if (rc == 2)
                    {
                        int y1s = col.Y1Start; int y1e = col.Y1End; int len1 = y1e - y1s + 1;
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                        {
                            int li = y * 256 + z * S + x; occFull[li >> 6] |= 1UL << (li & 63);
                            if (!boundsInitFull) { minXf = maxXf = (byte)x; minYf = maxYf = (byte)y; minZf = maxZf = (byte)z; boundsInitFull = true; } else { if (x < minXf) minXf = (byte)x; if (x > maxXf) maxXf = (byte)x; if (y < minYf) minYf = (byte)y; if (y > maxYf) maxYf = (byte)y; if (z < minZf) minZf = (byte)z; if (z > maxZf) maxZf = (byte)z; }
                        }
                        adjYf += len1 - 1;
                    }
                }
            }

            int nonAirFull = PopCountBatch(occFull); // HW accelerated
            sec.NonAirCount = nonAirFull;
            if (!boundsInitFull)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty; sec.IsAllAir = true; sec.MetadataBuilt = true; ReturnScratch(scratch); sec.BuildScratch = null; return;
            }
            sec.HasBounds = true; sec.MinLX = minXf; sec.MaxLX = maxXf; sec.MinLY = minYf; sec.MaxLY = maxYf; sec.MinLZ = minZf; sec.MaxLZ = maxZf;

            // X and Z adjacency via occupancy scan of neighbors (bitwise)
            for (int y = 0; y < S; y++)
            {
                int yBase = y * 256;
                for (int z = 0; z < S; z++)
                {
                    int rowBase = yBase + z * S;
                    int liRowStart = rowBase;
                    bool prev = false;
                    for (int x = 0; x < S; x++)
                    {
                        int li = liRowStart + x; bool filled = (occFull[li >> 6] & (1UL << (li & 63))) != 0; if (filled && prev) adjXf++; prev = filled;
                        if (z > 0 && filled)
                        {
                            int backLi = li - S; if ((occFull[backLi >> 6] & (1UL << (backLi & 63))) != 0) adjZf++; // Z adjacency
                        }
                    }
                }
            }

            int exposureFull = 6 * nonAirFull - 2 * (adjXf + adjYf + adjZf);
            sec.InternalExposure = exposureFull;

            // Decide representation (original logic)
            if (nonAirFull == S * S * S && scratch.DistinctCount == 1)
            {
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = scratch.Distinct[0];
                sec.IsAllAir = false; sec.CompletelyFull = true;
            }
            else if (nonAirFull <= 128)
            {
                int[] idx = new int[nonAirFull]; ushort[] blocks = new ushort[nonAirFull];
                int p = 0; ushort singleId = scratch.DistinctCount == 1 ? scratch.Distinct[0] : (ushort)0;
                for (int word = 0; word < occFull.Length; word++)
                {
                    ulong w = occFull[word];
                    while (w != 0) { int bit = BitOperations.TrailingZeroCount(w); int li = (word << 6) + bit; idx[p] = li; blocks[p] = singleId != 0 ? singleId : ResolveBlockId(ref scratch.Columns[(li & 255)], li); p++; w &= w - 1; }
                }
                sec.Kind = ChunkSection.RepresentationKind.Sparse;
                sec.SparseIndices = idx; sec.SparseBlocks = blocks; sec.IsAllAir = false;
            }
            else
            {
                if (scratch.DistinctCount == 1)
                {
                    sec.Kind = ChunkSection.RepresentationKind.Packed;
                    sec.Palette = new List<ushort> { AIR, scratch.Distinct[0] };
                    sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { scratch.Distinct[0], 1 } };
                    sec.BitsPerIndex = 1; sec.BitData = new uint[(4096 + 31) / 32];
                    for (int word = 0; word < occFull.Length; word++)
                    {
                        ulong w = occFull[word];
                        while (w != 0) { int bit = BitOperations.TrailingZeroCount(w); int li = (word << 6) + bit; int bw = li >> 5; int bo = li & 31; sec.BitData[bw] |= 1u << bo; w &= w - 1; }
                    }
                    sec.IsAllAir = false;
                }
                else
                {
                    sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                    var dense = sec.ExpandedDense = new ushort[4096];
                    for (int z = 0; z < S; z++)
                    for (int x = 0; x < S; x++)
                    {
                        ref var col = ref scratch.Columns[z * S + x];
                        byte rc = col.RunCount; if (rc == 0) continue;
                        if (rc == 255) { var arr = col.Escalated; for (int y = 0; y < S; y++) { ushort id = arr[y]; if (id != AIR) dense[(y * 256) + (z * S) + x] = id; } }
                        else
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++) dense[(y * 256) + (z * S) + x] = col.Id0;
                            if (rc == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) dense[(y * 256) + (z * S) + x] = col.Id1;
                        }
                    }
                    sec.IsAllAir = false;
                }
            }
            sec.MetadataBuilt = true;
            sec.BuildScratch = null; ReturnScratch(scratch);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ResolveBlockId(ref ColumnData col, int li)
        {
            int y = li / 256; // since li = y*256 + baseXZ
            if (col.RunCount == 255) { return col.Escalated[y]; }
            if (col.RunCount == 0) return AIR;
            if (col.RunCount >= 1 && y >= col.Y0Start && y <= col.Y0End) return col.Id0;
            if (col.RunCount == 2 && y >= col.Y1Start && y <= col.Y1End) return col.Id1;
            return AIR;
        }
    }
}

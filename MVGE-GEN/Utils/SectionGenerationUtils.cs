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
        private static readonly ushort[,] MaskRangeLut = BuildMaskRangeLut();
        private static ushort[,] BuildMaskRangeLut()
        {
            var t = new ushort[16, 16];
            for (int i = 0; i < 16; i++) for (int j = i; j < 16; j++) t[i, j] = (ushort)(((1 << (j - i + 1)) - 1) << i);
            return t;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort MaskRangeLutGet(int s, int e) => MaskRangeLut[s, e];

        // -------------------------------------------------------------------------------------------------
        // EnsureScratch: Returns a writable SectionBuildScratch for the given section.
        // Guarantees that the returned instance is initialized and ready for column writes.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SectionBuildScratch EnsureScratch(ChunkSection sec)
        {
            // Reuse existing helper to obtain a writable scratch. Assumes generation-time access.
            return GetScratch(sec);
        }

        // -------------------------------------------------------------------------------------------------
        // DirectSetColumnRun1: Specialized single-run fast path using precomputed scratch and ci.
        // Overwrites a column with a single run (id, ys..ye).
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DirectSetColumnRun1(
            SectionBuildScratch scratch, int ci,
            ushort id, byte ys, byte ye)
        {
            if (id == AIR || ys > ye)
            {
                // Treat as empty write – clear minimal fields for consistency.
                ref var emptyCol = ref scratch.GetWritableColumn(ci);
                emptyCol.RunCount = 0;
                emptyCol.Id0 = 0; emptyCol.Id1 = 0;
                emptyCol.Y0Start = emptyCol.Y0End = emptyCol.Y1Start = emptyCol.Y1End = 0;
                emptyCol.OccMask = 0;
                emptyCol.NonAir = 0;
                emptyCol.AdjY = 0;
                emptyCol.Escalated = null;
                return;
            }

            ushort occMask = MaskRangeLutGet(ys, ye);
            int len = (ye - ys + 1);
            byte nonAir = (byte)len;
            byte adjY = (byte)(len - 1);

            ref var col = ref scratch.GetWritableColumn(ci);
            col.RunCount = 1;
            col.Id0 = id;
            col.Y0Start = ys; col.Y0End = ye;
            col.Id1 = 0;
            col.Y1Start = 0; col.Y1End = 0;
            col.OccMask = occMask;
            col.NonAir = nonAir;
            col.AdjY = adjY;
            col.Escalated = null;

            int w = ci >> 6, b = ci & 63;
            ulong bit = 1UL << b;
            ulong prev = scratch.NonEmptyColumnBits[w];
            if ((prev & bit) == 0) scratch.NonEmptyCount++;
            scratch.NonEmptyColumnBits[w] = prev | bit;
            scratch.AnyNonAir = true;
            scratch.DistinctDirty = true;
        }

        // -------------------------------------------------------------------------------------------------
        // DirectSetColumnRuns: Accepts precomputed scratch and column index with byte Y ranges.
        // Writes 0, 1, or 2 runs directly without re-fetching scratch or recomputing ci.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DirectSetColumnRuns(
            SectionBuildScratch scratch, int ci,
            ushort id0, byte y0s, byte y0e,
            ushort id1 = 0, byte y1s = 0, byte y1e = 0)
        {
            if (id0 == AIR || y0s > y0e)
            {
                // Empty column
                ref var col = ref scratch.GetWritableColumn(ci);
                col.RunCount = 0;
                col.Id0 = 0; col.Id1 = 0;
                col.Y0Start = col.Y0End = col.Y1Start = col.Y1End = 0;
                col.OccMask = 0;
                col.NonAir = 0;
                col.AdjY = 0;
                col.Escalated = null;
                return;
            }

            bool single = (id1 == 0 || id1 == AIR || y1s > y1e);
            if (single)
            {
                DirectSetColumnRun1(scratch, ci, id0, y0s, y0e);
                return;
            }

            ushort occ0 = MaskRangeLutGet(y0s, y0e);
            ushort occ1 = MaskRangeLutGet(y1s, y1e);
            ushort occMask = (ushort)(occ0 | occ1);
            int lenA = (y0e - y0s + 1);
            int lenB = (y1e - y1s + 1);
            byte nonAir = (byte)(lenA + lenB);
            bool contiguous = y1s == (byte)(y0e + 1);
            byte adjY = (byte)((lenA - 1) + (lenB - 1) + (contiguous ? 1 : 0));

            ref var c = ref scratch.GetWritableColumn(ci);
            c.RunCount = 2;
            c.Id0 = id0;
            c.Y0Start = y0s; c.Y0End = y0e;
            c.Id1 = id1;
            c.Y1Start = y1s; c.Y1End = y1e;
            c.OccMask = occMask;
            c.NonAir = nonAir;
            c.AdjY = adjY;
            c.Escalated = null;

            int w = ci >> 6, b = ci & 63;
            ulong bit = 1UL << b;
            ulong prev = scratch.NonEmptyColumnBits[w];
            if ((prev & bit) == 0) scratch.NonEmptyCount++;
            scratch.NonEmptyColumnBits[w] = prev | bit;
            scratch.AnyNonAir = true;
            scratch.DistinctDirty = true;
        }

        // -------------------------------------------------------------------------------------------------
        // DirectSetRowSingleRun16: Bulk single-run writer for a given Z row.
        // Applies the same (id, yStart..yEnd) to all columns at (x,z) where xMask has a bit set.
        // xMaskBits16[0] holds a 16-bit mask (LSB -> x=0). No-op when mask is zero.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DirectSetRowSingleRun16(
            SectionBuildScratch scratch, int z,
            ushort id, byte yStart, byte yEnd,
            in ReadOnlySpan<ushort> xMaskBits16)
        {
            if (id == AIR || yStart > yEnd) return;
            if (xMaskBits16.IsEmpty) return;
            ushort xMask = xMaskBits16[0];
            if (xMask == 0) return;

            ushort occMask = MaskRangeLutGet(yStart, yEnd);
            int len = (yEnd - yStart + 1);
            byte nonAir = (byte)len;
            byte adjY = (byte)(len - 1);

            int baseCi = (z << 4); // z * 16
            int w = baseCi >> 6;   // row is always contained within one 64-bit word
            int b0 = baseCi & 63;

            // Column writes
            ushort mask = xMask;
            while (mask != 0)
            {
                int x = BitOperations.TrailingZeroCount(mask);
                mask = (ushort)(mask & (mask - 1));
                int ci = baseCi + x;
                ref var col = ref scratch.GetWritableColumn(ci);
                col.RunCount = 1;
                col.Id0 = id;
                col.Y0Start = yStart; col.Y0End = yEnd;
                col.Id1 = 0;
                col.Y1Start = 0; col.Y1End = 0;
                col.OccMask = occMask;
                col.NonAir = nonAir;
                col.AdjY = adjY;
                col.Escalated = null;
            }

            // Membership bits in bulk
            ulong prev = scratch.NonEmptyColumnBits[w];
            ulong shifted = ((ulong)xMask) << b0;
            ulong newlyAdded = shifted & ~prev;
            scratch.NonEmptyColumnBits[w] = prev | shifted;
            scratch.NonEmptyCount += BitOperations.PopCount(newlyAdded);
            scratch.AnyNonAir = true;
            scratch.DistinctDirty = true;
        }

        // -------------------------------------------------------------------------------------------------
        // EscalatedFinalizeSection
        // Converts the incremental run‑based scratch (SectionBuildScratch) into a permanent section
        // representation (Uniform, Sparse, Packed, MultiPacked, DenseExpanded or Empty) and builds
        // derived metadata (occupancy masks, face masks, bounds, internal exposure metrics).
        //
        // Fast paths:
        //   1. Cheap palette/id swap path when only IdMapDirty (no structural changes, no scratch).
        //   2. Single pass classification for non‑escalated columns using run metadata only.
        //   3. Special single‑id full‑height column compaction path (Uniform or 1‑bit Packed).
        //   4. Early full‑uniform short‑circuit detection during fused traversal.
        //
        // Fallback path:
        //   When any column escalated (RunCount==255) we rebuild a dense array (O(4096)).
        // -------------------------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GenerationFinalizeSection(ChunkSection sec)
        {
            if (sec == null) return;

            // -----------------------------------
            // 1. Cheap palette / id remap path
            // -----------------------------------
            if (sec.BuildScratch == null && !sec.StructuralDirty && sec.IdMapDirty)
            {
                // Uniform: nothing but id changed.
                if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                {
                    sec.MetadataBuilt = true;
                    sec.IdMapDirty = false;
                    sec.StructuralDirty = false; // early uniform id remap
                    return;
                }

                // Single‑id packed (AIR + single block) – occupancy unchanged. If totally full promote to Uniform.
                if ((sec.Kind == ChunkSection.RepresentationKind.Packed || sec.Kind == ChunkSection.RepresentationKind.MultiPacked) &&
                    sec.Palette != null && sec.Palette.Count <= 2)
                {
                    if (sec.Palette.Count == 2 && sec.NonAirCount == 4096)
                    {
                        // Convert to Uniform for fastest downstream queries.
                        sec.Kind = ChunkSection.RepresentationKind.Uniform;
                        sec.UniformBlockId = sec.Palette[1];
                        sec.CompletelyFull = true;
                        sec.Palette = null;
                        sec.PaletteLookup = null;
                        ReturnBitData(sec.BitData);
                        sec.BitData = null;
                        sec.BitsPerIndex = 0;
                    }
                    sec.MetadataBuilt = true;
                    sec.IdMapDirty = false;
                    sec.StructuralDirty = false;
                    return;
                }

                // Other representations (Sparse / Dense / MultiPacked) – id‑only swap semantics preserved.
                if (sec.Kind == ChunkSection.RepresentationKind.Sparse ||
                    sec.Kind == ChunkSection.RepresentationKind.DenseExpanded ||
                    sec.Kind == ChunkSection.RepresentationKind.MultiPacked)
                {
                    sec.MetadataBuilt = true;
                    sec.IdMapDirty = false;
                    sec.StructuralDirty = false;
                    return;
                }
            }

            // -----------------------------------
            // 2. No scratch: rebuild metadata only if dirty
            // -----------------------------------
            if (sec.BuildScratch == null)
            {
                if (sec.MetadataBuilt && !sec.StructuralDirty && !sec.IdMapDirty)
                {
                    return; // Already valid
                }

                switch (sec.Kind)
                {
                    case ChunkSection.RepresentationKind.Empty:
                        sec.IsAllAir = true;
                        sec.NonAirCount = 0;
                        sec.InternalExposure = 0;
                        sec.HasBounds = false;
                        sec.MetadataBuilt = true;
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        return;

                    case ChunkSection.RepresentationKind.Uniform:
                        BuildMetadataUniform(sec);
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        return;

                    case ChunkSection.RepresentationKind.Sparse:
                        BuildMetadataSparse(sec);
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        return;

                    case ChunkSection.RepresentationKind.DenseExpanded:
                        BuildMetadataDense(sec);
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        return;

                    case ChunkSection.RepresentationKind.Packed:
                    case ChunkSection.RepresentationKind.MultiPacked:
                    default:
                        BuildMetadataPacked(sec);
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        return;
                }
            }

            var scratch = sec.BuildScratch as SectionBuildScratch;

            // -----------------------------------
            // 3. Empty / untouched early exit
            // -----------------------------------
            if (scratch == null || !scratch.AnyNonAir)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty;
                sec.IsAllAir = true;
                sec.MetadataBuilt = true;
                sec.VoxelCount = S * S * S;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;

                if (scratch != null)
                {
                    sec.BuildScratch = null;
                    ReturnScratch(scratch);
                }
                return;
            }

            // -----------------------------------
            // 4. Fast run‑based path (no escalated columns)
            // -----------------------------------
            if (!scratch.AnyEscalated)
            {
                // 4.a Single full‑height single‑id column classification.
                bool singleIdFullHeightCandidate = true;
                ushort candidateId = 0;
                int filledColumns = 0;
                Span<ushort> rowMask = stackalloc ushort[S]; // occupancy per z row (16 bits for x)

                // Track bounds while scanning (saves a second pass)
                byte fMinX = 15, fMaxX = 0, fMinZ = 15, fMaxZ = 0;
                bool any = false;

                for (int z = 0; z < S && singleIdFullHeightCandidate; z++)
                {
                    ushort maskRow = 0;

                    for (int x = 0; x < S; x++)
                    {
                        int ci = z * S + x;
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        byte rc = col.RunCount;

                        if (rc == 0)
                        {
                            continue; // empty column
                        }

                        // Must be exactly one run spanning full height [0..15]
                        if (rc != 1 || col.Y0Start != 0 || col.Y0End != 15)
                        {
                            singleIdFullHeightCandidate = false;
                            break;
                        }

                        ushort id = col.Id0;
                        if (candidateId == 0) candidateId = id; // first id
                        else if (id != candidateId)
                        {
                            singleIdFullHeightCandidate = false;
                            break;
                        }

                        maskRow |= (ushort)(1 << x);
                        filledColumns++;
                    }

                    rowMask[z] = maskRow;
                    if (maskRow != 0)
                    {
                        if (!any)
                        {
                            any = true;
                            fMinZ = fMaxZ = (byte)z;
                        }
                        else
                        {
                            if (z < fMinZ) fMinZ = (byte)z;
                            else if (z > fMaxZ) fMaxZ = (byte)z;
                        }

                        // Leading/trailing set bit indices for min/max X bounds.
                        int first = BitOperations.TrailingZeroCount(maskRow);
                        int last = 15 - BitOperations.LeadingZeroCount((uint)maskRow << 16);
                        if (first < fMinX) fMinX = (byte)first;
                        if (last > fMaxX) fMaxX = (byte)last;
                    }
                }

                if (singleIdFullHeightCandidate && candidateId != 0)
                {
                    // Compute exposure analytically using column adjacency counts.
                    int C = filledColumns;
                    int fastNonAir = C * 16; // 16 voxels per column

                    int adj2D = 0; // adjacency pairs in X and Z planes collapsed into 2D patterns
                    for (int z = 0; z < S; z++)
                    {
                        ushort m = rowMask[z];
                        if (m == 0) continue;
                        adj2D += BitOperations.PopCount((uint)(m & (m << 1))); // X direction
                    }
                    for (int z = 0; z < S - 1; z++)
                    {
                        ushort inter = (ushort)(rowMask[z] & rowMask[z + 1]);
                        if (inter != 0) adj2D += BitOperations.PopCount(inter); // Z direction
                    }

                    int verticalAdj = 15 * C;      // each full column has 15 internal vertical adjacencies
                    int lateralAdj = 16 * adj2D;   // each horizontal adjacency spans 16 vertical cells
                    int exposure = 6 * fastNonAir - 2 * (verticalAdj + lateralAdj);

                    sec.InternalExposure = exposure;
                    sec.NonAirCount = fastNonAir;
                    sec.VoxelCount = S * S * S;

                    if (!any)
                    {
                        // Degenerate (all air) – classify empty.
                        sec.Kind = ChunkSection.RepresentationKind.Empty;
                        sec.IsAllAir = true;
                        sec.MetadataBuilt = true;
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        sec.BuildScratch = null;
                        ReturnScratch(scratch);
                        return;
                    }

                    sec.HasBounds = true;
                    sec.MinLX = fMinX; sec.MaxLX = fMaxX;
                    sec.MinLY = 0; sec.MaxLY = 15;
                    sec.MinLZ = fMinZ; sec.MaxLZ = fMaxZ;

                    if (C == 256)
                    {
                        // Entire section filled with same id – Uniform.
                        sec.Kind = ChunkSection.RepresentationKind.Uniform;
                        sec.UniformBlockId = candidateId;
                        sec.IsAllAir = false;
                        sec.CompletelyFull = true;
                        sec.MetadataBuilt = true;
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        sec.BuildScratch = null;
                        ReturnScratch(scratch);
                        return;
                    }

                    // Partial fill single id -> 1‑bit packed form (palette [AIR, id]).
                    sec.Kind = ChunkSection.RepresentationKind.Packed;
                    sec.Palette = new List<ushort> { AIR, candidateId };
                    sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { candidateId, 1 } };
                    sec.BitsPerIndex = 1;
                    sec.BitData = RentBitData(128); // 4096 bits / 32
                    Array.Clear(sec.BitData, 0, 128);

                    var occ = RentOccupancy();
                    if (filledColumns > 0)
                    {
                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            if (col.RunCount == 1 && col.Y0Start == 0 && col.Y0End == 15 && col.Id0 == candidateId)
                            {
                                WriteColumnMask(occ, ci, col.OccMask);
                                WriteColumnMaskToBitData(sec.BitData, ci, col.OccMask);
                            }
                        }
                    }
                    sec.OpaqueBits = occ;
                    BuildFaceMasks(sec, occ);

                    sec.IsAllAir = false;
                    sec.MetadataBuilt = true;
                    sec.StructuralDirty = false;
                    sec.IdMapDirty = false;
                    sec.BuildScratch = null;
                    ReturnScratch(scratch);
                    return;
                }

                // 4.b Fused traversal: counts + adjacency + distinct ids + bounds.
                FusedNonEscalatedFinalize(sec, scratch);
                return;
            }

            // -----------------------------------
            // 5. Escalated fallback (dense reconstruction)
            // -----------------------------------
            DenseExpandedFinaliseSection(sec, scratch);
        }
    }
}

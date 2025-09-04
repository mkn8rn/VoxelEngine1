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
        // GenerationAddRun
        // Inserts a vertically inclusive span [yStart,yEnd] within 0..15 into a column under ordered
        // non-overlapping generation assumptions. Per column we maintain up to two compact runs
        // before escalating to per‑voxel storage. Metadata is updated incrementally: occupied mask,
        // voxel count and vertical adjacency pairs. Distinct ids are tracked for later representation
        // selection. All unexpected overlap / touching with different ids falls back to escalation.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void GenerationAddRun(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (sec == null || blockId == AIR || (uint)yStart > 15 || (uint)yEnd > 15 || yEnd < yStart)
                return;

            var scratch = GetScratch(sec);

            int ci = (localZ << 4) | localX;            // column index (z * 16 + x)
            ref var col = ref scratch.GetWritableColumn(ci);

            // Fast path: empty column receiving full height span.
            if (col.RunCount == 0 && yStart == 0 && yEnd == 15)
            {
                scratch.AnyNonAir = true;
                sec.StructuralDirty = true;
                col.RunCount = 1;
                col.Id0 = blockId;
                col.Y0Start = 0; col.Y0End = 15;
                col.OccMask = 0xFFFF;
                col.NonAir = 16;
                col.AdjY = 15;
                TrackDistinct(scratch, blockId, ci);
                return;
            }

            // Normal mutation marks (skipped above for already identical full case if early exited later).
            scratch.AnyNonAir = true;
            sec.StructuralDirty = true;

            // Pre-compute span length once for branches that use it.
            int spanLen = yEnd - yStart + 1;

            // Dispatch based on current run state.
            switch (col.RunCount)
            {
                case 0: // First insertion, partial span
                {
                    col.RunCount = 1;
                    col.Id0 = blockId;
                    col.Y0Start = (byte)yStart; col.Y0End = (byte)yEnd;
                    col.OccMask = MaskRange(yStart, yEnd);
                    col.NonAir = (byte)spanLen;
                    col.AdjY = (byte)(spanLen - 1);
                    TrackDistinct(scratch, blockId, ci);
                    return;
                }

                case 1: // One existing run in the column
                {
                    // Potential full-height overwrite (single or previously full first run). Placed early.
                    if (yStart == 0 && yEnd == 15)
                    {
                        if (!(col.Y0Start == 0 && col.Y0End == 15 && col.Id0 == blockId))
                        {
                            col.Id0 = blockId; // overwrite (second run slot is not used here)
                            col.Y0Start = 0; col.Y0End = 15;
                            col.OccMask = 0xFFFF;
                            col.NonAir = 16;
                            col.AdjY = 15;
                            col.Id1 = 0;
                            TrackDistinct(scratch, blockId, ci);
                        }
                        return;
                    }

                    // Same id contiguous extension (only possible above under ordered emission).
                    if (blockId == col.Id0)
                    {
                        int gap = yStart - col.Y0End; // gap==1 contiguous, gap>=2 disjoint, gap<=0 overlap
                        if (gap == 1)
                        {
                            col.Y0End = (byte)yEnd;
                            col.OccMask |= MaskRange(yStart, yEnd);
                            byte add = (byte)spanLen;
                            col.NonAir += add;
                            col.AdjY += add; // internal + bridging equals added length
                            return;
                        }
                        if (gap <= 0)
                        {
                            // Overlap or touching with same id not strictly above current end.
                            goto Escalate;
                        }
                        // gap >= 2 falls through to second run creation below (still same id allowed as separate run under invariant)
                    }

                    // Disjoint above existing run (gap >= 2). Create second run.
                    if (yStart > col.Y0End + 1)
                    {
                        col.RunCount = 2;
                        col.Id1 = blockId;
                        col.Y1Start = (byte)yStart; col.Y1End = (byte)yEnd;
                        col.OccMask |= MaskRange(yStart, yEnd);
                        col.NonAir += (byte)spanLen;
                        col.AdjY += (byte)(spanLen - 1); // No bridging when gap >= 2
                        TrackDistinct(scratch, blockId, ci);
                        return;
                    }

                    // Contiguous but different id or any unexpected relation -> escalation.
                    goto Escalate;
                }

                case 2: // Two compact runs present
                {
                    // Full-height overwrite allowed only if first run already full (0..15) optionally with second appended earlier.
                    if (yStart == 0 && yEnd == 15 && col.Y0Start == 0 && col.Y0End == 15)
                    {
                        if (!(col.RunCount == 1 && col.Id0 == blockId))
                        {
                            col.RunCount = 1;
                            col.Id0 = blockId;
                            col.Y0Start = 0; col.Y0End = 15;
                            col.OccMask = 0xFFFF;
                            col.NonAir = 16;
                            col.AdjY = 15;
                            col.Id1 = 0;
                            TrackDistinct(scratch, blockId, ci);
                        }
                        return;
                    }

                    // Extension of second run (same id, directly above current end).
                    if (blockId == col.Id1)
                    {
                        int gap = yStart - col.Y1End; // gap == 1 contiguous, gap >= 2 disjoint, gap <=0 overlap
                        if (gap == 1)
                        {
                            col.Y1End = (byte)yEnd;
                            col.OccMask |= MaskRange(yStart, yEnd);
                            byte add = (byte)spanLen;
                            col.NonAir += add;
                            col.AdjY += add;
                            return;
                        }
                        if (gap <= 0) goto Escalate; // overlap / touch -> escalate
                        // gap >= 2 -> escalate to per-voxel fast path below (ordered third disjoint run)
                        if (gap >= 2)
                        {
                            // Convert in-place to escalated per-voxel representation reusing existing metadata where possible.
                            var arr = col.Escalated ?? RentEscalatedColumn();
                            if (col.RunCount != 255) // first escalation: materialize existing two runs
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                                for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                            }
                            for (int y = yStart; y <= yEnd; y++) arr[y] = blockId;
                            col.Escalated = arr;

                            ushort segMask = MaskRange(yStart, yEnd);
                            ushort prevMask = col.OccMask;
                            ushort added = (ushort)(segMask & ~prevMask);
                            if (added != 0)
                            {
                                col.OccMask = (ushort)(prevMask | segMask);
                                int addVoxelCount = BitOperations.PopCount(added);
                                col.NonAir += (byte)addVoxelCount;
                                int internalPairs = spanLen - 1; // Only internal vertical pairs (no bridging across gap >=2)
                                col.AdjY += (byte)internalPairs;
                                TrackDistinct(scratch, blockId, ci);
                            }
                            else if (blockId != col.Id0 && blockId != col.Id1)
                            {
                                TrackDistinct(scratch, blockId, ci);
                            }
                            col.RunCount = 255;
                            scratch.AnyEscalated = true;
                            return;
                        }
                    }

                    // Different id contiguous or any unexpected pattern -> escalation.
                    goto Escalate;
                }

                default: // Already escalated or unexpected marker -> general escalation path
                    goto Escalate;
            }

        Escalate:
            EscalatedAddRun(sec, localX, localZ, yStart, yEnd, blockId);
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
                    sec.OccupancyBits = occ;
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
            EscalatedFinaliseSection(sec, scratch);
        }
    }
}

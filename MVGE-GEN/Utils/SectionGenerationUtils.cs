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
        // DirectSetColumnRuns
        // Overwrites a single vertical column (x,z) in the build scratch with 0, 1, or 2 runs and
        // precomputed per‑column metadata. This is a fast generation‑time helper intended for cases
        // where the caller already knows the final runs for a column and wants to bypass incremental
        // merging/escalation logic.
        //
        // Purpose:
        //   * Populate SectionBuildScratch.Columns[ci] directly with compact run data (up to two runs).
        //   * Compute and store derived column metrics:
        //       - OccMask: 16‑bit occupancy mask for Y levels covered by the run(s).
        //       - NonAir: number of non‑air voxels in this column.
        //       - AdjY:   internal vertical adjacency count within the column (sum of (len-1) per run
        //                 plus +1 if two runs are exactly contiguous).
        //   * Update scratch non‑empty bookkeeping bits and mark DistinctDirty so finalize can rebuild
        //     per‑id sets and higher‑level metadata.
        //
        // Inputs:
        //   - (localX, localZ): column coordinates in [0..15].
        //   - id0, y0Start, y0End: first run definition (inclusive Y range). AIR or invalid range => empty column.
        //   - id1, y1Start, y1End: optional second run. Ignored when id1 == 0/AIR or y1Start > y1End.
        //
        // Flow:
        //   1) Guard null section; compute columnIndex = (z << 4) | x; fetch writable scratch via GetScratch.
        //   2) Decide whether we have a single run (id1 is AIR/invalid) or two runs.
        //   3) Compute target values (runCount, occMask, nonAir, adjY) and normalized ids/ranges:
        //        - Empty: id0 is AIR or y0 invalid -> runCount=0, zero all metrics.
        //        - Single run: runCount=1; occMask from MaskRangeLut; nonAir = length; adjY = length-1.
        //        - Two runs: runCount=2; occMask = OR of both ranges; nonAir = lenA + lenB;
        //                    adjY = (lenA-1) + (lenB-1) + (contiguous ? 1 : 0) where contiguous means y1Start == y0End+1.
        //   4) Blast the computed fields into the writable column; clear Escalated (per‑voxel) storage.
        //   5) Membership bookkeeping:
        //        - If runCount>0: set NonEmptyColumnBits bit, increment NonEmptyCount, set AnyNonAir=true,
        //          and mark DistinctDirty=true (distinct ids will be recomputed during finalize).
        //        - Else (empty): zero ids and ranges for consistency.
        //
        // Notes:
        //   * No attempt is made to merge/equalize ids, validate ordering, or track Distinct[] here.
        //     Finalization paths rebuild distinct id lists and section‑level metadata based on the scratch.
        //   * Intended for O(1) column writes during procedural generation; callers should provide sane,
        //     non‑overlapping Y ranges within [0..15].
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DirectSetColumnRuns(
            ChunkSection sec, int localX, int localZ,
            ushort id0, int y0Start, int y0End,
            ushort id1 = 0, int y1Start = 0, int y1End = 0)
        {
            if (sec == null) return;

            int ci = (localZ << 4) | localX;
            var scratch = GetScratch(sec); // always writable at generation time

            bool single = (id1 == 0 || id1 == ChunkSection.AIR || y1Start > y1End);

            // Compute target values
            byte runCount;
            ushort occMask;
            byte nonAir, adjY;
            ushort outId0 = id0, outId1 = 0;
            byte y0s = (byte)y0Start, y0e = (byte)y0End;
            byte y1s = 0, y1e = 0;

            if (id0 == ChunkSection.AIR || y0Start > y0End)
            {
                runCount = 0;
                occMask = 0;
                nonAir = 0;
                adjY = 0;
                outId0 = 0;
            }
            else if (single)
            {
                runCount = 1;
                occMask = MaskRangeLutGet(y0Start, y0End);
                int len = y0End - y0Start + 1;
                nonAir = (byte)len;
                adjY = (byte)(len - 1);
            }
            else
            {
                runCount = 2;
                occMask = (ushort)(MaskRangeLutGet(y0Start, y0End) | MaskRangeLutGet(y1Start, y1End));
                outId1 = id1;
                y1s = (byte)y1Start;
                y1e = (byte)y1End;
                int lenA = y0End - y0Start + 1;
                int lenB = y1End - y1Start + 1;
                nonAir = (byte)(lenA + lenB);
                bool contiguous = (y1Start - y0End - 1) == 0;
                adjY = (byte)((lenA - 1) + (lenB - 1) + (contiguous ? 1 : 0));
            }

            // Acquire writable column and blast values
            ref var col = ref scratch.GetWritableColumn(ci);

            col.RunCount = runCount;
            col.Id0 = outId0;
            col.Y0Start = y0s; col.Y0End = y0e;
            col.Id1 = outId1;
            col.Y1Start = y1s; col.Y1End = y1e;
            col.OccMask = occMask;
            col.NonAir = nonAir;
            col.AdjY = adjY;
            col.Escalated = null;

            // Non-empty membership
            if (runCount > 0)
            {
                int w = ci >> 6, b = ci & 63;
                scratch.NonEmptyColumnBits[w] |= 1UL << b;
                scratch.NonEmptyCount++;
                scratch.AnyNonAir = true;
                scratch.DistinctDirty = true;
            }
            else
            {
                col.Id0 = col.Id1 = 0;
                col.Y0Start = col.Y0End = col.Y1Start = col.Y1End = 0;
            }

            // Note: no equality check, no distinct-ID tracking.
            // FinalizeSection will rebuild Distinct[] via DistinctDirty and mark StructuralDirty as needed.
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

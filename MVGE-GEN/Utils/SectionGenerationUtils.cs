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
using MVGE_INF.Loaders;

namespace MVGE_GEN.Utils
{
    internal static partial class SectionUtils
    {
        private const int S = Section.SECTION_SIZE;          // Section linear dimension (16)
        private const int AIR = Section.AIR;                  // Block id representing empty space
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

        // Helper: ensure transparent boundary face masks are built (only when transparent bits exist) and build EmptyBits/EmptyCount.
        // EmptyBits represent air voxels (bit set => air). This complements opaque + transparent occupancy and is built for
        // all finalized representations (Empty, Sparse, Packed, MultiPacked, DenseExpanded, Uniform partial). Uniform full non‑air
        // sections have no air so EmptyBits remain null (EmptyCount=0). Empty representation allocates a full bitset of air.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FinalizeTransparentAndEmptyMasks(Section sec)
        {
            // Transparent face masks (only once; BuildTransparentFaceMasks clears if already allocated)
            if (sec.TransparentBits != null && sec.TransparentFaceNegXBits == null)
            {
                BuildTransparentFaceMasks(sec, sec.TransparentBits);
            }

            // Build EmptyBits (air) tracking.
            if (sec.Kind == Section.RepresentationKind.Empty || (sec.IsAllAir && sec.VoxelCount == 0))
            {
                // All air section (no voxels or empty representation): allocate full air bitset if not present.
                if (sec.EmptyBits == null)
                {
                    sec.EmptyBits = new ulong[64];
                    for (int i = 0; i < 64; i++) sec.EmptyBits[i] = ulong.MaxValue;
                }
                sec.EmptyCount = Section.VOXELS_PER_SECTION;
                sec.HasAir = true;
                return;
            }

            // Uniform non‑air full volume has no air cells.
            if (sec.Kind == Section.RepresentationKind.Uniform && sec.UniformBlockId != AIR)
            {
                sec.EmptyBits = null; sec.EmptyCount = 0; sec.HasAir = false; return;
            }

            // Compute occupancy union (opaque | transparent). If neither present (should not happen except uninitialized), skip.
            if (sec.OpaqueBits == null && sec.TransparentBits == null)
            {
                // Either all air already handled, or metadata not yet built; leave as-is.
                return;
            }

            ulong[] emptyBits = new ulong[64];
            int emptyCount = 0;
            for (int i = 0; i < 64; i++)
            {
                ulong occ = 0UL;
                if (sec.OpaqueBits != null) occ |= sec.OpaqueBits[i];
                if (sec.TransparentBits != null) occ |= sec.TransparentBits[i];
                ulong empty = ~occ; // bit set => air
                emptyBits[i] = empty;
                emptyCount += BitOperations.PopCount(empty);
            }
            if (emptyCount > 0)
            {
                sec.EmptyBits = emptyBits;
                sec.EmptyCount = emptyCount;
                sec.HasAir = true;
            }
            else
            {
                sec.EmptyBits = null;
                sec.EmptyCount = 0;
                sec.HasAir = false;
            }
        }

        // -------------------------------------------------------------------------------------------------
        // EnsureScratch: Returns a writable SectionBuildScratch for the given section.
        // Guarantees that the returned instance is initialized and ready for column writes.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SectionBuildScratch EnsureScratch(Section sec)
        {
            // Reuse existing helper to obtain a writable scratch. Assumes generation-time access.
            return GetScratch(sec);
        }

        // -------------------------------------------------------------------------------------------------
        // DirectSetColumnRun1: Specialized single-run fast path using precomputed scratch and ci.
        // Overwrites a column with a single run (id, ys..ye).
        // NOTE: At write time we do not distinguish opaque vs transparent; this is resolved during finalize
        // using TerrainLoader.IsOpaque so generation writes remain minimal cost.
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
        // NOTE: As with single-run writer, transparency is resolved later in finalize stage.
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
        // GenerationFinalizeSection 
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
        public static void GenerationFinalizeSection(Section sec)
        {
            if (sec == null) return;

            // 1. Cheap palette / id remap path (no scratch)
            if (sec.BuildScratch == null && !sec.StructuralDirty && sec.IdMapDirty)
            {
                if (sec.Kind == Section.RepresentationKind.Uniform)
                {
                    bool uniformOpaque = TerrainLoader.IsOpaque(sec.UniformBlockId);
                    if (uniformOpaque)
                    {
                        sec.TransparentBits = null; sec.TransparentCount = 0; sec.HasTransparent = false;
                        // Opaque uniform: EmptyBits handled below (none)
                    }
                    else
                    {
                        sec.OpaqueBits = null; sec.OpaqueVoxelCount = 0; // transparent uniform
                        if (sec.TransparentBits == null)
                        {
                            sec.TransparentBits = new ulong[64];
                            for (int i = 0; i < 64; i++) sec.TransparentBits[i] = ulong.MaxValue;
                        }
                        BuildTransparentFaceMasks(sec, sec.TransparentBits); // ensure transparent face masks built here
                    }
                    sec.MetadataBuilt = true;
                    sec.IdMapDirty = false;
                    sec.StructuralDirty = false;
                    FinalizeTransparentAndEmptyMasks(sec);
                    return;
                }

                if ((sec.Kind == Section.RepresentationKind.Packed || sec.Kind == Section.RepresentationKind.MultiPacked) &&
                    sec.Palette != null && sec.Palette.Count <= 2)
                {
                    ushort singleId = (sec.Palette.Count == 2) ? sec.Palette[1] : (ushort)0;
                    bool singleOpaque = singleId != 0 && TerrainLoader.IsOpaque(singleId);
                    if (sec.Palette.Count == 2 && sec.OpaqueVoxelCount == Section.VOXELS_PER_SECTION && singleOpaque)
                    {
                        sec.Kind = Section.RepresentationKind.Uniform;
                        sec.UniformBlockId = singleId;
                        sec.CompletelyFull = true;
                        sec.TransparentBits = null; sec.TransparentCount = 0; sec.HasTransparent = false;
                        sec.Palette = null; sec.PaletteLookup = null;
                        ReturnBitData(sec.BitData); sec.BitData = null; sec.BitsPerIndex = 0;
                    }
                    else if (sec.Palette.Count == 2 && !singleOpaque && sec.OpaqueVoxelCount == Section.VOXELS_PER_SECTION)
                    {
                        sec.Kind = Section.RepresentationKind.Uniform;
                        sec.UniformBlockId = singleId;
                        sec.CompletelyFull = true;
                        sec.OpaqueVoxelCount = 0;
                        sec.TransparentBits = new ulong[64]; for (int i = 0; i < 64; i++) sec.TransparentBits[i] = ulong.MaxValue;
                        sec.TransparentCount = Section.VOXELS_PER_SECTION; sec.HasTransparent = true;
                        BuildTransparentFaceMasks(sec, sec.TransparentBits);
                        sec.Palette = null; sec.PaletteLookup = null;
                        ReturnBitData(sec.BitData); sec.BitData = null; sec.BitsPerIndex = 0;
                    }
                    sec.MetadataBuilt = true;
                    sec.IdMapDirty = false;
                    sec.StructuralDirty = false;
                    FinalizeTransparentAndEmptyMasks(sec);
                    return;
                }

                if (sec.Kind == Section.RepresentationKind.Sparse ||
                    sec.Kind == Section.RepresentationKind.Expanded ||
                    sec.Kind == Section.RepresentationKind.MultiPacked)
                {
                    sec.MetadataBuilt = true;
                    sec.IdMapDirty = false;
                    sec.StructuralDirty = false;
                    FinalizeTransparentAndEmptyMasks(sec);
                    return;
                }
            }

            // 2. No scratch present: rebuild metadata only when dirty (handled by metadata builders which already build transparent masks where applicable).
            if (sec.BuildScratch == null)
            {
                if (sec.MetadataBuilt && !sec.StructuralDirty && !sec.IdMapDirty)
                {
                    FinalizeTransparentAndEmptyMasks(sec);
                    return;
                }

                switch (sec.Kind)
                {
                    case Section.RepresentationKind.Empty:
                        sec.IsAllAir = true;
                        sec.OpaqueVoxelCount = 0;
                        sec.InternalExposure = 0;
                        sec.HasBounds = false;
                        sec.MetadataBuilt = true;
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        FinalizeTransparentAndEmptyMasks(sec);
                        return;
                    case Section.RepresentationKind.Uniform:
                        BuildMetadataUniform(sec);
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        FinalizeTransparentAndEmptyMasks(sec);
                        return;
                    case Section.RepresentationKind.Expanded:
                        BuildMetadataDense(sec);
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        FinalizeTransparentAndEmptyMasks(sec);
                        return;
                    case Section.RepresentationKind.Packed:
                    case Section.RepresentationKind.MultiPacked:
                    default:
                        BuildMetadataPacked(sec);
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        FinalizeTransparentAndEmptyMasks(sec);
                        return;
                }
            }

            var scratch = sec.BuildScratch as SectionBuildScratch;

            // 3. Empty / untouched early exit
            if (scratch == null || !scratch.AnyNonAir)
            {
                sec.Kind = Section.RepresentationKind.Empty;
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
                FinalizeTransparentAndEmptyMasks(sec);
                return;
            }

            // 4. Fast run‑based path (no escalated columns)
            if (!scratch.AnyEscalated)
            {
                // 4.a Single full‑height single‑id column classification.
                bool singleIdFullHeightCandidate = true;
                ushort candidateId = 0;
                int filledColumns = 0;
                Span<ushort> rowMask = stackalloc ushort[S]; // occupancy per z row (16 bits for x)
                byte fMinX = 15, fMaxX = 0, fMinZ = 15, fMaxZ = 0; // bounds
                bool any = false;

                for (int z = 0; z < S && singleIdFullHeightCandidate; z++)
                {
                    ushort maskRow = 0;
                    for (int x = 0; x < S; x++)
                    {
                        int ci = z * S + x;
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        byte rc = col.RunCount;
                        if (rc == 0) continue; // empty column
                        if (rc != 1 || col.Y0Start != 0 || col.Y0End != 15)
                        {
                            singleIdFullHeightCandidate = false;
                            break;
                        }
                        ushort id = col.Id0;
                        if (candidateId == 0) candidateId = id; else if (id != candidateId) { singleIdFullHeightCandidate = false; break; }
                        maskRow |= (ushort)(1 << x);
                        filledColumns++;
                    }
                    rowMask[z] = maskRow;
                    if (maskRow != 0)
                    {
                        if (!any) { any = true; fMinZ = fMaxZ = (byte)z; }
                        else { if (z < fMinZ) fMinZ = (byte)z; else if (z > fMaxZ) fMaxZ = (byte)z; }
                        int first = BitOperations.TrailingZeroCount(maskRow);
                        int last = 15 - BitOperations.LeadingZeroCount((uint)maskRow << 16);
                        if (first < fMinX) fMinX = (byte)first; if (last > fMaxX) fMaxX = (byte)last;
                    }
                }

                if (singleIdFullHeightCandidate && candidateId != 0)
                {
                    int C = filledColumns;
                    int fastNonAir = C * 16;
                    int adj2D = 0;
                    for (int z = 0; z < S; z++) { ushort m = rowMask[z]; if (m != 0) adj2D += BitOperations.PopCount((uint)(m & (m << 1))); }
                    for (int z = 0; z < S - 1; z++) { ushort inter = (ushort)(rowMask[z] & rowMask[z + 1]); if (inter != 0) adj2D += BitOperations.PopCount(inter); }
                    int verticalAdj = 15 * C;
                    int lateralAdj = 16 * adj2D;
                    int exposure = 6 * fastNonAir - 2 * (verticalAdj + lateralAdj);
                    bool candidateOpaque = TerrainLoader.IsOpaque(candidateId);
                    if (!any)
                    {
                        sec.Kind = Section.RepresentationKind.Empty;
                        sec.IsAllAir = true;
                        sec.MetadataBuilt = true;
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        sec.BuildScratch = null; ReturnScratch(scratch);
                        FinalizeTransparentAndEmptyMasks(sec);
                        return;
                    }
                    sec.HasBounds = true;
                    sec.MinLX = fMinX; sec.MaxLX = fMaxX; sec.MinLY = 0; sec.MaxLY = 15; sec.MinLZ = fMinZ; sec.MaxLZ = fMaxZ;
                    if (candidateOpaque)
                    {
                        sec.InternalExposure = exposure; sec.OpaqueVoxelCount = fastNonAir;
                    }
                    else
                    {
                        sec.InternalExposure = 0; sec.OpaqueVoxelCount = 0; sec.TransparentCount = fastNonAir; sec.HasTransparent = true;
                    }
                    sec.VoxelCount = S * S * S;
                    if (C == 256)
                    {
                        // Entire section filled with same id – build full uniform including transparent bits if needed.
                        sec.Kind = Section.RepresentationKind.Uniform;
                        sec.UniformBlockId = candidateId;
                        sec.IsAllAir = false;
                        sec.CompletelyFull = true;
                        if (!candidateOpaque)
                        {
                            sec.TransparentBits = new ulong[64]; for (int i = 0; i < 64; i++) sec.TransparentBits[i] = ulong.MaxValue;
                            BuildTransparentFaceMasks(sec, sec.TransparentBits); // uniform transparent face masks
                        }
                        sec.MetadataBuilt = true;
                        sec.StructuralDirty = false;
                        sec.IdMapDirty = false;
                        sec.BuildScratch = null; ReturnScratch(scratch);
                        FinalizeTransparentAndEmptyMasks(sec);
                        return;
                    }
                    // Partial fill single id -> 1‑bit packed form (palette [AIR, id]).
                    sec.Kind = Section.RepresentationKind.Packed;
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
                    if (candidateOpaque)
                    {
                        sec.OpaqueBits = occ; BuildFaceMasks(sec, occ);
                    }
                    else
                    {
                        sec.TransparentBits = occ; sec.HasTransparent = true; sec.TransparentCount = fastNonAir; BuildTransparentFaceMasks(sec, occ);
                    }
                    sec.IsAllAir = false;
                    sec.MetadataBuilt = true; sec.StructuralDirty = false; sec.IdMapDirty = false; sec.BuildScratch = null; ReturnScratch(scratch);
                    FinalizeTransparentAndEmptyMasks(sec);
                    return;
                }

                // 4.b Fused traversal path (delegates to helper for representation decisions).
                FusedNonEscalatedFinalize(sec, scratch);
                return;
            }

            // 5. Escalated fallback (dense reconstruction)
            DenseExpandedFinaliseSection(sec, scratch);
            // DenseExpandedFinaliseSection will perform mask finalization.
        }
    }
}

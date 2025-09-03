using MVGE_INF.Generation.Models;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace MVGE_GEN.Utils
{
    internal partial class SectionUtils
    {
        // ---------------------------------------------------------------------------------------------
        // WriteColumnMaskToBitData
        // Writes a 16‑bit occupancy column mask (one bit per Y level) directly into the packed BitData
        // array at the column's base bit position (column-major layout: 16 voxels per column).
        // ---------------------------------------------------------------------------------------------
        private static void WriteColumnMaskToBitData(uint[] bitData, int columnIndex, ushort mask)
        {
            if (mask == 0) return; // nothing to write

            int baseLi = columnIndex << 4;      // 16 voxels per column
            int word = baseLi >> 5;             // index into 32‑bit word array
            int bitOffset = baseLi & 31;        // bit offset inside the word

            ulong bits = (ulong)mask << bitOffset; // may spill into next word
            bitData[word] |= (uint)(bits & 0xFFFFFFFFU);
            if (bitOffset + 16 > 32)
            {
                bitData[word + 1] |= (uint)(bits >> 32);
            }
        }

        // -------------------------------------------------------------------------------------------------
        // FinalizeSection
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
        public static void FinalizeSection(ChunkSection sec)
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
                    sec.StructuralDirty = false;
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
                    sec.MinLY = 0;     sec.MaxLY = 15;
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
                    sec.PaletteLookup = new Dictionary<ushort, int> {{ AIR, 0 }, { candidateId, 1 }};
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
            FinalizeSectionFallBack(sec, scratch);
        }

        // -------------------------------------------------------------------------------------------------
        // FusedNonEscalatedFinalize
        // Performs a single pass over all non‑empty, non‑escalated columns computing:
        //  * Non‑air voxel count
        //  * Internal adjacency (X,Y,Z)
        //  * Bounds (min/max in XYZ) 
        //  * Distinct id list (optional rebuild if DistinctDirty)
        // Then selects the optimal permanent representation.
        // -------------------------------------------------------------------------------------------------
        private static void FusedNonEscalatedFinalize(ChunkSection sec, SectionBuildScratch scratch)
        {
            const int S = ChunkSection.SECTION_SIZE;
            sec.VoxelCount = S * S * S;

            int nonAir = 0;
            int adjY = 0;
            int adjX = 0;
            int adjZ = 0;

            bool boundsInit = false;
            byte minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;

            // Double buffering of per‑X occupancy masks to compute Z adjacency cheaply.
            Span<ushort> prevRowOcc = stackalloc ushort[16];
            Span<ushort> curRowOcc = stackalloc ushort[16];
            prevRowOcc.Clear();
            curRowOcc.Clear();

            bool rebuildDistinct = scratch.DistinctDirty;
            Span<ushort> tmpDistinct = stackalloc ushort[8];
            int tmpDistinctCount = rebuildDistinct ? 0 : scratch.DistinctCount;
            Span<int> tmpDistinctVoxelCounts = stackalloc int[8];
            bool earlyUniformShortCircuit = false;
            ushort earlyUniformId = 0;

            // Track active (non‑empty) columns for later materialization (e.g., Packed/ Sparse).
            Span<int> activeColumns = stackalloc int[COLUMN_COUNT];
            int activeColumnCount = 0;

            bool singleIdPossible = !rebuildDistinct && scratch.DistinctCount == 1;
            ushort firstId = 0;

            for (int z = 0; z < S; z++)
            {
                // Reset current row occupancy
                for (int x = 0; x < S; x++) curRowOcc[x] = 0;
                ushort prevOccInRow = 0;

                for (int x = 0; x < S; x++)
                {
                    int ci = z * S + x;
                    ref var col = ref scratch.GetReadonlyColumn(ci);

                    if (col.RunCount == 0 || col.NonAir == 0)
                    {
                        prevOccInRow = col.OccMask; // zero path
                        continue;
                    }

                    activeColumns[activeColumnCount++] = ci;

                    // Opportunistic single-id tracking
                    if (singleIdPossible)
                    {
                        if (col.RunCount >= 1 && col.Id0 != AIR)
                        {
                            if (firstId == 0) firstId = col.Id0; else if (col.Id0 != firstId) singleIdPossible = false;
                        }
                        if (singleIdPossible && col.RunCount == 2 && col.Id1 != AIR)
                        {
                            if (firstId == 0) firstId = col.Id1; else if (col.Id1 != firstId) singleIdPossible = false;
                        }
                    }

                    nonAir += col.NonAir;
                    adjY += col.AdjY;         // Vertical adjacency cached per column

                    if (!boundsInit)
                    {
                        boundsInit = true;
                        minX = maxX = (byte)x;
                        minZ = maxZ = (byte)z;
                        minY = 15;
                        maxY = 0;
                    }

                    // Y bounds from runs
                    if (col.RunCount >= 1)
                    {
                        if (col.Y0Start < minY) minY = col.Y0Start;
                        if (col.Y0End > maxY) maxY = col.Y0End;
                    }
                    if (col.RunCount == 2)
                    {
                        if (col.Y1Start < minY) minY = col.Y1Start;
                        if (col.Y1End > maxY) maxY = col.Y1End;
                    }

                    // X/Z bounds
                    if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                    if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;

                    ushort occ = col.OccMask;
                    curRowOcc[x] = occ;

                    // X adjacency inside row – popcount of overlapping bits between consecutive columns.
                    if (x > 0 && prevOccInRow != 0 && occ != 0)
                    {
                        adjX += BitOperations.PopCount((uint)(occ & prevOccInRow));
                    }
                    prevOccInRow = occ;

                    // Distinct rebuild path (only if flagged dirty)
                    if (rebuildDistinct)
                    {
                        if (col.RunCount >= 1 && col.Id0 != AIR)
                        {
                            bool found = false; int foundIndex = -1;
                            for (int i = 0; i < tmpDistinctCount; i++)
                            {
                                if (tmpDistinct[i] == col.Id0) { found = true; foundIndex = i; break; }
                            }
                            if (!found && tmpDistinctCount < 8)
                            {
                                foundIndex = tmpDistinctCount;
                                tmpDistinct[tmpDistinctCount++] = col.Id0;
                            }
                            if (foundIndex >= 0)
                            {
                                tmpDistinctVoxelCounts[foundIndex] += col.NonAir;
                                if (!earlyUniformShortCircuit && tmpDistinctVoxelCounts[foundIndex] == 4096)
                                {
                                    earlyUniformShortCircuit = true;
                                    earlyUniformId = col.Id0;
                                }
                            }
                        }
                        if (col.RunCount == 2 && col.Id1 != AIR)
                        {
                            bool found = false; int foundIndex = -1;
                            for (int i = 0; i < tmpDistinctCount; i++)
                            {
                                if (tmpDistinct[i] == col.Id1) { found = true; foundIndex = i; break; }
                            }
                            if (!found && tmpDistinctCount < 8)
                            {
                                foundIndex = tmpDistinctCount;
                                tmpDistinct[tmpDistinctCount++] = col.Id1;
                            }
                            if (foundIndex >= 0)
                            {
                                tmpDistinctVoxelCounts[foundIndex] += col.NonAir;
                                if (!earlyUniformShortCircuit && tmpDistinctVoxelCounts[foundIndex] == 4096)
                                {
                                    earlyUniformShortCircuit = true;
                                    earlyUniformId = col.Id1;
                                }
                            }
                        }
                        if (singleIdPossible && tmpDistinctCount > 1) singleIdPossible = false;
                    }
                }

                // Z adjacency: compare current row vs previous row per X column occupancy bitmask.
                if (z > 0)
                {
                    for (int x = 0; x < S; x++)
                    {
                        ushort a = curRowOcc[x];
                        ushort b = prevRowOcc[x];
                        if (a != 0 && b != 0)
                        {
                            adjZ += BitOperations.PopCount((uint)(a & b));
                        }
                    }
                }

                // Swap buffers (copy current row to previous row buffer)
                for (int x = 0; x < S; x++) prevRowOcc[x] = curRowOcc[x];

                if (earlyUniformShortCircuit) break; // full cube uniform early exit possible
            }

            // Early full uniform short‑circuit
            if (earlyUniformShortCircuit && earlyUniformId != 0)
            {
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = earlyUniformId;
                sec.IsAllAir = false;
                sec.NonAirCount = sec.VoxelCount = S * S * S;
                sec.CompletelyFull = true;

                // Analytical exposure for full 16³ cube
                int len = S;
                long lenL = len;
                long internalAdj = (lenL - 1) * lenL * lenL + lenL * (lenL - 1) * lenL + lenL * lenL * (lenL - 1);
                sec.InternalExposure = (int)(6L * 4096 - 2L * internalAdj);
                sec.HasBounds = true;
                sec.MinLX = sec.MinLY = sec.MinLZ = 0;
                sec.MaxLX = sec.MaxLY = sec.MaxLZ = 15;
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                sec.BuildScratch = null;
                ReturnScratch(scratch);
                return;
            }

            // Commit rebuilt distinct list if required
            if (rebuildDistinct)
            {
                scratch.DistinctCount = tmpDistinctCount;
                for (int i = 0; i < tmpDistinctCount; i++) scratch.Distinct[i] = tmpDistinct[i];
                scratch.DistinctDirty = false;
            }

            sec.NonAirCount = nonAir;
            if (!boundsInit || nonAir == 0)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty;
                sec.IsAllAir = true;
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                ReturnScratch(scratch);
                sec.BuildScratch = null;
                return;
            }

            // Finalize bounds & exposure
            sec.HasBounds = true;
            sec.MinLX = minX; sec.MaxLX = maxX;
            sec.MinLY = minY; sec.MaxLY = maxY;
            sec.MinLZ = minZ; sec.MaxLZ = maxZ;
            sec.InternalExposure = 6 * nonAir - 2 * (adjX + adjY + adjZ);

            // ---------------- Representation selection ----------------
            if (nonAir == S * S * S && scratch.DistinctCount == 1)
            {
                // Full volume single id -> Uniform
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = scratch.Distinct[0];
                sec.IsAllAir = false;
                sec.CompletelyFull = true;
            }
            else if (nonAir <= 128)
            {
                // Sparse: materialize index/id arrays
                int count = nonAir;
                int[] idxArr = new int[count];
                ushort[] blkArr = new ushort[count];
                int write = 0;
                ushort singleId = scratch.DistinctCount == 1 ? scratch.Distinct[0] : (ushort)0;

                for (int i = 0; i < activeColumnCount; i++)
                {
                    int ci = activeColumns[i];
                    ref var col = ref scratch.GetReadonlyColumn(ci);
                    int baseLi = ci << 4;

                    if (col.RunCount >= 1)
                    {
                        ushort id0 = singleId != 0 ? singleId : col.Id0;
                        for (int y = col.Y0Start; y <= col.Y0End; y++)
                        {
                            idxArr[write] = baseLi + y;
                            blkArr[write++] = id0;
                        }
                    }
                    if (col.RunCount == 2)
                    {
                        ushort id1 = singleId != 0 ? singleId : col.Id1;
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                        {
                            idxArr[write] = baseLi + y;
                            blkArr[write++] = id1;
                        }
                    }
                }

                sec.Kind = ChunkSection.RepresentationKind.Sparse;
                sec.SparseIndices = idxArr;
                sec.SparseBlocks = blkArr;
                sec.IsAllAir = false;

                // Build optional occupancy mask for sparse range (enables face mask generation)
                if (count >= SPARSE_MASK_BUILD_MIN && count <= 128)
                {
                    var occSparse = RentOccupancy();
                    for (int i = 0; i < idxArr.Length; i++)
                    {
                        int li = idxArr[i];
                        occSparse[li >> 6] |= 1UL << (li & 63);
                    }
                    sec.OccupancyBits = occSparse;
                    BuildFaceMasks(sec, occSparse);
                }
            }
            else
            {
                // Multi‑form decisions for higher voxel counts.
                if (scratch.DistinctCount == 1)
                {
                    // Single id partial -> 1‑bit Packed
                    sec.Kind = ChunkSection.RepresentationKind.Packed;
                    sec.Palette = new List<ushort> { AIR, scratch.Distinct[0] };
                    sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { scratch.Distinct[0], 1 } };
                    sec.BitsPerIndex = 1;
                    sec.BitData = RentBitData(128);
                    Array.Clear(sec.BitData, 0, 128);
                    var occ = RentOccupancy();

                    for (int i = 0; i < activeColumnCount; i++)
                    {
                        int ci = activeColumns[i];
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        if (col.OccMask == 0) continue;
                        WriteColumnMask(occ, ci, col.OccMask);
                        WriteColumnMaskToBitData(sec.BitData, ci, col.OccMask);
                    }

                    sec.OccupancyBits = occ;
                    sec.IsAllAir = false;
                    BuildFaceMasks(sec, occ);
                }
                else
                {
                    // Decide between MultiPacked and DenseExpanded.
                    int distinctCount = scratch.DistinctCount;
                    int paletteCount = distinctCount + 1; // include AIR
                    int bitsPerIndex = paletteCount <= 1 ? 1 : (int)BitOperations.Log2((uint)(paletteCount - 1)) + 1;
                    int packedBytes = (int)(((long)bitsPerIndex * 4096 + 7) / 8);
                    int denseBytes = 4096 * sizeof(ushort);
                    bool chooseMultiPacked = paletteCount <= 64 && (packedBytes + paletteCount * 2) < denseBytes;

                    if (chooseMultiPacked)
                    {
                        sec.Kind = ChunkSection.RepresentationKind.MultiPacked;
                        sec.Palette = new List<ushort>(paletteCount) { AIR };
                        sec.PaletteLookup = new Dictionary<ushort, int>(paletteCount) { { AIR, 0 } };
                        for (int i = 0; i < distinctCount; i++)
                        {
                            ushort id = scratch.Distinct[i];
                            if (!sec.PaletteLookup.ContainsKey(id))
                            {
                                sec.PaletteLookup[id] = sec.Palette.Count;
                                sec.Palette.Add(id);
                            }
                        }

                        int pcMinusOne = sec.Palette.Count - 1;
                        sec.BitsPerIndex = pcMinusOne <= 0 ? 1 : (int)BitOperations.Log2((uint)pcMinusOne) + 1;
                        long totalBits = (long)sec.BitsPerIndex * 4096;
                        int uintCount = (int)((totalBits + 31) / 32);
                        sec.BitData = RentBitData(uintCount);
                        Array.Clear(sec.BitData, 0, uintCount);
                        var occ = RentOccupancy();

                        for (int i = 0; i < activeColumnCount; i++)
                        {
                            int ci = activeColumns[i];
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            int baseLi = ci << 4;

                            if (col.RunCount >= 1)
                            {
                                int pi0 = sec.PaletteLookup[col.Id0];
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                {
                                    int li = baseLi + y;
                                    WriteBits(sec, li, pi0);
                                    occ[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                            if (col.RunCount == 2)
                            {
                                int pi1 = sec.PaletteLookup[col.Id1];
                                for (int y = col.Y1Start; y <= col.Y1End; y++)
                                {
                                    int li = baseLi + y;
                                    WriteBits(sec, li, pi1);
                                    occ[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                        }
                        sec.OccupancyBits = occ;
                        sec.IsAllAir = false;
                        BuildFaceMasks(sec, occ);
                    }
                    else
                    {
                        // Dense expanded form.
                        sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                        var dense = RentDense();
                        var occDense = RentOccupancy();

                        for (int i = 0; i < activeColumnCount; i++)
                        {
                            int ci = activeColumns[i];
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            int baseLi = ci << 4;

                            if (col.RunCount >= 1)
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                {
                                    int li = baseLi + y;
                                    dense[li] = col.Id0;
                                    occDense[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                            if (col.RunCount == 2)
                            {
                                for (int y = col.Y1Start; y <= col.Y1End; y++)
                                {
                                    int li = baseLi + y;
                                    dense[li] = col.Id1;
                                    occDense[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                        }

                        sec.ExpandedDense = dense;
                        sec.OccupancyBits = occDense;
                        sec.IsAllAir = false;
                        BuildFaceMasks(sec, occDense);
                    }
                }
            }

            sec.MetadataBuilt = true;
            sec.StructuralDirty = false;
            sec.IdMapDirty = false;
            sec.BuildScratch = null;
            ReturnScratch(scratch);
        }

        // -------------------------------------------------------------------------------------------------
        // FinalizeSectionFallBack
        // Escalated path: at least one column escalated (RunCount == 255) to per‑voxel storage. We
        // rebuild a dense array (ushort[4096]) while computing adjacency, bounds and occupancy in a
        // straightforward O(4096) pass.
        // -------------------------------------------------------------------------------------------------
        private static void FinalizeSectionFallBack(ChunkSection sec, SectionBuildScratch scratch)
        {
            sec.VoxelCount = S * S * S;
            var dense = RentDense();
            var occ = RentOccupancy();

            int nonAir = 0;
            byte minX = 255, minY = 255, minZ = 255, maxX = 0, maxY = 0, maxZ = 0;
            bool bounds = false;
            int adjX = 0, adjY = 0, adjZ = 0;

            // Local helper: set one voxel (skip duplicates) and update bounds / occupancy.
            void SetVoxel(int ci, int x, int z, int y, ushort id)
            {
                if (id == AIR) return;
                int li = (ci << 4) + y;
                if (dense[li] != 0) return; // already set from another run path
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

            // Decode each column and accumulate vertical adjacency (AdjY).
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
                            if (prev != 0) adjY++;
                            prev = id;
                        }
                        else prev = 0;
                    }
                }
                else
                {
                    if (rc >= 1)
                    {
                        for (int y = col.Y0Start; y <= col.Y0End; y++)
                        {
                            SetVoxel(ci, x, z, y, col.Id0);
                            if (y > col.Y0Start) adjY++;
                        }
                    }
                    if (rc == 2)
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                        {
                            SetVoxel(ci, x, z, y, col.Id1);
                            if (y > col.Y1Start) adjY++;
                        }
                    }
                }
            }

            // Horizontal adjacency across X/Z dimensions.
            const int DeltaX = 16;   // adding 16 moves to next column in X inside same Z row
            const int DeltaZ = 256;  // adding 256 moves to next Z slice (16 columns * 16 y)

            for (int z = 0; z < S; z++)
            {
                for (int x = 0; x < S; x++)
                {
                    int ci = z * S + x;
                    for (int y = 0; y < S; y++)
                    {
                        int li = (ci << 4) + y;
                        if (dense[li] == 0) continue;
                        if (x + 1 < S && dense[li + DeltaX] != 0) adjX++;  // +X neighbor
                        if (z + 1 < S && dense[li + DeltaZ] != 0) adjZ++;  // +Z neighbor
                    }
                }
            }

            // Build final representation (DenseExpanded) + metadata.
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
            sec.StructuralDirty = false;
            sec.IdMapDirty = false;
            sec.BuildScratch = null;
            ReturnScratch(scratch);
        }
    }
}

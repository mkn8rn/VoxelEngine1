using MVGE_INF.Generation.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Utils
{
    internal partial class SectionUtils
    {
        // Helper: write a 16-bit column mask directly into 1-bit packed BitData (uint[] words) at the column's base linear bit.
        private static void WriteColumnMaskToBitData(uint[] bitData, int columnIndex, ushort mask)
        {
            if (mask == 0)
            {
                return;
            }

            int baseLi = columnIndex << 4; // 16 voxels per column
            int word = baseLi >> 5;        // 32-bit word index
            int bitOffset = baseLi & 31;   // bit offset inside word

            // shift into position (may spill into next word)
            ulong bits = (ulong)mask << bitOffset;

            bitData[word] |= (uint)(bits & 0xFFFFFFFFu);

            if (bitOffset + 16 > 32)
            {
                bitData[word + 1] |= (uint)(bits >> 32);
            }
        }

        // -------------------------------------------------------------------------------------------------
        // FinalizeSection:
        // Primary entry. Determines if fast path (no escalated per-voxel columns) can be used.
        // Falls back to a dense expansion path when any column is escalated (RunCount==255).
        // -------------------------------------------------------------------------------------------------
        public static void FinalizeSection(ChunkSection sec)
        {
            if (sec == null)
            {
                return;
            }

            // FAST GUARD: If there is no build scratch, the section was already finalized / materialized.
            // Previous logic incorrectly reclassified such sections (including valid Uniform ones) as Empty.
            // We only rebuild metadata if it is explicitly marked not built.
            if (sec.BuildScratch == null)
            {
                if (sec.MetadataBuilt)
                {
                    return; // nothing to do
                }

                // Lightweight metadata rebuild depending on representation.
                switch (sec.Kind)
                {
                    case ChunkSection.RepresentationKind.Empty:
                        sec.IsAllAir = true;
                        sec.NonAirCount = 0;
                        sec.InternalExposure = 0;
                        sec.HasBounds = false;
                        sec.MetadataBuilt = true;
                        return;

                    case ChunkSection.RepresentationKind.Uniform:
                        BuildMetadataUniform(sec);
                        return;

                    case ChunkSection.RepresentationKind.Sparse:
                        BuildMetadataSparse(sec);
                        return;

                    case ChunkSection.RepresentationKind.DenseExpanded:
                        BuildMetadataDense(sec);
                        return;

                    case ChunkSection.RepresentationKind.Packed:
                    default:
                        BuildMetadataPacked(sec);
                        return;
                }
            }

            var scratch = sec.BuildScratch as SectionBuildScratch;

            // Early out: untouched or all air.
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

            // Fast path: no escalated columns -> can reason purely from run metadata + 16-bit occupancy.
            if (!scratch.AnyEscalated)
            {
                // -----------------------------------------------------------------------------------------
                // Special classification: all contributing columns are a single full-height run of the
                // same block id. This allows a very compact Packed or Uniform representation.
                // -----------------------------------------------------------------------------------------
                bool singleIdFullHeightCandidate = true;
                ushort candidateId = 0;
                int filledColumns = 0;
                Span<ushort> rowMask = stackalloc ushort[S]; // per z row: 16 bits for x occupancy

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

                        if (rc != 1 || col.Y0Start != 0 || col.Y0End != 15)
                        {
                            singleIdFullHeightCandidate = false;
                            break;
                        }

                        ushort id = col.Id0;

                        if (candidateId == 0)
                        {
                            candidateId = id; // first id seen
                        }
                        else if (id != candidateId)
                        {
                            singleIdFullHeightCandidate = false;
                            break;
                        }

                        maskRow |= (ushort)(1 << x);
                        filledColumns++;
                    }

                    rowMask[z] = maskRow;
                }

                if (singleIdFullHeightCandidate && candidateId != 0)
                {
                    int C = filledColumns;
                    int fastNonAir = C * 16; // 16 voxels per full column

                    // Horizontal adjacency within each row (X direction only).
                    int adj2D = 0;

                    for (int z = 0; z < S; z++)
                    {
                        ushort m = rowMask[z];

                        if (m == 0)
                        {
                            continue;
                        }

                        adj2D += BitOperations.PopCount((uint)(m & (m << 1)));
                    }

                    // Vertical adjacency between adjacent rows (Z direction) treating each row as 16 bits.
                    for (int z = 0; z < S - 1; z++)
                    {
                        ushort inter = (ushort)(rowMask[z] & rowMask[z + 1]);

                        if (inter != 0)
                        {
                            adj2D += BitOperations.PopCount(inter);
                        }
                    }

                    // Fast exact exposure calculus for uniform full-height columns.
                    int fastAdjY = 15 * C;       // inside each full column there are 15 vertical adjacencies
                    int adjXplusZ = 16 * adj2D;  // each horizontal adjacency spans 16 vertically
                    int exposure = 6 * fastNonAir - 2 * (fastAdjY + adjXplusZ);

                    sec.InternalExposure = exposure;
                    sec.NonAirCount = fastNonAir;
                    sec.VoxelCount = S * S * S;

                    // Determine 2D bounds in X/Z (Y is full height 0..15).
                    byte fMinX = 15;
                    byte fMaxX = 0;
                    byte fMinZ = 15;
                    byte fMaxZ = 0;
                    bool any = false;

                    for (int z = 0; z < S; z++)
                    {
                        ushort m = rowMask[z];

                        if (m == 0)
                        {
                            continue;
                        }

                        if (!any)
                        {
                            any = true;
                            fMinZ = fMaxZ = (byte)z;
                            fMinX = 15;
                            fMaxX = 0;
                        }

                        if (z < fMinZ)
                        {
                            fMinZ = (byte)z;
                        }

                        if (z > fMaxZ)
                        {
                            fMaxZ = (byte)z;
                        }

                        if (m != 0)
                        {
                            int first = BitOperations.TrailingZeroCount(m);
                            int last = 15 - BitOperations.LeadingZeroCount((uint)m << 16);

                            if (first < fMinX)
                            {
                                fMinX = (byte)first;
                            }

                            if (last > fMaxX)
                            {
                                fMaxX = (byte)last;
                            }
                        }
                    }

                    if (!any)
                    {
                        sec.Kind = ChunkSection.RepresentationKind.Empty;
                        sec.IsAllAir = true;
                        sec.MetadataBuilt = true;
                        sec.BuildScratch = null;
                        ReturnScratch(scratch);
                        return;
                    }

                    sec.HasBounds = true;
                    sec.MinLX = fMinX;
                    sec.MaxLX = fMaxX;
                    sec.MinLY = 0;
                    sec.MaxLY = 15;
                    sec.MinLZ = fMinZ;
                    sec.MaxLZ = fMaxZ;

                    // Entire section filled with one id -> Uniform.
                    if (C == 256)
                    {
                        sec.Kind = ChunkSection.RepresentationKind.Uniform;
                        sec.UniformBlockId = candidateId;
                        sec.IsAllAir = false;
                        sec.CompletelyFull = true;
                        sec.MetadataBuilt = true;
                        sec.BuildScratch = null;
                        ReturnScratch(scratch);
                        return;
                    }

                    // Partial fill with one id -> 1-bit packed representation (palette [AIR, id]).
                    sec.Kind = ChunkSection.RepresentationKind.Packed;
                    sec.Palette = new List<ushort> { AIR, candidateId };
                    sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { candidateId, 1 } };
                    sec.BitsPerIndex = 1;
                    sec.BitData = RentBitData(128); // 4096 bits / 32 = 128 uint words
                    Array.Clear(sec.BitData, 0, 128);

                    var occFull = RentOccupancy();

                    // Column-oriented occupancy using existing OccMask (full-height columns have 0xFFFF)
                    if (C > 0)
                    {
                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);

                            if (col.RunCount == 1 && col.Y0Start == 0 && col.Y0End == 15 && col.Id0 == candidateId)
                            {
                                WriteColumnMask(occFull, ci, col.OccMask);
                                WriteColumnMaskToBitData(sec.BitData, ci, col.OccMask); // direct write, no second copy pass
                            }
                        }
                    }

                    sec.OccupancyBits = occFull;
                    BuildFaceMasks(sec, occFull);

                    sec.IsAllAir = false;
                    sec.MetadataBuilt = true;
                    sec.BuildScratch = null;
                    ReturnScratch(scratch);
                    return;
                }

                // -----------------------------------------------------------------------------------------
                // Fused single traversal for distinct rebuild (if needed), counts, bounds and adjacency.
                // Opportunistic occupancy build for potential single-id classification.
                // -----------------------------------------------------------------------------------------
                sec.VoxelCount = S * S * S;

                int nonAir = 0;
                int adjY = 0;
                int adjX = 0;
                int adjZ = 0;

                bool boundsInit = false;
                byte minX = 0;
                byte maxX = 0;
                byte minY = 0;
                byte maxY = 0;
                byte minZ = 0;
                byte maxZ = 0;

                Span<ushort> prevRowOccMask = stackalloc ushort[16];
                prevRowOccMask.Clear();

                bool rebuildDistinct = scratch.DistinctDirty;
                Span<ushort> tmpDistinct = stackalloc ushort[8];
                int tmpDistinctCount = rebuildDistinct ? 0 : scratch.DistinctCount;

                // Opportunistic single-id tracking
                bool singleIdPossible = !rebuildDistinct ? (scratch.DistinctCount == 1) : true; // optimistic if rebuilding
                ushort firstId = 0;

                // NOTE: Removed occSingle allocation to avoid later copy; we will rebuild occupancy only if needed per representation.
                ulong[] occSingle = null; // kept for sparse path compatibility (not used for packed heavy fill now)

                for (int z = 0; z < S; z++)
                {
                    ushort prevOccInRow = 0;

                    for (int x = 0; x < S; x++)
                    {
                        int ci = z * S + x;
                        ref var col = ref scratch.GetReadonlyColumn(ci);

                        if (col.RunCount == 0 || col.NonAir == 0)
                        {
                            prevOccInRow = col.OccMask;
                            continue;
                        }

                        if (singleIdPossible)
                        {
                            if (col.RunCount >= 1 && col.Id0 != AIR)
                            {
                                if (firstId == 0)
                                {
                                    firstId = col.Id0;
                                }
                                else if (col.Id0 != firstId)
                                {
                                    singleIdPossible = false;
                                }
                            }

                            if (singleIdPossible && col.RunCount == 2 && col.Id1 != AIR)
                            {
                                if (firstId == 0)
                                {
                                    firstId = col.Id1;
                                }
                                else if (col.Id1 != firstId)
                                {
                                    singleIdPossible = false;
                                }
                            }
                        }

                        nonAir += col.NonAir;
                        adjY += col.AdjY;

                        if (!boundsInit)
                        {
                            boundsInit = true;
                            minX = maxX = (byte)x;
                            minZ = maxZ = (byte)z;
                            minY = 15;
                            maxY = 0;
                        }

                        if (col.RunCount >= 1)
                        {
                            if (col.Y0Start < minY)
                            {
                                minY = col.Y0Start;
                            }

                            if (col.Y0End > maxY)
                            {
                                maxY = col.Y0End;
                            }
                        }

                        if (col.RunCount == 2)
                        {
                            if (col.Y1Start < minY)
                            {
                                minY = col.Y1Start;
                            }

                            if (col.Y1End > maxY)
                            {
                                maxY = col.Y1End;
                            }
                        }

                        if (x < minX)
                        {
                            minX = (byte)x;
                        }
                        else if (x > maxX)
                        {
                            maxX = (byte)x;
                        }

                        if (z < minZ)
                        {
                            minZ = (byte)z;
                        }
                        else if (z > maxZ)
                        {
                            maxZ = (byte)z;
                        }

                        ushort occ = col.OccMask;

                        if (x > 0 && prevOccInRow != 0 && occ != 0)
                        {
                            adjX += BitOperations.PopCount((uint)(occ & prevOccInRow));
                        }

                        prevOccInRow = occ;

                        ushort prevZOcc = prevRowOccMask[x];

                        if (z > 0 && prevZOcc != 0 && occ != 0)
                        {
                            adjZ += BitOperations.PopCount((uint)(occ & prevZOcc));
                        }

                        if (rebuildDistinct)
                        {
                            if (col.RunCount >= 1 && col.Id0 != AIR)
                            {
                                bool found = false;

                                for (int i = 0; i < tmpDistinctCount; i++)
                                {
                                    if (tmpDistinct[i] == col.Id0)
                                    {
                                        found = true;
                                        break;
                                    }
                                }

                                if (!found && tmpDistinctCount < 8)
                                {
                                    tmpDistinct[tmpDistinctCount++] = col.Id0;
                                }
                            }

                            if (col.RunCount == 2 && col.Id1 != AIR)
                            {
                                bool found = false;

                                for (int i = 0; i < tmpDistinctCount; i++)
                                {
                                    if (tmpDistinct[i] == col.Id1)
                                    {
                                        found = true;
                                        break;
                                    }
                                }

                                if (!found && tmpDistinctCount < 8)
                                {
                                    tmpDistinct[tmpDistinctCount++] = col.Id1;
                                }
                            }

                            if (singleIdPossible && tmpDistinctCount > 1)
                            {
                                singleIdPossible = false;
                            }
                        }
                    }

                    // store current row occ masks for next z layer comparison
                    for (int x = 0; x < S; x++)
                    {
                        prevRowOccMask[x] = scratch.GetReadonlyColumn(z * S + x).OccMask;
                    }
                }

                if (rebuildDistinct)
                {
                    scratch.DistinctCount = tmpDistinctCount;

                    for (int i = 0; i < tmpDistinctCount; i++)
                    {
                        scratch.Distinct[i] = tmpDistinct[i];
                    }

                    scratch.DistinctDirty = false;
                }

                // If during traversal singleIdPossible became false but we allocated occSingle, return pool copy.
                if (!singleIdPossible && occSingle != null)
                {
                    ReturnOccupancy(occSingle);
                    occSingle = null;
                }

                bool finalSingleId = singleIdPossible && scratch.DistinctCount == 1 && scratch.Distinct[0] == firstId && firstId != 0;

                sec.NonAirCount = nonAir;

                if (!boundsInit || nonAir == 0)
                {
                    // All air after aggregation.
                    if (occSingle != null)
                    {
                        ReturnOccupancy(occSingle);
                    }

                    sec.Kind = ChunkSection.RepresentationKind.Empty;
                    sec.IsAllAir = true;
                    sec.MetadataBuilt = true;
                    ReturnScratch(scratch);
                    sec.BuildScratch = null;
                    return;
                }

                sec.HasBounds = true;
                sec.MinLX = minX;
                sec.MaxLX = maxX;
                sec.MinLY = minY;
                sec.MaxLY = maxY;
                sec.MinLZ = minZ;
                sec.MaxLZ = maxZ;

                // Internal exposure (same formula) now that adjX/adjY/adjZ are computed.
                sec.InternalExposure = 6 * nonAir - 2 * (adjX + adjY + adjZ);

                // -----------------------------------------------------------------------------------------
                // Representation selection heuristics (unchanged logic, using updated DistinctCount).
                // -----------------------------------------------------------------------------------------
                if (nonAir == S * S * S && scratch.DistinctCount == 1)
                {
                    if (occSingle != null)
                    {
                        ReturnOccupancy(occSingle); // uniform path won't use it
                    }

                    sec.Kind = ChunkSection.RepresentationKind.Uniform;
                    sec.UniformBlockId = scratch.Distinct[0];
                    sec.IsAllAir = false;
                    sec.CompletelyFull = true;
                }
                else if (nonAir <= 128)
                {
                    // Low voxel count -> sparse arrays (index + block id per voxel).
                    int count = nonAir;
                    int[] idxArr = new int[count];
                    ushort[] blkArr = new ushort[count];
                    int p = 0;
                    ushort singleId = scratch.DistinctCount == 1 ? scratch.Distinct[0] : (ushort)0;

                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        byte rc = col.RunCount;

                        if (rc == 0)
                        {
                            continue;
                        }

                        int baseLi = ci << 4;

                        if (rc >= 1)
                        {
                            if (singleId != 0)
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                {
                                    idxArr[p] = baseLi + y;
                                    blkArr[p++] = singleId;
                                }
                            }
                            else
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                {
                                    idxArr[p] = baseLi + y;
                                    blkArr[p++] = col.Id0;
                                }
                            }
                        }

                        if (rc == 2)
                        {
                            if (singleId != 0)
                            {
                                for (int y = col.Y1Start; y <= col.Y1End; y++)
                                {
                                    idxArr[p] = baseLi + y;
                                    blkArr[p++] = singleId;
                                }
                            }
                            else
                            {
                                for (int y = col.Y1Start; y <= col.Y1End; y++)
                                {
                                    idxArr[p] = baseLi + y;
                                    blkArr[p++] = col.Id1;
                                }
                            }
                        }
                    }

                    sec.Kind = ChunkSection.RepresentationKind.Sparse;
                    sec.SparseIndices = idxArr;
                    sec.SparseBlocks = blkArr;
                    sec.IsAllAir = false;

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
                    // Heavier fill. Distinguish single id vs multiple ids.
                    if (scratch.DistinctCount == 1)
                    {
                        // Optimized single-id packed path: build occupancy + BitData in one pass; no intermediate copy.
                        sec.Kind = ChunkSection.RepresentationKind.Packed;
                        sec.Palette = new List<ushort> { AIR, scratch.Distinct[0] };
                        sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { scratch.Distinct[0], 1 } };
                        sec.BitsPerIndex = 1;
                        sec.BitData = RentBitData(128);
                        Array.Clear(sec.BitData, 0, 128);

                        var occ = RentOccupancy();

                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);

                            if (col.RunCount == 0)
                            {
                                continue;
                            }

                            if (col.OccMask == 0)
                            {
                                continue;
                            }

                            WriteColumnMask(occ, ci, col.OccMask);
                            WriteColumnMaskToBitData(sec.BitData, ci, col.OccMask);
                        }

                        sec.OccupancyBits = occ;
                        sec.IsAllAir = false;
                        BuildFaceMasks(sec, occ);
                    }
                    else
                    {
                        sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                        var dense = RentDense();
                        var occDense = RentOccupancy();

                        for (int ci = 0; ci < COLUMN_COUNT; ci++)
                        {
                            ref var col = ref scratch.GetReadonlyColumn(ci);
                            byte rc = col.RunCount;

                            if (rc == 0)
                            {
                                continue;
                            }

                            int baseLi = ci << 4;

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

            // Any escalated columns -> fallback path.
            FinalizeSectionFallBack(sec, scratch);
        }

        // -------------------------------------------------------------------------------------------------
        // FinalizeSectionFallBack:
        // Used when any column escalated to per-voxel storage (RunCount == 255). It reconstructs a
        // dense array while computing counts, bounds, adjacency and occupancy. This is a straightforward
        // O(4096) pass.
        // -------------------------------------------------------------------------------------------------
        private static void FinalizeSectionFallBack(ChunkSection sec, SectionBuildScratch scratch)
        {
            sec.VoxelCount = S * S * S;
            var dense = RentDense();
            var occ = RentOccupancy();

            int nonAir = 0;
            byte minX = 255;
            byte minY = 255;
            byte minZ = 255;
            byte maxX = 0;
            byte maxY = 0;
            byte maxZ = 0;
            bool bounds = false;
            int adjX = 0;
            int adjY = 0;
            int adjZ = 0;

            // Local inlined setter: writes a voxel into dense + occupancy, updates counts & bounds.
            void SetVoxel(int ci, int x, int z, int y, ushort id)
            {
                if (id == AIR)
                {
                    return;
                }

                int li = (ci << 4) + y;

                // skip duplicates
                if (dense[li] != 0)
                {
                    return;
                }

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
                    if (x < minX) minX = (byte)x;
                    else if (x > maxX) maxX = (byte)x;

                    if (y < minY) minY = (byte)y;
                    else if (y > maxY) maxY = (byte)y;

                    if (z < minZ) minZ = (byte)z;
                    else if (z > maxZ) maxZ = (byte)z;
                }
            }

            // Decode each column into dense form, tracking vertical adjacency.
            for (int ci = 0; ci < COLUMN_COUNT; ci++)
            {
                ref var col = ref scratch.GetReadonlyColumn(ci);
                byte rc = col.RunCount;

                if (rc == 0)
                {
                    continue;
                }

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

                            if (prev != 0)
                            {
                                adjY++;
                            }

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

                            if (y > col.Y0Start)
                            {
                                adjY++;
                            }
                        }
                    }

                    if (rc == 2)
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                        {
                            SetVoxel(ci, x, z, y, col.Id1);

                            if (y > col.Y1Start)
                            {
                                adjY++;
                            }
                        }
                    }
                }
            }

            // Horizontal adjacency in X/Z across dense array. DeltaX/Z precomputed linear offsets.
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

                        if (dense[li] == 0)
                        {
                            continue;
                        }

                        if (x + 1 < S && dense[li + DeltaX] != 0)
                        {
                            adjX++;   // +X neighbor
                        }

                        if (z + 1 < S && dense[li + DeltaZ] != 0)
                        {
                            adjZ++;   // +Z neighbor
                        }
                    }
                }
            }

            sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
            sec.ExpandedDense = dense;
            sec.NonAirCount = nonAir;

            if (bounds)
            {
                sec.HasBounds = true;
                sec.MinLX = minX;
                sec.MaxLX = maxX;
                sec.MinLY = minY;
                sec.MaxLY = maxY;
                sec.MinLZ = minZ;
                sec.MaxLZ = maxZ;
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

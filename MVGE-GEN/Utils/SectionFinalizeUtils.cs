using MVGE_GEN.Models;
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
        // -------------------------------------------------------------------------------------------------
        // FinalizeSection:
        // Primary entry. Determines if fast path (no escalated per‑voxel columns) can be used.
        // Falls back to a dense expansion path when any column is escalated (RunCount==255).
        // -------------------------------------------------------------------------------------------------
        public static void FinalizeSection(ChunkSection sec)
        {
            if (sec == null) return;
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

            // Fast path: no escalated columns -> can reason purely from run metadata + 16‑bit occupancy.
            if (!scratch.AnyEscalated)
            {
                // -----------------------------------------------------------------------------------------
                // Special classification: all contributing columns are a single full‑height run of the
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

                        if (rc == 0) continue; // empty column
                        if (rc != 1 || col.Y0Start != 0 || col.Y0End != 15) { singleIdFullHeightCandidate = false; break; }
                        ushort id = col.Id0;
                        if (candidateId == 0) candidateId = id;            // first id seen
                        else if (id != candidateId) { singleIdFullHeightCandidate = false; break; }

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
                        if (m == 0) continue;
                        adj2D += BitOperations.PopCount((uint)(m & (m << 1)));
                    }
                    // Vertical adjacency between adjacent rows (Z direction) treating each row as 16 bits.
                    for (int z = 0; z < S - 1; z++)
                    {
                        ushort inter = (ushort)(rowMask[z] & rowMask[z + 1]);
                        if (inter != 0) adj2D += BitOperations.PopCount(inter);
                    }

                    // Fast exact exposure calculus for uniform full-height columns.
                    int fastAdjY = 15 * C;       // inside each full column there are 15 vertical adjacencies
                    int adjXplusZ = 16 * adj2D;  // each horizontal adjacency spans 16 vertically
                    int exposure = 6 * fastNonAir - 2 * (fastAdjY + adjXplusZ);
                    sec.InternalExposure = exposure;
                    sec.NonAirCount = fastNonAir;
                    sec.VoxelCount = S * S * S;

                    // Determine 2D bounds in X/Z (Y is full height 0..15).
                    byte fMinX = 15, fMaxX = 0, fMinZ = 15, fMaxZ = 0; bool any = false;
                    for (int z = 0; z < S; z++)
                    {
                        ushort m = rowMask[z];
                        if (m == 0) continue;
                        if (!any) { any = true; fMinZ = fMaxZ = (byte)z; fMinX = 15; fMaxX = 0; }
                        if (z < fMinZ) fMinZ = (byte)z; if (z > fMaxZ) fMaxZ = (byte)z;
                        if (m != 0)
                        {
                            int first = BitOperations.TrailingZeroCount(m);
                            int last = 15 - BitOperations.LeadingZeroCount((uint)m << 16);
                            if (first < fMinX) fMinX = (byte)first;
                            if (last > fMaxX) fMaxX = (byte)last;
                        }
                    }
                    if (!any) { sec.Kind = ChunkSection.RepresentationKind.Empty; sec.IsAllAir = true; sec.MetadataBuilt = true; sec.BuildScratch = null; ReturnScratch(scratch); return; }

                    sec.HasBounds = true; sec.MinLX = fMinX; sec.MaxLX = fMaxX; sec.MinLY = 0; sec.MaxLY = 15; sec.MinLZ = fMinZ; sec.MaxLZ = fMaxZ;

                    // Entire section filled with one id -> Uniform.
                    if (C == 256)
                    {
                        sec.Kind = ChunkSection.RepresentationKind.Uniform; sec.UniformBlockId = candidateId; sec.IsAllAir = false; sec.CompletelyFull = true; sec.MetadataBuilt = true; sec.BuildScratch = null; ReturnScratch(scratch); return;
                    }

                    // Partial fill with one id -> 1‑bit packed representation (palette [AIR, id]).
                    sec.Kind = ChunkSection.RepresentationKind.Packed;
                    sec.Palette = new List<ushort> { AIR, candidateId };
                    sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { candidateId, 1 } };
                    sec.BitsPerIndex = 1;
                    sec.BitData = RentBitData(128); // 4096 bits / 32 = 128 uint words
                    Array.Clear(sec.BitData, 0, 128);

                    var occ = RentOccupancy();
                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        if (col.RunCount == 1 && col.Y0Start == 0 && col.Y0End == 15 && col.Id0 == candidateId)
                        {
                            SetRunBits(occ, ci, 0, 15);
                        }
                    }
                    sec.OccupancyBits = occ;
                    // Copy 4096 occupancy bits into BitData (pair of uint per ulong word)
                    for (int w = 0; w < 64; w++)
                    {
                        ulong ow = occ[w];
                        int dst = w << 1;
                        sec.BitData[dst] = (uint)(ow & 0xFFFFFFFF);
                        sec.BitData[dst + 1] = (uint)(ow >> 32);
                    }
                    BuildFaceMasks(sec, occ);
                    sec.IsAllAir = false; sec.MetadataBuilt = true; sec.BuildScratch = null; ReturnScratch(scratch); return;
                }

                // Rebuild distinct list if any replacements changed ids mid‑build.
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
                            bool found = false; for (int i = 0; i < count; i++) if (tmp[i] == col.Id0) { found = true; break; }
                            if (!found && count < tmp.Length) tmp[count++] = col.Id0;
                        }
                        if (rc == 2 && col.Id1 != AIR)
                        {
                            bool found = false; for (int i = 0; i < count; i++) if (tmp[i] == col.Id1) { found = true; break; }
                            if (!found && count < tmp.Length) tmp[count++] = col.Id1;
                        }
                    }
                    scratch.DistinctCount = count; for (int i = 0; i < count; i++) scratch.Distinct[i] = tmp[i];
                    scratch.DistinctDirty = false;
                }

                sec.VoxelCount = S * S * S;
                int nonAir = 0; int adjY = 0; int adjX = 0; int adjZ = 0;
                bool boundsInit = false; byte minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;

                // -----------------------------------------------------------------------------------------
                // Aggregate counts / bounds from columns using already maintained per‑column metadata.
                // -----------------------------------------------------------------------------------------
                for (int z = 0; z < S; z++)
                {
                    int zBase = z * S;
                    for (int x = 0; x < S; x++)
                    {
                        int ci = zBase + x;
                        ref var col = ref scratch.GetReadonlyColumn(ci);
                        if (col.RunCount == 0) continue;
                        if (col.NonAir == 0) continue;
                        nonAir += col.NonAir;
                        adjY += col.AdjY;

                        // Bounds: pull directly from run starts/ends (cheaper than scanning masks).
                        if (!boundsInit)
                        {
                            boundsInit = true; minX = maxX = (byte)x; minZ = maxZ = (byte)z;
                            minY = 15; maxY = 0; // will be refined
                        }
                        if (col.RunCount >= 1)
                        {
                            if (col.Y0Start < minY) minY = col.Y0Start; if (col.Y0End > maxY) maxY = col.Y0End;
                        }
                        if (col.RunCount == 2)
                        {
                            if (col.Y1Start < minY) minY = col.Y1Start; if (col.Y1End > maxY) maxY = col.Y1End;
                        }
                        if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                        if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;
                    }
                }

                sec.NonAirCount = nonAir;
                if (!boundsInit || nonAir == 0)
                {
                    // All air after aggregation.
                    sec.Kind = ChunkSection.RepresentationKind.Empty; sec.IsAllAir = true; sec.MetadataBuilt = true; ReturnScratch(scratch); sec.BuildScratch = null; return;
                }

                sec.HasBounds = true; sec.MinLX = minX; sec.MaxLX = maxX; sec.MinLY = minY; sec.MaxLY = maxY; sec.MinLZ = minZ; sec.MaxLZ = maxZ;

                // -----------------------------------------------------------------------------------------
                // Horizontal adjacency (X and Z directions) using compact 16‑bit per‑column occupancy.
                // Each column's OccMask encodes which y levels are filled; overlapping bits across
                // neighboring columns yield adjacency counts directly.
                // -----------------------------------------------------------------------------------------
                for (int z = 0; z < S; z++)
                {
                    int baseIdx = z * S;
                    for (int x = 0; x < S - 1; x++)
                    {
                        ref var a = ref scratch.GetReadonlyColumn(baseIdx + x);
                        ref var b = ref scratch.GetReadonlyColumn(baseIdx + x + 1);
                        if (a.OccMask == 0 || b.OccMask == 0) continue;
                        adjX += BitOperations.PopCount((uint)(a.OccMask & b.OccMask));
                    }
                }
                for (int z = 0; z < S - 1; z++)
                {
                    int baseA = z * S; int baseB = (z + 1) * S;
                    for (int x = 0; x < S; x++)
                    {
                        ref var a = ref scratch.GetReadonlyColumn(baseA + x);
                        ref var b = ref scratch.GetReadonlyColumn(baseB + x);
                        if (a.OccMask == 0 || b.OccMask == 0) continue;
                        adjZ += BitOperations.PopCount((uint)(a.OccMask & b.OccMask));
                    }
                }

                sec.InternalExposure = 6 * nonAir - 2 * (adjX + adjY + adjZ);

                // -----------------------------------------------------------------------------------------
                // Representation selection heuristics.
                // -----------------------------------------------------------------------------------------
                if (nonAir == S * S * S && scratch.DistinctCount == 1)
                {
                    // Fully filled section of one id.
                    sec.Kind = ChunkSection.RepresentationKind.Uniform; sec.UniformBlockId = scratch.Distinct[0]; sec.IsAllAir = false; sec.CompletelyFull = true;
                }
                else if (nonAir <= 128)
                {
                    // Low voxel count -> sparse arrays (index + block id per voxel).
                    int count = nonAir; int[] idxArr = new int[count]; ushort[] blkArr = new ushort[count]; int p = 0; ushort singleId = scratch.DistinctCount == 1 ? scratch.Distinct[0] : (ushort)0;
                    for (int ci = 0; ci < COLUMN_COUNT; ci++)
                    {
                        ref var col = ref scratch.GetReadonlyColumn(ci); byte rc = col.RunCount; if (rc == 0) continue; int baseLi = ci << 4; if (rc >= 1)
                        {
                            if (singleId != 0) for (int y = col.Y0Start; y <= col.Y0End; y++) { idxArr[p] = baseLi + y; blkArr[p++] = singleId; }
                            else for (int y = col.Y0Start; y <= col.Y0End; y++) { idxArr[p] = baseLi + y; blkArr[p++] = col.Id0; }
                        }
                        if (rc == 2)
                        {
                            if (singleId != 0) for (int y = col.Y1Start; y <= col.Y1End; y++) { idxArr[p] = baseLi + y; blkArr[p++] = singleId; }
                            else for (int y = col.Y1Start; y <= col.Y1End; y++) { idxArr[p] = baseLi + y; blkArr[p++] = col.Id1; }
                        }
                    }
                    sec.Kind = ChunkSection.RepresentationKind.Sparse; sec.SparseIndices = idxArr; sec.SparseBlocks = blkArr; sec.IsAllAir = false;
                    if (count >= SPARSE_MASK_BUILD_MIN && count <= 128)
                    {
                        // Optional occupancy + face masks for mid‑sized sparse sets.
                        var occSparse = RentOccupancy();
                        for (int i = 0; i < idxArr.Length; i++) { int li = idxArr[i]; occSparse[li >> 6] |= 1UL << (li & 63); }
                        sec.OccupancyBits = occSparse; BuildFaceMasks(sec, occSparse);
                    }
                }
                else
                {
                    // Heavier fill. Distinguish single id vs multiple ids.
                    if (scratch.DistinctCount == 1)
                    {
                        // Single id with gaps: store as 1‑bit packed occupancy.
                        sec.Kind = ChunkSection.RepresentationKind.Packed; sec.Palette = new List<ushort> { AIR, scratch.Distinct[0] }; sec.PaletteLookup = new Dictionary<ushort, int> { { AIR, 0 }, { scratch.Distinct[0], 1 } }; sec.BitsPerIndex = 1; sec.BitData = RentBitData(128); Array.Clear(sec.BitData, 0, 128);
                        var occSingle = RentOccupancy();
                        for (int ci = 0; ci < COLUMN_COUNT; ci++) { ref var col = ref scratch.GetReadonlyColumn(ci); byte rc = col.RunCount; if (rc == 0) continue; if (rc >= 1) SetRunBits(occSingle, ci, col.Y0Start, col.Y0End); if (rc == 2) SetRunBits(occSingle, ci, col.Y1Start, col.Y1End); }
                        sec.OccupancyBits = occSingle; for (int w = 0; w < 64; w++) { ulong ow = occSingle[w]; int dst = w << 1; sec.BitData[dst] = (uint)(ow & 0xFFFFFFFF); sec.BitData[dst + 1] = (uint)(ow >> 32); }
                        sec.IsAllAir = false; BuildFaceMasks(sec, occSingle);
                    }
                    else
                    {
                        // Multi‑id fill -> expanded dense array.
                        sec.Kind = ChunkSection.RepresentationKind.DenseExpanded; var dense = RentDense(); var occDense = RentOccupancy();
                        for (int ci = 0; ci < COLUMN_COUNT; ci++) { ref var col = ref scratch.GetReadonlyColumn(ci); byte rc = col.RunCount; if (rc == 0) continue; int baseLi = ci << 4; if (rc >= 1) for (int y = col.Y0Start; y <= col.Y0End; y++) { int li = baseLi + y; dense[li] = col.Id0; occDense[li >> 6] |= 1UL << (li & 63); } if (rc == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) { int li = baseLi + y; dense[li] = col.Id1; occDense[li >> 6] |= 1UL << (li & 63); } }
                        sec.ExpandedDense = dense; sec.OccupancyBits = occDense; sec.IsAllAir = false; BuildFaceMasks(sec, occDense);
                    }
                }

                sec.MetadataBuilt = true; sec.BuildScratch = null; ReturnScratch(scratch); return;
            }

            // Any escalated columns -> fallback path.
            FinalizeSectionFallBack(sec, scratch);
        }

        // -------------------------------------------------------------------------------------------------
        // FinalizeSectionFallBack:
        // Used when any column escalated to per‑voxel storage (RunCount == 255). It reconstructs a
        // dense array while computing counts, bounds, adjacency and occupancy. This is a straightforward
        // O(4096) pass.
        // -------------------------------------------------------------------------------------------------
        private static void FinalizeSectionFallBack(ChunkSection sec, SectionBuildScratch scratch)
        {
            sec.VoxelCount = S * S * S;
            var dense = RentDense();
            var occ = RentOccupancy();
            int nonAir = 0; byte minX = 255, minY = 255, minZ = 255, maxX = 0, maxY = 0, maxZ = 0; bool bounds = false; int adjX = 0, adjY = 0, adjZ = 0;

            // Local inlined setter: writes a voxel into dense + occupancy, updates counts & bounds.
            void SetVoxel(int ci, int x, int z, int y, ushort id)
            {
                if (id == AIR) return;
                int li = (ci << 4) + y; if (dense[li] != 0) return; // skip duplicates
                dense[li] = id; nonAir++; occ[li >> 6] |= 1UL << (li & 63);
                if (!bounds)
                { bounds = true; minX = maxX = (byte)x; minY = maxY = (byte)y; minZ = maxZ = (byte)z; }
                else
                {
                    if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                    if (y < minY) minY = (byte)y; else if (y > maxY) maxY = (byte)y;
                    if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;
                }
            }

            // Decode each column into dense form, tracking vertical adjacency.
            for (int ci = 0; ci < COLUMN_COUNT; ci++)
            {
                ref var col = ref scratch.GetReadonlyColumn(ci);
                byte rc = col.RunCount; if (rc == 0) continue; int x = ci % S; int z = ci / S;
                if (rc == 255)
                {
                    var arr = col.Escalated; ushort prev = 0;
                    for (int y = 0; y < S; y++) { ushort id = arr[y]; if (id != AIR) { SetVoxel(ci, x, z, y, id); if (prev != 0) adjY++; prev = id; } else prev = 0; }
                }
                else
                {
                    if (rc >= 1) for (int y = col.Y0Start; y <= col.Y0End; y++) { SetVoxel(ci, x, z, y, col.Id0); if (y > col.Y0Start) adjY++; }
                    if (rc == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) { SetVoxel(ci, x, z, y, col.Id1); if (y > col.Y1Start) adjY++; }
                }
            }

            // Horizontal adjacency in X/Z across dense array. DeltaX/Z precomputed linear offsets.
            const int DeltaX = 16; const int DeltaZ = 256;
            for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                {
                    int ci = z * S + x;
                    for (int y = 0; y < S; y++)
                    {
                        int li = (ci << 4) + y; if (dense[li] == 0) continue;
                        if (x + 1 < S && dense[li + DeltaX] != 0) adjX++;   // +X neighbor
                        if (z + 1 < S && dense[li + DeltaZ] != 0) adjZ++;   // +Z neighbor
                    }
                }

            sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
            sec.ExpandedDense = dense;
            sec.NonAirCount = nonAir;
            if (bounds)
            {
                sec.HasBounds = true; sec.MinLX = minX; sec.MaxLX = maxX; sec.MinLY = minY; sec.MaxLY = maxY; sec.MinLZ = minZ; sec.MaxLZ = maxZ;
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

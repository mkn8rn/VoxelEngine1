using MVGE_INF.Generation.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using MVGE_INF.Loaders;

namespace MVGE_GEN.Utils
{
    internal partial class SectionUtils
    {

        // -------------------------------------------------------------------------------------------------
        // Array pooling: reduces allocation pressure for frequently reused temporary structures.
        //   Occupancy: 4096 bits (64 * ulong) used when building face masks or packed/sparse data.
        //   Dense:     4096 ushorts storing full per‑voxel ids (8 KB) for DenseExpanded or fallback.
        // Objects are cleared before returning to pool to avoid leaking data across uses.
        // -------------------------------------------------------------------------------------------------
        private static readonly ConcurrentBag<ulong[]> _occupancyPool = new();
        private static readonly ConcurrentBag<ushort[]> _densePool = new();
        // Pool for escalated per-column 16-length voxel arrays
        private static readonly ConcurrentBag<ushort[]> _escalatedColumnPool = new();

        // -------------------------------------------------------------------------------------------------
        // RentBitData – rents a uint[] array of at least the requested length.
        // Used for Packed or MultiPacked representations.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] RentOccupancy() => _occupancyPool.TryTake(out var a) ? a : new ulong[64];
        // -------------------------------------------------------------------------------------------------
        // WriteColumnMask
        // Inserts a 16‑bit vertical occupancy mask for a single column into the 4096‑bit section
        // occupancy bitset (occ). Layout is column‑major: each of the 256 columns (columnIndex 0..255)
        // occupies a contiguous 16‑bit slice starting at linearBit = columnIndex * 16. Bit y in the
        // 16‑bit mask corresponds to local Y level y (0..15) inside that column.
        //
        // Fast path:
        //   * Computes the starting 64‑bit word (w0) and bit offset.
        //   * ORs the shifted 16‑bit mask directly into occ[w0].
        // Spill handling:
        //   * If the 16 bits straddle a 64‑bit boundary (only when starting bit > 48),
        //     the overflow (spill) upper bits are written into occ[w0 + 1].
        //
        // Notes:
        //   * mask == 0 is an early exit (no occupancy).
        //   * No clearing is performed; this is an additive population step.
        //   * Used by packed / single‑id finalize paths to build the unified occupancy bitset
        //     without per‑voxel loops.
        // Complexity: O(1)
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteColumnMask(ulong[] occ, int columnIndex, ushort mask)
        {
            if (mask == 0) return;
            int baseLi = columnIndex << 4; // 16 voxels per column
            int w0 = baseLi >> 6;          // starting 64-bit word index
            int bit = baseLi & 63;         // starting bit within word
            ulong shifted = (ulong)mask << bit;
            occ[w0] |= shifted;
            int spill = bit + 16 - 64;    // how many bits overflow into next word (if any)
            if (spill > 0)
            {
                occ[w0 + 1] |= (ulong)mask >> (16 - spill);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort[] RentDense() => _densePool.TryTake(out var a) ? a : new ushort[4096];

        // -------------------------------------------------------------------------------------------------
        // Scratch pooling: Each in‑progress section maintains a SectionBuildScratch instance holding
        // per‑column run data + small distinct id tracking. Pooling avoids churn when many sections
        // are generated frame to frame.
        // -------------------------------------------------------------------------------------------------
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

        // -------------------------------------------------------------------------------------------------
        // Precomputed inclusive mask table for y ranges [ys,ye] (ys,ye in 0..15, ys<=ye). Saves bit shifts.
        // -------------------------------------------------------------------------------------------------
        // Flattened: index = (ys << 4) | ye
        private static readonly ushort[] _maskTable = BuildMaskTable();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Index(int ys, int ye) => (ys << 4) | ye;

        private static ushort[] BuildMaskTable()
        {
            var tbl = new ushort[16 * 16];
            for (int ys = 0; ys < 16; ys++)
            {
                ushort baseMask = (ushort)(ushort.MaxValue << ys);
                for (int ye = ys; ye < 16; ye++)
                {
                    int len = ye - ys + 1;
                    // Take only len bits starting at ys
                    tbl[(ys << 4) | ye] = (ushort)(((1 << len) - 1) << ys);
                }
            }
            return tbl;
        }

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
            int totalVoxels = S * S * S;
            sec.VoxelCount = totalVoxels;

            int opaqueCount = 0; // replaces previous nonAir accumulation semantics
            int adjY = 0;
            int adjX = 0;
            int adjZ = 0;

            bool boundsInit = false;
            byte minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;

            Span<ushort> prevRowOcc = stackalloc ushort[S];
            Span<ushort> curRowOcc = stackalloc ushort[S];
            prevRowOcc.Clear();
            curRowOcc.Clear();

            bool rebuildDistinct = scratch.DistinctDirty;
            Span<ushort> tmpDistinct = stackalloc ushort[8];
            int tmpDistinctCount = rebuildDistinct ? 0 : scratch.DistinctCount;
            Span<int> tmpDistinctVoxelCounts = stackalloc int[8];
            bool earlyUniformShortCircuit = false;
            ushort earlyUniformId = 0;

            Span<int> activeColumns = stackalloc int[COLUMN_COUNT];
            int activeColumnCount = 0;

            bool singleIdPossible = !rebuildDistinct && scratch.DistinctCount == 1;
            ushort firstId = 0;

            // local caches to avoid repeated property/indexing
            var scratchDistinct = scratch.Distinct;
            int scratchDistinctCount = scratch.DistinctCount;

            for (int z = 0; z < S; z++)
            {
                curRowOcc.Clear();
                ushort prevOccInRow = 0;

                for (int x = 0; x < S; x++)
                {
                    int ci = (z * S) + x;
                    ref readonly var col = ref scratch.GetReadonlyColumn(ci);

                    // fast path for empty column
                    if (col.RunCount == 0 || col.NonAir == 0)
                    {
                        prevOccInRow = col.OccMask;
                        continue;
                    }

                    activeColumns[activeColumnCount++] = ci;

                    // opportunistic single id tracking (cheap)
                    if (singleIdPossible)
                    {
                        if (col.RunCount >= 1 && col.Id0 != AIR)
                        {
                            if (firstId == 0) firstId = col.Id0;
                            else if (col.Id0 != firstId) singleIdPossible = false;
                        }
                        if (singleIdPossible && col.RunCount == 2 && col.Id1 != AIR)
                        {
                            if (firstId == 0) firstId = col.Id1;
                            else if (col.Id1 != firstId) singleIdPossible = false;
                        }
                    }

                    // Recompute opaque occupancy per column from runs; existing col.OccMask represented full run vertical bits regardless of opacity.
                    // We assume generation paths only write opaque materials for stone/soil; if transparency added to runs, filter here.
                    ushort occMaskOpaque = 0;
                    if (col.RunCount >= 1 && col.Id0 != AIR && TerrainLoader.IsOpaque(col.Id0))
                    {
                        // col.OccMask includes both runs when RunCount==2; reconstruct mask segments instead of using full mask unfiltered.
                        int y0s = col.Y0Start; int y0e = col.Y0End; for (int y = y0s; y <= y0e; y++) occMaskOpaque |= (ushort)(1 << y);
                        opaqueCount += (y0e - y0s + 1);
                        adjY += (y0e - y0s); // vertical adjacencies within run
                    }
                    if (col.RunCount == 2 && col.Id1 != AIR && TerrainLoader.IsOpaque(col.Id1))
                    {
                        int y1s = col.Y1Start; int y1e = col.Y1End; for (int y = y1s; y <= y1e; y++) occMaskOpaque |= (ushort)(1 << y);
                        opaqueCount += (y1e - y1s + 1);
                        adjY += (y1e - y1s); // vertical adjacencies within second run
                        // If the two runs are contiguous and both opaque add the connecting adjacency
                        if (col.Id0 != AIR && TerrainLoader.IsOpaque(col.Id0) && y1s == col.Y0End + 1) adjY++;
                    }

                    if (occMaskOpaque != 0)
                    {
                        if (!boundsInit)
                        {
                            boundsInit = true;
                            minX = maxX = (byte)x;
                            minZ = maxZ = (byte)z;
                            minY = 15; maxY = 0; // will correct below
                        }
                        // Y bounds
                        if (col.RunCount >= 1)
                        {
                            if (col.Y0Start < minY) minY = col.Y0Start; if (col.Y0End > maxY) maxY = col.Y0End;
                        }
                        if (col.RunCount == 2)
                        {
                            if (col.Y1Start < minY) minY = col.Y1Start; if (col.Y1End > maxY) maxY = col.Y1End;
                        }

                        // X/Z bounds
                        if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                        if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;
                    }

                    curRowOcc[x] = occMaskOpaque;

                    // X adjacency via popcount of overlap with previous column in row
                    if (x > 0 && prevOccInRow != 0 && occMaskOpaque != 0)
                    {
                        adjX += BitOperations.PopCount((uint)(occMaskOpaque & prevOccInRow));
                    }
                    prevOccInRow = occMaskOpaque;

                    // Distinct rebuild (linear scan up to 8 entries is still fastest here)
                    if (rebuildDistinct)
                    {
                        if (col.RunCount >= 1 && col.Id0 != AIR)
                        {
                            int foundIndex = -1;
                            for (int i = 0; i < tmpDistinctCount; i++) if (tmpDistinct[i] == col.Id0) { foundIndex = i; break; }
                            if (foundIndex == -1 && tmpDistinctCount < 8) { foundIndex = tmpDistinctCount; tmpDistinct[tmpDistinctCount++] = col.Id0; }
                            if (foundIndex >= 0)
                            {
                                // Only treat as potential uniform if whole section volume opaque with this id
                                // For simplicity accumulate raw run size (includes potential translucent if added later)
                                tmpDistinctVoxelCounts[foundIndex] += col.NonAir;
                                if (!earlyUniformShortCircuit && tmpDistinctVoxelCounts[foundIndex] == totalVoxels)
                                {
                                    earlyUniformShortCircuit = true; earlyUniformId = col.Id0;
                                }
                            }
                        }
                        if (col.RunCount == 2 && col.Id1 != AIR)
                        {
                            int foundIndex = -1;
                            for (int i = 0; i < tmpDistinctCount; i++) if (tmpDistinct[i] == col.Id1) { foundIndex = i; break; }
                            if (foundIndex == -1 && tmpDistinctCount < 8) { foundIndex = tmpDistinctCount; tmpDistinct[tmpDistinctCount++] = col.Id1; }
                            if (foundIndex >= 0)
                            {
                                tmpDistinctVoxelCounts[foundIndex] += col.NonAir;
                                if (!earlyUniformShortCircuit && tmpDistinctVoxelCounts[foundIndex] == totalVoxels)
                                {
                                    earlyUniformShortCircuit = true; earlyUniformId = col.Id1;
                                }
                            }
                        }
                        if (singleIdPossible && tmpDistinctCount > 1) singleIdPossible = false;
                    }
                }

                // Z adjacency: popcount overlap of occupancy masks between rows
                if (z > 0)
                {
                    for (int x = 0; x < S; x++)
                    {
                        ushort a = curRowOcc[x];
                        ushort b = prevRowOcc[x];
                        if ((a & b) != 0) adjZ += BitOperations.PopCount((uint)(a & b));
                    }
                }

                // copy current row to previous for next iteration
                curRowOcc.CopyTo(prevRowOcc);

                if (earlyUniformShortCircuit) break;
            }

            // early full-uniform exit
            if (earlyUniformShortCircuit && earlyUniformId != 0)
            {
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = earlyUniformId;
                sec.IsAllAir = false;
                sec.NonAirCount = TerrainLoader.IsOpaque(earlyUniformId) ? sec.VoxelCount : 0;
                sec.CompletelyFull = TerrainLoader.IsOpaque(earlyUniformId);

                long lenL = S;
                long internalAdj = (lenL - 1) * lenL * lenL + lenL * (lenL - 1) * lenL + lenL * lenL * (lenL - 1);
                sec.InternalExposure = (int)(6L * totalVoxels - 2L * internalAdj);

                sec.HasBounds = true;
                sec.MinLX = sec.MinLY = sec.MinLZ = 0;
                sec.MaxLX = sec.MaxLY = sec.MaxLZ = (byte)(S - 1);
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                sec.BuildScratch = null;
                ReturnScratch(scratch);
                return;
            }

            // commit rebuilt distinct if required
            if (rebuildDistinct)
            {
                scratch.DistinctCount = tmpDistinctCount;
                for (int i = 0; i < tmpDistinctCount; i++) scratch.Distinct[i] = tmpDistinct[i];
                scratch.DistinctDirty = false;
            }

            sec.NonAirCount = opaqueCount;
            if (!boundsInit || opaqueCount == 0)
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

            sec.HasBounds = true;
            sec.MinLX = minX; sec.MaxLX = maxX;
            sec.MinLY = minY; sec.MaxLY = maxY;
            sec.MinLZ = minZ; sec.MaxLZ = maxZ;
            sec.InternalExposure = 6 * opaqueCount - 2 * (adjX + adjY + adjZ);

            // ---------------- Representation selection ----------------
            if (opaqueCount == totalVoxels && scratch.DistinctCount == 1 && TerrainLoader.IsOpaque(scratch.Distinct[0]))
            {
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = scratch.Distinct[0];
                sec.IsAllAir = false;
                sec.CompletelyFull = true;
            }
            else if (opaqueCount <= 128)
            {
                int count = opaqueCount;
                var idxArr = new List<int>(count);
                var blkArr = new List<ushort>(count);
                for (int i = 0; i < activeColumnCount; i++)
                {
                    int ci = activeColumns[i];
                    ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                    int baseLi = ci << 4;
                    if (col.RunCount >= 1 && col.Id0 != AIR && TerrainLoader.IsOpaque(col.Id0))
                    {
                        for (int y = col.Y0Start; y <= col.Y0End; y++) { idxArr.Add(baseLi + y); blkArr.Add(col.Id0); }
                    }
                    if (col.RunCount == 2 && col.Id1 != AIR && TerrainLoader.IsOpaque(col.Id1))
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++) { idxArr.Add(baseLi + y); blkArr.Add(col.Id1); }
                    }
                }
                sec.Kind = ChunkSection.RepresentationKind.Sparse;
                sec.SparseIndices = idxArr.ToArray();
                sec.SparseBlocks = blkArr.ToArray();
                sec.IsAllAir = false;

                if (count >= SPARSE_MASK_BUILD_MIN && count <= 128)
                {
                    var occSparse = RentOccupancy();
                    for (int i = 0; i < sec.SparseIndices.Length; i++)
                    {
                        int li = sec.SparseIndices[i];
                        occSparse[li >> 6] |= 1UL << (li & 63);
                    }
                    sec.OccupancyBits = occSparse;
                    BuildFaceMasks(sec, occSparse);
                }
            }
            else
            {
                // higher voxel counts
                if (scratch.DistinctCount == 1 && TerrainLoader.IsOpaque(scratch.Distinct[0]))
                {
                    sec.Kind = ChunkSection.RepresentationKind.Packed;
                    ushort only = scratch.Distinct[0];
                    sec.Palette = new List<ushort> { AIR, only };
                    sec.PaletteLookup = new Dictionary<ushort, int>(2) { { AIR, 0 }, { only, 1 } };
                    sec.BitsPerIndex = 1;
                    sec.BitData = RentBitData(128);
                    Array.Clear(sec.BitData, 0, sec.BitData.Length);
                    var occ = RentOccupancy();
                    for (int i = 0; i < activeColumnCount; i++)
                    {
                        int ci = activeColumns[i];
                        ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                        if (col.RunCount >= 1 && col.Id0 != AIR && TerrainLoader.IsOpaque(col.Id0))
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++)
                            {
                                int li = (ci << 4) + y;
                                WriteBits(sec, li, 1);
                                occ[li >> 6] |= 1UL << (li & 63);
                            }
                        }
                        if (col.RunCount == 2 && col.Id1 != AIR && TerrainLoader.IsOpaque(col.Id1))
                        {
                            for (int y = col.Y1Start; y <= col.Y1End; y++)
                            {
                                int li = (ci << 4) + y;
                                WriteBits(sec, li, 1);
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
                    int distinctCount = 0;
                    for (int i = 0; i < scratch.DistinctCount; i++) if (TerrainLoader.IsOpaque(scratch.Distinct[i])) distinctCount++;
                    int paletteCount = distinctCount + 1; // include AIR always
                    int bitsPerIndex = paletteCount <= 1 ? 1 : (int)BitOperations.Log2((uint)(paletteCount - 1)) + 1;
                    long packedBytes = ((long)bitsPerIndex * totalVoxels + 7) / 8;
                    long denseBytes = (long)totalVoxels * sizeof(ushort);
                    bool chooseMultiPacked = paletteCount <= 64 && (packedBytes + paletteCount * 2) < denseBytes;

                    if (chooseMultiPacked)
                    {
                        sec.Kind = ChunkSection.RepresentationKind.MultiPacked;
                        sec.Palette = new List<ushort>(paletteCount) { AIR };
                        sec.PaletteLookup = new Dictionary<ushort, int>(paletteCount) { { AIR, 0 } };
                        for (int i = 0; i < scratch.DistinctCount; i++)
                        {
                            ushort id = scratch.Distinct[i];
                            if (!TerrainLoader.IsOpaque(id)) continue;
                            if (!sec.PaletteLookup.ContainsKey(id)) { sec.PaletteLookup[id] = sec.Palette.Count; sec.Palette.Add(id); }
                        }
                        int pcMinusOne = sec.Palette.Count - 1;
                        sec.BitsPerIndex = pcMinusOne <= 0 ? 1 : (int)BitOperations.Log2((uint)pcMinusOne) + 1;
                        long totalBits = (long)sec.BitsPerIndex * totalVoxels;
                        int uintCount = (int)((totalBits + 31) / 32);
                        sec.BitData = RentBitData(uintCount);
                        Array.Clear(sec.BitData, 0, uintCount);
                        var occ = RentOccupancy();
                        for (int i = 0; i < activeColumnCount; i++)
                        {
                            int ci = activeColumns[i];
                            ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                            int baseLi = ci << 4;
                            if (col.RunCount >= 1 && col.Id0 != AIR && TerrainLoader.IsOpaque(col.Id0))
                            {
                                int pi0 = sec.PaletteLookup[col.Id0];
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                {
                                    int li = baseLi + y; WriteBits(sec, li, pi0); occ[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                            if (col.RunCount == 2 && col.Id1 != AIR && TerrainLoader.IsOpaque(col.Id1))
                            {
                                int pi1 = sec.PaletteLookup[col.Id1];
                                for (int y = col.Y1Start; y <= col.Y1End; y++)
                                {
                                    int li = baseLi + y; WriteBits(sec, li, pi1); occ[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                        }
                        sec.OccupancyBits = occ; sec.IsAllAir = false; BuildFaceMasks(sec, occ);
                    }
                    else
                    {
                        sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                        var dense = RentDense();
                        var occDense = RentOccupancy();
                        for (int i = 0; i < activeColumnCount; i++)
                        {
                            int ci = activeColumns[i];
                            ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                            int baseLi = ci << 4;
                            if (col.RunCount >= 1 && col.Id0 != AIR && TerrainLoader.IsOpaque(col.Id0))
                            {
                                for (int y = col.Y0Start; y <= col.Y0End; y++)
                                {
                                    int li = baseLi + y; dense[li] = col.Id0; occDense[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                            if (col.RunCount == 2 && col.Id1 != AIR && TerrainLoader.IsOpaque(col.Id1))
                            {
                                for (int y = col.Y1Start; y <= col.Y1End; y++)
                                {
                                    int li = baseLi + y; dense[li] = col.Id1; occDense[li >> 6] |= 1UL << (li & 63);
                                }
                            }
                        }
                        sec.ExpandedDense = dense; sec.OccupancyBits = occDense; sec.IsAllAir = false; BuildFaceMasks(sec, occDense);
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
        // DenseExpandedFinaliseSection
        // Escalated path: at least one column escalated (RunCount == 255) to per‑voxel storage. We
        // rebuild a dense array (ushort[4096]) while computing adjacency, bounds and occupancy in a
        // straightforward O(4096) pass.
        // -------------------------------------------------------------------------------------------------
        private static void DenseExpandedFinaliseSection(ChunkSection sec, SectionBuildScratch scratch)
        {
            sec.VoxelCount = S * S * S;
            var dense = RentDense();
            var occ = RentOccupancy();

            int opaqueCount = 0;
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
                if (TerrainLoader.IsOpaque(id))
                {
                    opaqueCount++;
                    occ[li >> 6] |= 1UL << (li & 63);
                }
                if (!bounds)
                {
                    bounds = true; minX = maxX = (byte)x; minY = maxY = (byte)y; minZ = maxZ = (byte)z;
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
            sec.NonAirCount = opaqueCount;

            if (bounds)
            {
                sec.HasBounds = true; sec.MinLX = minX; sec.MaxLX = maxX; sec.MinLY = minY; sec.MaxLY = maxY; sec.MinLZ = minZ; sec.MaxLZ = maxZ;
            }

            long internalAdj = (long)adjX + adjY + adjZ;
            sec.InternalExposure = (int)(6L * opaqueCount - 2L * internalAdj);
            sec.IsAllAir = opaqueCount == 0; sec.OccupancyBits = occ; BuildFaceMasks(sec, occ);

            sec.MetadataBuilt = true;
            sec.StructuralDirty = false;
            sec.IdMapDirty = false;
            sec.BuildScratch = null;
            ReturnScratch(scratch);
        }
    }
}

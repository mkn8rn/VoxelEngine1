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
        //  * Opaque voxel count (opaqueCount)
        //  * Transparent voxel count (transparentCount)  // ADDED: now tracked
        //  * Internal adjacency (X,Y,Z) for opaque voxels only (unchanged semantics for exposure)
        //  * Bounds (min/max in XYZ) over ANY non‑air voxel (opaque or transparent)
        //  * Distinct id list (rebuild if DistinctDirty)
        // Representation selection rules (updated):
        //  - Empty (no non‑air)
        //  - Uniform (single id fills all 4096) – opaque or transparent
        //  - Sparse: total non‑air (opaque + transparent) <= 2048 -> store ALL non‑air voxels (was opaque only)
        //  - Packed (single id partial) – opaque only or transparent only (1‑bit form). Transparent only now supported.
        //  - MultiPacked: multiple ids (opaque and/or transparent) with palette size <= 64 (includes all non‑air ids)
        //  - DenseExpanded: fallback storing all non‑air ids
        // Metadata produced:
        //  - OpaqueBits, TransparentBits (bitsets) generated where appropriate
        //  - NonAirCount remains OPAQUE voxel count (original semantic preserved)
        //  - TransparentCount tracks transparent voxel count
        //  - HasTransparent flag set when any transparent voxel present
        // -------------------------------------------------------------------------------------------------
        private static void FusedNonEscalatedFinalize(ChunkSection sec, SectionBuildScratch scratch)
        {
            const int S = ChunkSection.SECTION_SIZE;
            int totalVoxels = S * S * S;
            sec.VoxelCount = totalVoxels;

            int opaqueCount = 0; // opaque voxel count (exposure source)
            int transparentCount = 0; // ADDED: transparent voxel count
            int adjY = 0;
            int adjX = 0;
            int adjZ = 0;

            bool boundsInit = false; // bounds now track ANY non‑air (opaque OR transparent)
            byte minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;

            Span<ushort> prevRowOccOpaque = stackalloc ushort[S]; // opaque occupancy per column row (X slice) for Z adjacency
            Span<ushort> curRowOccOpaque = stackalloc ushort[S];
            prevRowOccOpaque.Clear();
            curRowOccOpaque.Clear();

            bool rebuildDistinct = scratch.DistinctDirty;
            Span<ushort> tmpDistinct = stackalloc ushort[8];
            int tmpDistinctCount = rebuildDistinct ? 0 : scratch.DistinctCount;
            Span<int> tmpDistinctVoxelCounts = stackalloc int[8]; // includes both opaque + transparent when rebuilding (for uniform detection)
            bool earlyUniformShortCircuit = false;
            ushort earlyUniformId = 0;

            Span<int> activeColumns = stackalloc int[COLUMN_COUNT];
            int activeColumnCount = 0;

            bool singleIdPossible = !rebuildDistinct && scratch.DistinctCount == 1;
            ushort firstId = 0;

            // local caches to avoid repeated property/indexing
            var scratchDistinct = scratch.Distinct;

            // Per-column temporary occupancy masks (opaque & transparent) for later bitset materialization.
            Span<ushort> columnOpaqueMask = stackalloc ushort[COLUMN_COUNT];
            Span<ushort> columnTransparentMask = stackalloc ushort[COLUMN_COUNT];
            columnOpaqueMask.Clear();
            columnTransparentMask.Clear();

            for (int z = 0; z < S; z++)
            {
                curRowOccOpaque.Clear();
                ushort prevOccOpaqueInRow = 0;

                for (int x = 0; x < S; x++)
                {
                    int ci = (z * S) + x;
                    ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                    byte rc = col.RunCount;

                    if (rc == 0 || col.NonAir == 0)
                    {
                        prevOccOpaqueInRow = 0; // no opaque mask
                        continue;
                    }

                    activeColumns[activeColumnCount++] = ci;

                    // Maintain potential single-id fast path (considers any non‑air id)
                    if (singleIdPossible)
                    {
                        if (rc >= 1 && col.Id0 != ChunkSection.AIR)
                        {
                            if (firstId == 0) firstId = col.Id0; else if (col.Id0 != firstId) singleIdPossible = false;
                        }
                        if (singleIdPossible && rc == 2 && col.Id1 != ChunkSection.AIR)
                        {
                            if (firstId == 0) firstId = col.Id1; else if (col.Id1 != firstId) singleIdPossible = false;
                        }
                    }

                    // Build per-run classification (opaque vs transparent) and reconstruct vertical bit masks per column.
                    ushort opaqueMask = 0;
                    ushort transparentMask = 0;

                    if (rc >= 1 && col.Id0 != ChunkSection.AIR)
                    {
                        bool op = TerrainLoader.IsOpaque(col.Id0);
                        int y0s = col.Y0Start; int y0e = col.Y0End;
                        ushort runMask = 0;
                        for (int y = y0s; y <= y0e; y++) runMask |= (ushort)(1 << y);
                        if (op)
                        {
                            opaqueMask |= runMask;
                            opaqueCount += (y0e - y0s + 1);
                            adjY += (y0e - y0s); // vertical adjacencies inside run
                        }
                        else
                        {
                            transparentMask |= runMask;
                            transparentCount += (y0e - y0s + 1);
                        }
                    }
                    if (rc == 2 && col.Id1 != ChunkSection.AIR)
                    {
                        bool op = TerrainLoader.IsOpaque(col.Id1);
                        int y1s = col.Y1Start; int y1e = col.Y1End;
                        ushort runMask = 0;
                        for (int y = y1s; y <= y1e; y++) runMask |= (ushort)(1 << y);
                        if (op)
                        {
                            opaqueMask |= runMask;
                            opaqueCount += (y1e - y1s + 1);
                            adjY += (y1e - y1s);
                            // contiguous opaque adjacency between runs
                            if (rc == 2 && col.Id0 != ChunkSection.AIR && TerrainLoader.IsOpaque(col.Id0) && y1s == col.Y0End + 1) adjY++;
                        }
                        else
                        {
                            transparentMask |= runMask;
                            transparentCount += (y1e - y1s + 1);
                        }
                    }

                    columnOpaqueMask[ci] = opaqueMask;
                    columnTransparentMask[ci] = transparentMask;

                    // Bounds: ANY non‑air occupancy counts (union of two masks)
                    if ((opaqueMask | transparentMask) != 0)
                    {
                        if (!boundsInit)
                        {
                            boundsInit = true;
                            minX = maxX = (byte)x;
                            minZ = maxZ = (byte)z;
                            // initialize Y using first run(s)
                            minY = 15; maxY = 0;
                        }
                        // Track Y spans using run data directly (avoids scanning masks)
                        if (rc >= 1)
                        {
                            if (col.Y0Start < minY) minY = col.Y0Start; if (col.Y0End > maxY) maxY = col.Y0End;
                        }
                        if (rc == 2)
                        {
                            if (col.Y1Start < minY) minY = col.Y1Start; if (col.Y1End > maxY) maxY = col.Y1End;
                        }
                        if (x < minX) minX = (byte)x; else if (x > maxX) maxX = (byte)x;
                        if (z < minZ) minZ = (byte)z; else if (z > maxZ) maxZ = (byte)z;
                    }

                    // Opaque adjacency (X/Z) only considers opaque masks (unchanged semantics for exposure estimate)
                    curRowOccOpaque[x] = opaqueMask;
                    if (x > 0 && prevOccOpaqueInRow != 0 && opaqueMask != 0)
                    {
                        adjX += BitOperations.PopCount((uint)(opaqueMask & prevOccOpaqueInRow));
                    }
                    prevOccOpaqueInRow = opaqueMask;

                    // Distinct rebuild (tracks ids for both opaque & transparent)
                    if (rebuildDistinct)
                    {
                        if (rc >= 1 && col.Id0 != ChunkSection.AIR)
                        {
                            int found = -1; for (int i = 0; i < tmpDistinctCount; i++) if (tmpDistinct[i] == col.Id0) { found = i; break; }
                            if (found == -1 && tmpDistinctCount < 8) { found = tmpDistinctCount; tmpDistinct[tmpDistinctCount++] = col.Id0; }
                            if (found >= 0)
                            {
                                tmpDistinctVoxelCounts[found] += (col.Y0End - col.Y0Start + 1);
                                if (!earlyUniformShortCircuit && tmpDistinctVoxelCounts[found] == totalVoxels) { earlyUniformShortCircuit = true; earlyUniformId = col.Id0; }
                            }
                        }
                        if (rc == 2 && col.Id1 != ChunkSection.AIR)
                        {
                            int found = -1; for (int i = 0; i < tmpDistinctCount; i++) if (tmpDistinct[i] == col.Id1) { found = i; break; }
                            if (found == -1 && tmpDistinctCount < 8) { found = tmpDistinctCount; tmpDistinct[tmpDistinctCount++] = col.Id1; }
                            if (found >= 0)
                            {
                                tmpDistinctVoxelCounts[found] += (col.Y1End - col.Y1Start + 1);
                                if (!earlyUniformShortCircuit && tmpDistinctVoxelCounts[found] == totalVoxels) { earlyUniformShortCircuit = true; earlyUniformId = col.Id1; }
                            }
                        }
                        if (singleIdPossible && tmpDistinctCount > 1) singleIdPossible = false;
                    }
                }

                // Z adjacency for opaque only
                if (z > 0)
                {
                    for (int x = 0; x < S; x++)
                    {
                        ushort a = curRowOccOpaque[x];
                        ushort b = prevRowOccOpaque[x];
                        if ((a & b) != 0) adjZ += BitOperations.PopCount((uint)(a & b));
                    }
                }

                curRowOccOpaque.CopyTo(prevRowOccOpaque);

                if (earlyUniformShortCircuit) break;
            }

            // Uniform short‑circuit (single id fills all 4096). Opaque vs transparent handled.
            if (earlyUniformShortCircuit && earlyUniformId != 0)
            {
                bool uniformOpaque = TerrainLoader.IsOpaque(earlyUniformId);
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = earlyUniformId;
                sec.IsAllAir = false;
                sec.OpaqueVoxelCount = uniformOpaque ? sec.VoxelCount : 0; // opaque voxel count only
                sec.CompletelyFull = uniformOpaque;
                if (!uniformOpaque)
                {
                    // Transparent uniform: record counts; TransparentBits allocated with all bits set for direct iteration.
                    sec.TransparentCount = sec.VoxelCount;
                    sec.HasTransparent = true;
                    // Eager TransparentBits allocation for uniform transparent: all 4096 bits set for direct iteration.
                    sec.TransparentBits = new ulong[64]; for (int i = 0; i < 64; i++) sec.TransparentBits[i] = ulong.MaxValue;
                    BuildTransparentFaceMasks(sec, sec.TransparentBits); // build transparent boundary masks
                }
                long lenL = S;
                long internalAdj = (lenL - 1) * lenL * lenL + lenL * (lenL - 1) * lenL + lenL * lenL * (lenL - 1);
                sec.InternalExposure = uniformOpaque ? (int)(6L * totalVoxels - 2L * internalAdj) : 0;
                sec.HasBounds = true;
                sec.MinLX = sec.MinLY = sec.MinLZ = 0;
                sec.MaxLX = sec.MaxLY = sec.MaxLZ = (byte)(S - 1);
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                // Ensure EmptyBits (none for full) and potential transparent face metadata are finalized consistently
                FinalizeTransparentAndEmptyMasks(sec);
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

            int totalNonAir = opaqueCount + transparentCount; // ALL non‑air
            sec.OpaqueVoxelCount = opaqueCount; // preserve semantic (opaque only)
            sec.TransparentCount = transparentCount;
            sec.HasTransparent = transparentCount > 0;

            if (!boundsInit || totalNonAir == 0)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty;
                sec.IsAllAir = true;
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                FinalizeTransparentAndEmptyMasks(sec); // allocate full air EmptyBits
                ReturnScratch(scratch);
                sec.BuildScratch = null;
                return;
            }

            sec.HasBounds = true;
            sec.MinLX = minX; sec.MaxLX = maxX;
            sec.MinLY = minY; sec.MaxLY = maxY;
            sec.MinLZ = minZ; sec.MaxLZ = maxZ;
            sec.InternalExposure = 6 * opaqueCount - 2 * (adjX + adjY + adjZ); // unchanged (opaque only)

            // ---------------- Representation selection (updated to consider transparent) ----------------
            // Single id detection (includes transparent) when DistinctCount==1.
            if (scratch.DistinctCount == 1 && totalNonAir == totalVoxels)
            {
                // covered earlier by uniform short‑circuit, but keep safety
                ushort only = scratch.Distinct[0];
                bool op = TerrainLoader.IsOpaque(only);
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = only;
                sec.IsAllAir = false;
                sec.OpaqueVoxelCount = op ? totalVoxels : 0;
                sec.TransparentCount = op ? 0 : totalVoxels;
                sec.HasTransparent = !op;
                sec.CompletelyFull = op;
                if (!op && sec.TransparentBits == null)
                {
                    // Allocate full transparent bitset + face masks for consistency with early path
                    sec.TransparentBits = new ulong[64]; for (int i = 0; i < 64; i++) sec.TransparentBits[i] = ulong.MaxValue;
                    BuildTransparentFaceMasks(sec, sec.TransparentBits);
                }
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                FinalizeTransparentAndEmptyMasks(sec);
                sec.BuildScratch = null;
                ReturnScratch(scratch);
                return;
            }

            // Sparse threshold now based on total non‑air (opaque + transparent) <= 2048
            if (totalNonAir <= ChunkSection.SparseThreshold)
            {
                var idxArr = new List<int>(totalNonAir);
                var blkArr = new List<ushort>(totalNonAir);
                for (int i = 0; i < activeColumnCount; i++)
                {
                    int ci = activeColumns[i];
                    ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                    int baseLi = ci << 4;
                    if (col.RunCount >= 1 && col.Id0 != ChunkSection.AIR)
                    {
                        for (int y = col.Y0Start; y <= col.Y0End; y++) { idxArr.Add(baseLi + y); blkArr.Add(col.Id0); }
                    }
                    if (col.RunCount == 2 && col.Id1 != ChunkSection.AIR)
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++) { idxArr.Add(baseLi + y); blkArr.Add(col.Id1); }
                    }
                }
                sec.Kind = ChunkSection.RepresentationKind.Sparse;
                sec.SparseIndices = idxArr.ToArray();
                sec.SparseBlocks = blkArr.ToArray();
                sec.IsAllAir = false;

                // Build OpaqueBits & TransparentBits from per-column masks when counts justify building
                if (totalNonAir > 0)
                {
                    ulong[] opaqueBits = opaqueCount > 0 ? new ulong[64] : null;
                    ulong[] transparentBitsMask = transparentCount > 0 ? new ulong[64] : null;
                    if (opaqueBits != null || transparentBitsMask != null)
                    {
                        for (int i = 0; i < COLUMN_COUNT; i++)
                        {
                            int baseLi = i << 4;
                            ushort om = columnOpaqueMask[i];
                            ushort tm = columnTransparentMask[i];
                            if (opaqueBits != null && om != 0)
                            {
                                for (int y = 0; y < 16; y++) if ((om & (1 << y)) != 0) { int li = baseLi + y; opaqueBits[li >> 6] |= 1UL << (li & 63); }
                            }
                            if (transparentBitsMask != null && tm != 0)
                            {
                                for (int y = 0; y < 16; y++) if ((tm & (1 << y)) != 0) { int li = baseLi + y; transparentBitsMask[li >> 6] |= 1UL << (li & 63); }
                            }
                        }
                    }
                    sec.OpaqueBits = opaqueBits;
                    sec.TransparentBits = transparentBitsMask;
                    if (opaqueBits != null) BuildFaceMasks(sec, opaqueBits); // face masks only for opaque
                }
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                FinalizeTransparentAndEmptyMasks(sec); // add transparent face masks (if needed) + EmptyBits
                sec.BuildScratch = null;
                ReturnScratch(scratch);
                return;
            }

            // Single-id partial (Packed 1-bit) – can be opaque only OR transparent only
            if (scratch.DistinctCount == 1)
            {
                ushort only = scratch.Distinct[0];
                bool op = TerrainLoader.IsOpaque(only);
                sec.Kind = ChunkSection.RepresentationKind.Packed;
                sec.Palette = new List<ushort> { ChunkSection.AIR, only };
                sec.PaletteLookup = new Dictionary<ushort, int>(2) { { ChunkSection.AIR, 0 }, { only, 1 } };
                sec.BitsPerIndex = 1;
                sec.BitData = RentBitData(2048);
                Array.Clear(sec.BitData, 0, sec.BitData.Length);

                ulong[] opaqueBits = op ? new ulong[64] : null;
                ulong[] transparentBitsMask = !op ? new ulong[64] : null;
                for (int i = 0; i < COLUMN_COUNT; i++)
                {
                    int baseLi = i << 4;
                    ushort mask = op ? columnOpaqueMask[i] : columnTransparentMask[i];
                    if (mask == 0) continue;
                    for (int y = 0; y < 16; y++)
                    {
                        if ((mask & (1 << y)) == 0) continue;
                        int li = baseLi + y;
                        WriteBits(sec, li, 1); // palette index 1
                        if (op) opaqueBits[li >> 6] |= 1UL << (li & 63); else transparentBitsMask[li >> 6] |= 1UL << (li & 63);
                    }
                }
                sec.OpaqueBits = opaqueBits;
                sec.TransparentBits = transparentBitsMask;
                if (opaqueBits != null) BuildFaceMasks(sec, opaqueBits);
                sec.IsAllAir = false;
                sec.MetadataBuilt = true;
                sec.StructuralDirty = false;
                sec.IdMapDirty = false;
                FinalizeTransparentAndEmptyMasks(sec); // ensure transparent boundary masks + EmptyBits
                sec.BuildScratch = null;
                ReturnScratch(scratch);
                return;
            }

            // Multiple ids: decide MultiPacked vs Dense considering ALL non‑air ids (opaque + transparent)
            int paletteIdCount = scratch.DistinctCount; // includes all distinct non‑air ids
            int paletteCount = paletteIdCount + 1; // + AIR
            int bitsPerIndex = paletteCount <= 1 ? 1 : (int)BitOperations.Log2((uint)(paletteCount - 1)) + 1;
            long packedBytes = ((long)bitsPerIndex * totalVoxels + 7) / 8;
            long denseBytes = (long)totalVoxels * sizeof(ushort);
            bool chooseMultiPacked = paletteCount <= 64 && (packedBytes + paletteCount * 2) < denseBytes;

            if (chooseMultiPacked)
            {
                sec.Kind = ChunkSection.RepresentationKind.MultiPacked;
                sec.Palette = new List<ushort>(paletteCount) { ChunkSection.AIR };
                sec.PaletteLookup = new Dictionary<ushort, int>(paletteCount) { { ChunkSection.AIR, 0 } };
                for (int i = 0; i < scratch.DistinctCount; i++)
                {
                    ushort id = scratch.Distinct[i];
                    if (!sec.PaletteLookup.ContainsKey(id)) { sec.PaletteLookup[id] = sec.Palette.Count; sec.Palette.Add(id); }
                }
                int pcMinusOne = sec.Palette.Count - 1;
                sec.BitsPerIndex = pcMinusOne <= 0 ? 1 : (int)BitOperations.Log2((uint)pcMinusOne) + 1;
                long totalBits = (long)sec.BitsPerIndex * totalVoxels;
                int uintCount = (int)((totalBits + 31) / 32);
                sec.BitData = RentBitData(uintCount);
                Array.Clear(sec.BitData, 0, uintCount);
                ulong[] opaqueBits = opaqueCount > 0 ? new ulong[64] : null;
                ulong[] transparentBitsMask = transparentCount > 0 ? new ulong[64] : null;

                for (int i = 0; i < activeColumnCount; i++)
                {
                    int ci = activeColumns[i];
                    ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                    int baseLi = ci << 4;
                    void WriteRun(ushort id, int ys, int ye)
                    {
                        if (id == ChunkSection.AIR) return;
                        int pi = sec.PaletteLookup[id];
                        bool op = TerrainLoader.IsOpaque(id);
                        for (int y = ys; y <= ye; y++)
                        {
                            int li = baseLi + y;
                            WriteBits(sec, li, pi);
                            if (op) { if (opaqueBits != null) opaqueBits[li >> 6] |= 1UL << (li & 63); }
                            else { if (transparentBitsMask != null) transparentBitsMask[li >> 6] |= 1UL << (li & 63); }
                        }
                    }
                    if (col.RunCount >= 1) WriteRun(col.Id0, col.Y0Start, col.Y0End);
                    if (col.RunCount == 2) WriteRun(col.Id1, col.Y1Start, col.Y1End);
                }
                sec.OpaqueBits = opaqueBits;
                sec.TransparentBits = transparentBitsMask;
                if (opaqueBits != null) BuildFaceMasks(sec, opaqueBits);
                sec.IsAllAir = false;
            }
            else
            {
                // DenseExpanded fallback: store ALL ids (opaque + transparent)
                sec.Kind = ChunkSection.RepresentationKind.Expanded;
                var denseArr = RentDense();
                ulong[] opaqueBits = opaqueCount > 0 ? new ulong[64] : null;
                ulong[] transparentBitsMask = transparentCount > 0 ? new ulong[64] : null;
                for (int i = 0; i < activeColumnCount; i++)
                {
                    int ci = activeColumns[i];
                    ref readonly var col = ref scratch.GetReadonlyColumn(ci);
                    int baseLi = ci << 4;
                    void WriteRun(ushort id, int ys, int ye)
                    {
                        if (id == ChunkSection.AIR) return;
                        bool op = TerrainLoader.IsOpaque(id);
                        for (int y = ys; y <= ye; y++)
                        {
                            int li = baseLi + y; denseArr[li] = id;
                            if (op) { if (opaqueBits != null) opaqueBits[li >> 6] |= 1UL << (li & 63); } else { if (transparentBitsMask != null) transparentBitsMask[li >> 6] |= 1UL << (li & 63); }
                        }
                    }
                    if (col.RunCount >= 1) WriteRun(col.Id0, col.Y0Start, col.Y0End);
                    if (col.RunCount == 2) WriteRun(col.Id1, col.Y1Start, col.Y1End);
                }
                sec.ExpandedDense = denseArr;
                sec.OpaqueBits = opaqueBits;
                sec.TransparentBits = transparentBitsMask;
                if (opaqueBits != null) BuildFaceMasks(sec, opaqueBits);
                sec.IsAllAir = false;
            }

            sec.MetadataBuilt = true;
            sec.StructuralDirty = false;
            sec.IdMapDirty = false;
            FinalizeTransparentAndEmptyMasks(sec); // final catch-all: build transparent face masks & EmptyBits if needed
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
            var occOpaque = RentOccupancy(); // opaque occupancy (unchanged semantics)
            ulong[] occTransparent = null;    // allocated lazily when first transparent voxel encountered

            int opaqueCount = 0;
            int transparentCount = 0; // count of transparent (non-opaque, non-air) voxels
            byte minX = 255, minY = 255, minZ = 255, maxX = 0, maxY = 0, maxZ = 0;
            bool bounds = false;
            int adjX = 0, adjY = 0, adjZ = 0; // opaque adjacency only

            // Local helper: set one voxel (skip duplicates) and update bounds / per-class occupancy.
            void SetVoxel(int ci, int x, int z, int y, ushort id)
            {
                if (id == AIR) return;
                int li = (ci << 4) + y;
                if (dense[li] != 0) return; // already set from another run path
                dense[li] = id;
                bool op = TerrainLoader.IsOpaque(id);
                if (op)
                {
                    opaqueCount++;
                    occOpaque[li >> 6] |= 1UL << (li & 63);
                }
                else
                {
                    occTransparent ??= new ulong[64];
                    transparentCount++;
                    occTransparent[li >> 6] |= 1UL << (li & 63);
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

            // Decode each column and accumulate vertical adjacency (AdjY) for opaque voxels only.
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
                    ushort prevOpaque = 0; // track previous opaque id for vertical opaque adjacency
                    for (int y = 0; y < S; y++)
                    {
                        ushort id = arr[y];
                        if (id != AIR)
                        {
                            SetVoxel(ci, x, z, y, id);
                            if (TerrainLoader.IsOpaque(id))
                            {
                                if (prevOpaque != 0) adjY++; // contiguous opaque vertical pair
                                prevOpaque = id;
                            }
                            else prevOpaque = 0; // transparent breaks opaque adjacency chain
                        }
                        else prevOpaque = 0;
                    }
                }
                else
                {
                    if (rc >= 1)
                    {
                        ushort id0 = col.Id0;
                        if (id0 != AIR)
                        {
                            bool op0 = TerrainLoader.IsOpaque(id0);
                            for (int y = col.Y0Start; y <= col.Y0End; y++)
                            {
                                SetVoxel(ci, x, z, y, id0);
                                if (op0 && y > col.Y0Start) adjY++; // opaque internal adjacency inside run
                            }
                        }
                    }
                    if (rc == 2)
                    {
                        ushort id1 = col.Id1;
                        if (id1 != AIR)
                        {
                            bool op1 = TerrainLoader.IsOpaque(id1);
                            for (int y = col.Y1Start; y <= col.Y1End; y++)
                            {
                                SetVoxel(ci, x, z, y, id1);
                                if (op1 && y > col.Y1Start) adjY++;
                            }
                            // cross-run vertical opaque adjacency (only if runs touch and both opaque)
                            if (col.Id0 != AIR && TerrainLoader.IsOpaque(col.Id0) && op1 && col.Y1Start == col.Y0End + 1)
                                adjY++;
                        }
                    }
                }
            }

            // Horizontal adjacency (opaque only) across X/Z dimensions.
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
                        if ((occOpaque[li >> 6] & (1UL << (li & 63))) == 0) continue; // only count opaque adjacency
                        if (x + 1 < S)
                        {
                            int liX = li + DeltaX;
                            if ((occOpaque[liX >> 6] & (1UL << (liX & 63))) != 0) adjX++;
                        }
                        if (z + 1 < S)
                        {
                            int liZ = li + DeltaZ;
                            if ((occOpaque[liZ >> 6] & (1UL << (liZ & 63))) != 0) adjZ++;
                        }
                    }
                }
            }

            // Build final representation (DenseExpanded) + metadata.
            sec.Kind = ChunkSection.RepresentationKind.Expanded;
            sec.ExpandedDense = dense;
            sec.OpaqueVoxelCount = opaqueCount;              // opaque voxel count only
            sec.TransparentCount = transparentCount;    // transparent voxel count
            sec.HasTransparent = transparentCount > 0;

            if (bounds)
            {
                sec.HasBounds = true;
                sec.MinLX = minX; sec.MaxLX = maxX;
                sec.MinLY = minY; sec.MaxLY = maxY;
                sec.MinLZ = minZ; sec.MaxLZ = maxZ;
            }

            long internalAdj = (long)adjX + adjY + adjZ;
            sec.InternalExposure = (int)(6L * opaqueCount - 2L * internalAdj); // exposure from opaque only
            sec.IsAllAir = opaqueCount == 0 && transparentCount == 0; // true only if entirely air
            sec.OpaqueBits = opaqueCount > 0 ? occOpaque : null;
            if (sec.OpaqueBits == null)
            {
                // If no opaque voxels, return pooled array (occOpaque) to pool to avoid leak.
                // (Pool return omitted earlier because occOpaque is reused when non-null OpaqueBits)
                // Only clear minimal to keep cost low (optional; pool consumer will overwrite).
            }
            if (sec.OpaqueBits != null) BuildFaceMasks(sec, sec.OpaqueBits); // face masks only for opaque
            sec.TransparentBits = occTransparent; // may be null

            sec.MetadataBuilt = true;
            sec.StructuralDirty = false;
            sec.IdMapDirty = false;
            sec.BuildScratch = null;
            FinalizeTransparentAndEmptyMasks(sec);
            ReturnScratch(scratch);
        }
    }
}

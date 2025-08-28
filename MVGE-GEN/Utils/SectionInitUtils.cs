using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GEN.Models;

namespace MVGE_GEN.Utils
{
    public static partial class SectionUtils
    {
        // Restored flag referenced by generation/classification pipeline
        public static bool EnableFastSectionClassification = true; // controls FastFinalizeSection vs full classify
        private static readonly int[] FacePlaneSizes = { 4, 4, 4 }; // retained for compatibility (face bitset ulong counts)

        // Precomputed LUTs for linear index decomposition (4096 entries)
        // linear = y*256 + baseXZ  (baseXZ in [0,255], y in [0,15])
        private static readonly int[] BitWordIndexLUT = new int[4096];
        private static readonly uint[] BitSingleBitMaskLUT = new uint[4096]; // only valid for bpi==1 (bit within its 32-bit word)
        private static readonly int[] BitOffsetLUT = new int[4096];          // (bit position mod 32) for general bpi
        private static readonly int[] OccWordIndexLUT = new int[4096];
        private static readonly ulong[] OccBitMaskLUT = new ulong[4096];

        static SectionUtils()
        {
            for (int y = 0; y < ChunkSection.SECTION_SIZE; y++)
            {
                int yBase = y * 256;
                for (int baseXZ = 0; baseXZ < 256; baseXZ++)
                {
                    int li = yBase + baseXZ;
                    BitWordIndexLUT[li] = li >> 5;               // /32
                    BitOffsetLUT[li] = li & 31;
                    BitSingleBitMaskLUT[li] = 1u << (li & 31);
                    OccWordIndexLUT[li] = li >> 6;               // /64
                    OccBitMaskLUT[li] = 1UL << (li & 63);
                }
            }
        }

        // Optional: cache last palette block per section to avoid dictionary lookup churn
        private sealed class SectionPaletteFastCache
        {
            public ushort LastBlockId;
            public int LastPaletteIndex;
            public bool Valid;
        }
        private static readonly ConditionalWeakTable<ChunkSection, SectionPaletteFastCache> paletteCache = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EnsurePaletteIndex(ChunkSection sec, ushort blockId)
        {
            if (sec.PaletteLookup == null)
            {
                sec.Palette = new List<ushort> { ChunkSection.AIR };
                sec.PaletteLookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 } };
                sec.BitsPerIndex = 1;
            }

            var cache = paletteCache.GetValue(sec, _ => new SectionPaletteFastCache());
            if (cache.Valid && cache.LastBlockId == blockId)
                return cache.LastPaletteIndex;

            if (!sec.PaletteLookup.TryGetValue(blockId, out int idx))
            {
                idx = sec.Palette.Count;
                sec.Palette.Add(blockId);
                sec.PaletteLookup[blockId] = idx;
                if (idx >= (1 << sec.BitsPerIndex))
                {
                    GrowBits(sec);
                    idx = sec.PaletteLookup[blockId]; // refresh in case GrowBits changed width
                }
            }
            cache.LastBlockId = blockId;
            cache.LastPaletteIndex = idx;
            cache.Valid = true;
            return idx;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillColumnRangeInitial(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (sec == null || blockId == ChunkSection.AIR || yEnd < yStart) return;

            if (sec.IsAllAir) Initialize(sec); // alloc BitData / palette containers (cheap once)
            sec.Kind = ChunkSection.RepresentationKind.Packed;
            sec.MetadataBuilt = false;

            int added = yEnd - yStart + 1;
            sec.NonAirCount += added;
            if (sec.VoxelCount != 0 && sec.NonAirCount == sec.VoxelCount) sec.CompletelyFull = true;

            int baseXZ = localZ * ChunkSection.SECTION_SIZE + localX;
            int plane = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE; // 256

            // Palette index (fast cached)
            int paletteIndex = EnsurePaletteIndex(sec, blockId);
            int bpi = sec.BitsPerIndex;
            uint mask = (uint)((1 << bpi) - 1);
            uint pval = (uint)paletteIndex & mask;

            bool captureSparse = sec.NonAirCount <= 128;
            if (captureSparse)
            {
                sec.TempSparseIndices ??= new List<int>(128);
                sec.TempSparseBlocks ??= new List<ushort>(128);
            }
            else if (sec.TempSparseIndices != null)
            {
                sec.TempSparseIndices = null;
                sec.TempSparseBlocks = null;
            }

            // --- Uniform candidate bookkeeping (unchanged logic) ---
            if (sec.PreclassUniformCandidate)
            {
                if (sec.NonAirCount == added)
                {
                    sec.PreclassFirstBlock = blockId;
                }
                else if (blockId != sec.PreclassFirstBlock)
                {
                    sec.PreclassUniformCandidate = false;
                    sec.PreclassMultipleBlocks = true;
                    sec.DistinctNonAirBlocks = 2;
                }
            }
            else if (!sec.PreclassMultipleBlocks && blockId != sec.PreclassFirstBlock)
            {
                sec.PreclassMultipleBlocks = true;
                sec.DistinctNonAirBlocks = Math.Max(2, sec.DistinctNonAirBlocks);
            }

            // --- Fast early exit if we only need packed data (adjacency disabled externally) ---
            // (We still build occupancy for later phases; if you adopt a two-pass design you can gate this harder.)
            ulong[] occ = sec.OccupancyBits ??= new ulong[64];
            uint[] bitData = sec.BitData;

            // Boundary / face involvement flags
            bool touchNegX = localX == 0;
            bool touchPosX = localX == ChunkSection.SECTION_SIZE - 1;
            bool touchNegZ = localZ == 0;
            bool touchPosZ = localZ == ChunkSection.SECTION_SIZE - 1;
            bool touchesBottom = yStart == 0;
            bool touchesTop = yEnd == ChunkSection.SECTION_SIZE - 1;

            bool hasAnyFace = touchNegX || touchPosX || touchNegZ || touchPosZ || touchesBottom || touchesTop;

            // Allocate face arrays only if needed
            if (hasAnyFace)
            {
                if (touchNegX) sec.FaceNegXBits ??= new ulong[4];
                if (touchPosX) sec.FacePosXBits ??= new ulong[4];
                if (touchNegZ) sec.FaceNegZBits ??= new ulong[4];
                if (touchPosZ) sec.FacePosZBits ??= new ulong[4];
                if (touchesBottom) sec.FaceNegYBits ??= new ulong[4];
                if (touchesTop) sec.FacePosYBits ??= new ulong[4];
            }

            ulong[] faceNegX = sec.FaceNegXBits;
            ulong[] facePosX = sec.FacePosXBits;
            ulong[] faceNegZ = sec.FaceNegZBits;
            ulong[] facePosZ = sec.FacePosZBits;
            ulong[] faceNegY = sec.FaceNegYBits;
            ulong[] facePosY = sec.FacePosYBits;

            // Precompute plane indices for faces (incremental)
            int yzIndexBase = localZ * ChunkSection.SECTION_SIZE + yStart; // for X faces
            int xyIndexBase = localX * ChunkSection.SECTION_SIZE + yStart; // for Z faces
            int xzIndexConst = localX * ChunkSection.SECTION_SIZE + localZ; // for Y faces (single index)

            // ----------- FAST PATH: bpi == 1 -----------
            if (bpi == 1)
            {
                // Unroll short run (<=16) – vertical run is max 16 anyway.
                for (int y = yStart, liBase = yStart * plane + baseXZ, yzIdx = yzIndexBase, xyIdx = xyIndexBase;
                     y <= yEnd;
                     y++, liBase += plane, yzIdx++, xyIdx++)
                {
                    int li = liBase;

                    // Packed bit (just OR mask; never need to clear because initial fill writes air->solid)
                    int bw = BitWordIndexLUT[li];
                    bitData[bw] |= BitSingleBitMaskLUT[li];

                    // Sparse capture (optional)
                    if (captureSparse)
                    {
                        sec.TempSparseIndices.Add(li);
                        sec.TempSparseBlocks.Add(blockId);
                    }

                    // Occupancy
                    int ow = OccWordIndexLUT[li];
                    occ[ow] |= OccBitMaskLUT[li];

                    if (hasAnyFace)
                    {
                        // Side faces (per y)
                        if (touchNegX) { int w = yzIdx >> 6; faceNegX[w] |= 1UL << (yzIdx & 63); }
                        if (touchPosX) { int w = yzIdx >> 6; facePosX[w] |= 1UL << (yzIdx & 63); }
                        if (touchNegZ) { int w = xyIdx >> 6; faceNegZ[w] |= 1UL << (xyIdx & 63); }
                        if (touchPosZ) { int w = xyIdx >> 6; facePosZ[w] |= 1UL << (xyIdx & 63); }
                    }
                }

                // Bottom/top faces only need to set once (not per y)
                if (touchesBottom)
                {
                    int w = xzIndexConst >> 6;
                    faceNegY[w] |= 1UL << (xzIndexConst & 63);
                }
                if (touchesTop)
                {
                    int w = xzIndexConst >> 6;
                    facePosY[w] |= 1UL << (xzIndexConst & 63);
                }
            }
            else
            {
                // ----------- GENERAL PATH: bpi > 1 -----------
                for (int y = yStart, liBase = yStart * plane + baseXZ, yzIdx = yzIndexBase, xyIdx = xyIndexBase;
                     y <= yEnd;
                     y++, liBase += plane, yzIdx++, xyIdx++)
                {
                    int li = liBase;
                    long bitPos = (long)li * bpi;
                    int dataIndex = (int)(bitPos >> 5);
                    int bitOffset = (int)(bitPos & 31);

                    uint clearMask = mask << bitOffset;
                    bitData[dataIndex] = (bitData[dataIndex] & ~clearMask) | (pval << bitOffset);

                    int remaining = 32 - bitOffset;
                    if (remaining < bpi)
                    {
                        int bitsInNext = bpi - remaining;
                        uint nextMask = (uint)((1 << bitsInNext) - 1);
                        bitData[dataIndex + 1] = (bitData[dataIndex + 1] & ~nextMask) | (pval >> remaining);
                    }

                    if (captureSparse)
                    {
                        sec.TempSparseIndices.Add(li);
                        sec.TempSparseBlocks.Add(blockId);
                    }

                    int ow = OccWordIndexLUT[li];
                    occ[ow] |= OccBitMaskLUT[li];

                    if (hasAnyFace)
                    {
                        if (touchNegX) { int w = yzIdx >> 6; faceNegX[w] |= 1UL << (yzIdx & 63); }
                        if (touchPosX) { int w = yzIdx >> 6; facePosX[w] |= 1UL << (yzIdx & 63); }
                        if (touchNegZ) { int w = xyIdx >> 6; faceNegZ[w] |= 1UL << (xyIdx & 63); }
                        if (touchPosZ) { int w = xyIdx >> 6; facePosZ[w] |= 1UL << (xyIdx & 63); }
                    }
                }

                if (touchesBottom)
                {
                    int w = xzIndexConst >> 6;
                    faceNegY[w] |= 1UL << (xzIndexConst & 63);
                }
                if (touchesTop)
                {
                    int w = xzIndexConst >> 6;
                    facePosY[w] |= 1UL << (xzIndexConst & 63);
                }
            }

            // ---- Run-level adjacency accumulation (unchanged logic) ----
            int runLen = added;
            if (runLen > 1) sec.AdjPairsY += runLen - 1;

            // Below voxel adjacency (single check)
            if (yStart > 0)
            {
                int belowLinear = (yStart - 1) * plane + baseXZ;
                if ((occ[OccWordIndexLUT[belowLinear]] & OccBitMaskLUT[belowLinear]) != 0)
                    sec.AdjPairsY++;
            }

            // Negative X adjacency (scan once if column not at x==0)
            if (localX > 0)
            {
                int li = yStart * plane + baseXZ;
                for (int y = yStart; y <= yEnd; y++, li += plane)
                {
                    int nei = li - 1;
                    if ((occ[OccWordIndexLUT[nei]] & OccBitMaskLUT[nei]) != 0) sec.AdjPairsX++;
                }
            }
            // Negative Z adjacency
            if (localZ > 0)
            {
                int li = yStart * plane + baseXZ;
                for (int y = yStart; y <= yEnd; y++, li += plane)
                {
                    int nei = li - ChunkSection.SECTION_SIZE;
                    if ((occ[OccWordIndexLUT[nei]] & OccBitMaskLUT[nei]) != 0) sec.AdjPairsZ++;
                }
            }

            // Bounds
            if (!sec.BoundsInitialized)
            {
                sec.BoundsInitialized = true; sec.HasBounds = true;
                sec.MinLX = sec.MaxLX = (byte)localX;
                sec.MinLZ = sec.MaxLZ = (byte)localZ;
                sec.MinLY = (byte)yStart; sec.MaxLY = (byte)yEnd;
            }
            else
            {
                if (localX < sec.MinLX) sec.MinLX = (byte)localX; else if (localX > sec.MaxLX) sec.MaxLX = (byte)localX;
                if (localZ < sec.MinLZ) sec.MinLZ = (byte)localZ; else if (localZ > sec.MaxLZ) sec.MaxLZ = (byte)localZ;
                if (yStart < sec.MinLY) sec.MinLY = (byte)yStart;
                if (yEnd > sec.MaxLY) sec.MaxLY = (byte)yEnd;
            }
        }
    }
}

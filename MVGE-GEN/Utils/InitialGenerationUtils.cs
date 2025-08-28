using MVGE_GEN.Terrain;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GEN.Utils
{
    public static partial class SectionUtils
    {
        public static bool EnableFastSectionClassification = true;
        private static readonly int[] FacePlaneSizes = { 4, 4, 4 }; // each face bitset length in ulongs (256 bits)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillColumnRangeInitial(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (sec == null || yEnd < yStart || blockId == ChunkSection.AIR) return;

            if (sec.IsAllAir) Initialize(sec);
            sec.Kind = ChunkSection.RepresentationKind.Packed;
            sec.MetadataBuilt = false;

            if (!sec.PaletteLookup.TryGetValue(blockId, out int paletteIndex))
            {
                paletteIndex = sec.Palette.Count;
                sec.Palette.Add(blockId);
                sec.PaletteLookup[blockId] = paletteIndex;
                if (paletteIndex >= (1 << sec.BitsPerIndex))
                {
                    GrowBits(sec);
                    paletteIndex = sec.PaletteLookup[blockId];
                }
            }

            int added = yEnd - yStart + 1;
            sec.NonAirCount += added;
            if (sec.VoxelCount != 0 && sec.NonAirCount == sec.VoxelCount) sec.CompletelyFull = true;

            int plane = SECTION_SIZE * SECTION_SIZE; // 256
            int baseXZ = localZ * SECTION_SIZE + localX;
            int bpi = sec.BitsPerIndex;
            uint mask = (uint)((1 << bpi) - 1);
            uint pval = (uint)paletteIndex & mask;

            if (bpi == 1)
            {
                for (int y = yStart; y <= yEnd; y++)
                {
                    int linear = y * plane + baseXZ;
                    long bitPos = linear;
                    int dataIndex = (int)(bitPos >> 5);
                    int bitOffset = (int)(bitPos & 31);
                    sec.BitData[dataIndex] |= (pval << bitOffset);
                }
            }
            else
            {
                for (int y = yStart; y <= yEnd; y++)
                {
                    int linear = y * plane + baseXZ;
                    long bitPos = (long)linear * bpi;
                    int dataIndex = (int)(bitPos >> 5);
                    int bitOffset = (int)(bitPos & 31);
                    sec.BitData[dataIndex] &= ~(mask << bitOffset);
                    sec.BitData[dataIndex] |= pval << bitOffset;
                    int remaining = 32 - bitOffset;
                    if (remaining < bpi)
                    {
                        int bitsInNext = bpi - remaining;
                        uint nextMask = (uint)((1 << bitsInNext) - 1);
                        sec.BitData[dataIndex + 1] &= ~nextMask;
                        sec.BitData[dataIndex + 1] |= pval >> remaining;
                    }
                }
            }

            if (!EnableFastSectionClassification) return;

            // Uniform candidate tracking
            if (sec.PreclassUniformCandidate)
            {
                if (sec.NonAirCount == added)
                {
                    sec.PreclassFirstBlock = blockId; // first batch
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

            // Sparse provisional capture
            if (sec.NonAirCount <= 128)
            {
                sec.TempSparseIndices ??= new List<int>(128);
                sec.TempSparseBlocks ??= new List<ushort>(128);
                for (int y = yStart; y <= yEnd; y++)
                {
                    int linear = y * plane + baseXZ;
                    sec.TempSparseIndices.Add(linear);
                    sec.TempSparseBlocks.Add(blockId);
                }
            }
            else
            {
                // If threshold crossed, discard temp lists to avoid conversion later
                if (sec.TempSparseIndices != null) { sec.TempSparseIndices = null; sec.TempSparseBlocks = null; }
            }

            // Incremental occupancy + adjacency (only allocate when needed)
            sec.OccupancyBits ??= new ulong[64];
            for (int y = yStart; y <= yEnd; y++)
            {
                int linear = y * plane + baseXZ; // mapping: (y*256)+(z*16)+x
                int word = linear >> 6; int bit = linear & 63;
                ulong prev = sec.OccupancyBits[word];
                sec.OccupancyBits[word] |= 1UL << bit;
                if (prev != sec.OccupancyBits[word])
                {
                    // negative-side neighbor checks (avoid double counting)
                    if (localX > 0)
                    {
                        int li2 = linear - 1;
                        if ((sec.OccupancyBits[li2 >> 6] & (1UL << (li2 & 63))) != 0) sec.AdjPairsX++;
                    }
                    if (y > 0)
                    {
                        int li2 = linear - plane;
                        if ((sec.OccupancyBits[li2 >> 6] & (1UL << (li2 & 63))) != 0) sec.AdjPairsY++;
                    }
                    if (localZ > 0)
                    {
                        int li2 = linear - SECTION_SIZE;
                        if ((sec.OccupancyBits[li2 >> 6] & (1UL << (li2 & 63))) != 0) sec.AdjPairsZ++;
                    }
                }
            }

            // Face masks (allocate only when boundary column or reaching top/bottom)
            if (localX == 0) sec.FaceNegXBits ??= new ulong[4];
            if (localX == SECTION_SIZE - 1) sec.FacePosXBits ??= new ulong[4];
            if (localZ == 0) sec.FaceNegZBits ??= new ulong[4];
            if (localZ == SECTION_SIZE - 1) sec.FacePosZBits ??= new ulong[4];
            // Y faces only if touches min/max layer
            bool touchesBottom = yStart == 0;
            bool touchesTop = yEnd == SECTION_SIZE - 1;
            if (touchesBottom) sec.FaceNegYBits ??= new ulong[4];
            if (touchesTop) sec.FacePosYBits ??= new ulong[4];

            for (int y = yStart; y <= yEnd; y++)
            {
                int yzIndex = localZ * SECTION_SIZE + y; // for X faces
                int xzIndex = localX * SECTION_SIZE + localZ; // for Y faces
                int xyIndex = localX * SECTION_SIZE + y; // for Z faces
                if (localX == 0) sec.FaceNegXBits[yzIndex >> 6] |= 1UL << (yzIndex & 63);
                if (localX == SECTION_SIZE - 1) sec.FacePosXBits[yzIndex >> 6] |= 1UL << (yzIndex & 63);
                if (localZ == 0) sec.FaceNegZBits[xyIndex >> 6] |= 1UL << (xyIndex & 63);
                if (localZ == SECTION_SIZE - 1) sec.FacePosZBits[xyIndex >> 6] |= 1UL << (xyIndex & 63);
                if (y == 0 && touchesBottom) sec.FaceNegYBits[xzIndex >> 6] |= 1UL << (xzIndex & 63);
                if (y == SECTION_SIZE - 1 && touchesTop) sec.FacePosYBits[xzIndex >> 6] |= 1UL << (xzIndex & 63);
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
                if (localX < sec.MinLX) sec.MinLX = (byte)localX; if (localX > sec.MaxLX) sec.MaxLX = (byte)localX;
                if (localZ < sec.MinLZ) sec.MinLZ = (byte)localZ; if (localZ > sec.MaxLZ) sec.MaxLZ = (byte)localZ;
                if (yStart < sec.MinLY) sec.MinLY = (byte)yStart; if (yEnd > sec.MaxLY) sec.MaxLY = (byte)yEnd;
            }
        }
    }
}

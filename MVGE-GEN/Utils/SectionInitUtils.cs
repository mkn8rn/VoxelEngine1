using MVGE_GEN.Models;
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

            // Early exit: just write bits if fast classification disabled
            if (!EnableFastSectionClassification)
            {
                if (bpi == 1)
                {
                    // Single-bit fast path
                    int linear = yStart * plane + baseXZ;
                    for (int y = yStart; y <= yEnd; y++, linear += plane)
                    {
                        int dataIndex = linear >> 5;
                        int bitOffset = linear & 31;
                        sec.BitData[dataIndex] |= (pval << bitOffset);
                    }
                }
                else
                {
                    int linear = yStart * plane + baseXZ;
                    for (int y = yStart; y <= yEnd; y++, linear += plane)
                    {
                        int bitPos = linear * bpi;
                        int dataIndex = bitPos >> 5;
                        int bitOffset = bitPos & 31;
                        uint[] data = sec.BitData;
                        data[dataIndex] &= ~(mask << bitOffset);
                        data[dataIndex] |= pval << bitOffset;
                        int remaining = 32 - bitOffset;
                        if (remaining < bpi)
                        {
                            int bitsInNext = bpi - remaining;
                            uint nextMask = (uint)((1 << bitsInNext) - 1);
                            data[dataIndex + 1] &= ~nextMask;
                            data[dataIndex + 1] |= pval >> remaining;
                        }
                    }
                }
                return;
            }

            // ---- Fast classification enabled: fused pass with run-level adjacency ----

            // Uniform candidate tracking (section-level, not per voxel)
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

            bool captureSparse = sec.NonAirCount <= 128;
            if (captureSparse)
            {
                sec.TempSparseIndices ??= new List<int>(128);
                sec.TempSparseBlocks ??= new List<ushort>(128);
            }
            else if (sec.TempSparseIndices != null)
            {
                // Threshold crossed; discard to skip later conversion cost
                sec.TempSparseIndices = null;
                sec.TempSparseBlocks = null;
            }

            // Occupancy allocation (once)
            sec.OccupancyBits ??= new ulong[64];
            ulong[] occ = sec.OccupancyBits;
            uint[] bitData = sec.BitData;

            // Face bitset allocation (only allocate planes we will touch)
            bool touchNegX = localX == 0;
            bool touchPosX = localX == SECTION_SIZE - 1;
            bool touchNegZ = localZ == 0;
            bool touchPosZ = localZ == SECTION_SIZE - 1;
            bool touchesBottom = yStart == 0;
            bool touchesTop = yEnd == SECTION_SIZE - 1;

            if (touchNegX) sec.FaceNegXBits ??= new ulong[4];
            if (touchPosX) sec.FacePosXBits ??= new ulong[4];
            if (touchNegZ) sec.FaceNegZBits ??= new ulong[4];
            if (touchPosZ) sec.FacePosZBits ??= new ulong[4];
            if (touchesBottom) sec.FaceNegYBits ??= new ulong[4];
            if (touchesTop) sec.FacePosYBits ??= new ulong[4];

            ulong[] faceNegX = sec.FaceNegXBits;
            ulong[] facePosX = sec.FacePosXBits;
            ulong[] faceNegZ = sec.FaceNegZBits;
            ulong[] facePosZ = sec.FacePosZBits;
            ulong[] faceNegY = sec.FaceNegYBits;
            ulong[] facePosY = sec.FacePosYBits;

            // Precompute incremental indices
            int linearIter = yStart * plane + baseXZ;
            int yzIndexStart = localZ * SECTION_SIZE + yStart; // for X faces
            int xyIndexStart = localX * SECTION_SIZE + yStart; // for Z faces
            int xzIndexConst = localX * SECTION_SIZE + localZ; // for Y faces
            int runLen = added;

            // Bit / occupancy writes (no per-voxel adjacency counting now)
            if (bpi == 1)
            {
                int li = linearIter;
                int yzIndex = yzIndexStart;
                int xyIndex = xyIndexStart;
                for (int y = yStart; y <= yEnd; y++, li += plane, yzIndex++, xyIndex++)
                {
                    int dataIndex = li >> 5;
                    int bitOffset = li & 31;
                    bitData[dataIndex] |= (pval << bitOffset);

                    if (captureSparse)
                    {
                        sec.TempSparseIndices.Add(li);
                        sec.TempSparseBlocks.Add(blockId);
                    }

                    int word = li >> 6;
                    int bit = li & 63;
                    occ[word] |= 1UL << bit; // voxel newly occupied (initial generation guarantees no prior occupancy)

                    // Face masks (still per-y for simplicity; could batch later)
                    int yzWord = yzIndex >> 6; int yzBit = yzIndex & 63;
                    int xyWord = xyIndex >> 6; int xyBit = xyIndex & 63;
                    int xzWord = xzIndexConst >> 6; int xzBit = xzIndexConst & 63;
                    if (touchNegX) faceNegX[yzWord] |= 1UL << yzBit;
                    if (touchPosX) facePosX[yzWord] |= 1UL << yzBit;
                    if (touchNegZ) faceNegZ[xyWord] |= 1UL << xyBit;
                    if (touchPosZ) facePosZ[xyWord] |= 1UL << xyBit;
                    if (touchesBottom && y == 0) faceNegY[xzWord] |= 1UL << xzBit;
                    if (touchesTop && y == SECTION_SIZE - 1) facePosY[xzWord] |= 1UL << xzBit;
                }
            }
            else
            {
                int li = linearIter;
                int yzIndex = yzIndexStart;
                int xyIndex = xyIndexStart;
                for (int y = yStart; y <= yEnd; y++, li += plane, yzIndex++, xyIndex++)
                {
                    int bitPos = li * bpi;
                    int dataIndex = bitPos >> 5;
                    int bitOffset = bitPos & 31;
                    bitData[dataIndex] &= ~(mask << bitOffset);
                    bitData[dataIndex] |= pval << bitOffset;
                    int remaining = 32 - bitOffset;
                    if (remaining < bpi)
                    {
                        int bitsInNext = bpi - remaining;
                        uint nextMask = (uint)((1 << bitsInNext) - 1);
                        bitData[dataIndex + 1] &= ~nextMask;
                        bitData[dataIndex + 1] |= pval >> remaining;
                    }

                    if (captureSparse)
                    {
                        sec.TempSparseIndices.Add(li);
                        sec.TempSparseBlocks.Add(blockId);
                    }

                    int word = li >> 6;
                    int bit = li & 63;
                    occ[word] |= 1UL << bit;

                    int yzWord = yzIndex >> 6; int yzBit = yzIndex & 63;
                    int xyWord = xyIndex >> 6; int xyBit = xyIndex & 63;
                    int xzWord = xzIndexConst >> 6; int xzBit = xzIndexConst & 63;
                    if (touchNegX) faceNegX[yzWord] |= 1UL << yzBit;
                    if (touchPosX) facePosX[yzWord] |= 1UL << yzBit;
                    if (touchNegZ) faceNegZ[xyWord] |= 1UL << xyBit;
                    if (touchPosZ) facePosZ[xyWord] |= 1UL << xyBit;
                    if (touchesBottom && y == 0) faceNegY[xzWord] |= 1UL << xzBit;
                    if (touchesTop && y == SECTION_SIZE - 1) facePosY[xzWord] |= 1UL << xzBit;
                }
            }

            // ---- Run-level adjacency accumulation ----
            // Y internal adjacency between consecutive voxels in the run
            if (runLen > 1) sec.AdjPairsY += runLen - 1;
            // Bottom cross-run Y adjacency (negative side) if voxel below exists
            if (yStart > 0)
            {
                int belowLinear = (yStart - 1) * plane + baseXZ;
                if ((occ[belowLinear >> 6] & (1UL << (belowLinear & 63))) != 0) sec.AdjPairsY++;
            }
            // X negative side adjacency (scan once)
            if (localX > 0)
            {
                int li = yStart * plane + baseXZ;
                for (int y = yStart; y <= yEnd; y++, li += plane)
                {
                    int nei = li - 1;
                    if ((occ[nei >> 6] & (1UL << (nei & 63))) != 0) sec.AdjPairsX++;
                }
            }
            // Z negative side adjacency (scan once)
            if (localZ > 0)
            {
                int li = yStart * plane + baseXZ;
                for (int y = yStart; y <= yEnd; y++, li += plane)
                {
                    int nei = li - SECTION_SIZE; // subtract one Z step
                    if ((occ[nei >> 6] & (1UL << (nei & 63))) != 0) sec.AdjPairsZ++;
                }
            }

            // Bounds (section-level)
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
                if (yStart < sec.MinLY) sec.MinLY = (byte)yStart; if (yEnd > sec.MaxLY) sec.MaxLY = (byte)yEnd;
            }
        }
    }
}

using MVGE_GEN.Terrain;
using MVGE_INF.Models.Terrain;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MVGE_GEN.Utils
{
    public static class SectionUtils
    {
        private static uint[] RentBitData(int uintCount) => ArrayPool<uint>.Shared.Rent(uintCount);
        private static void ReturnBitData(uint[] data) { if (data != null) ArrayPool<uint>.Shared.Return(data, clearArray: false); }

        public const int SPARSE_THRESHOLD = 512; // currently unused in classification (optimization gate only)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort GetBlock(ChunkSection sec, int x, int y, int z)
        {
            if (sec == null || sec.IsAllAir || sec.Kind == ChunkSection.RepresentationKind.Empty) return ChunkSection.AIR;
            int linear = LinearIndex(x, y, z);
            switch (sec.Kind)
            {
                case ChunkSection.RepresentationKind.Uniform:
                    return sec.UniformBlockId;
                case ChunkSection.RepresentationKind.Sparse:
                case ChunkSection.RepresentationKind.DenseExpanded:
                case ChunkSection.RepresentationKind.Packed:
                default:
                    int paletteIndex = ReadBits(sec, linear); return sec.Palette[paletteIndex];
            }
        }

        public static void SetBlock(ChunkSection sec, int x, int y, int z, ushort blockId)
        {
            if (sec == null) return;
            // Only Packed / Uniform / Empty expected now; if Uniform and a different block is written, re-pack.
            if (sec.Kind == ChunkSection.RepresentationKind.Uniform && blockId != sec.UniformBlockId)
            {
                EnsurePacked(sec); // convert uniform to packed to allow mutation
            }
            if (sec.Kind != ChunkSection.RepresentationKind.Packed && sec.Kind != ChunkSection.RepresentationKind.Empty)
            {
                EnsurePacked(sec);
            }

            int linear = LinearIndex(x, y, z);

            if (blockId == ChunkSection.AIR)
            {
                if (sec.IsAllAir) return;
                int oldIdx = ReadBits(sec, linear);
                if (oldIdx == 0) return; // already air
                WriteBits(sec, linear, 0);
                sec.NonAirCount--;
                sec.CompletelyFull = false; // no longer full
                if (sec.NonAirCount == 0)
                {
                    Collapse(sec);
                    sec.Kind = ChunkSection.RepresentationKind.Empty;
                }
                return;
            }

            if (sec.IsAllAir)
            {
                Initialize(sec);
                sec.Kind = ChunkSection.RepresentationKind.Packed;
            }

            int existingPaletteIndex = ReadBits(sec, linear);
            ushort existingBlock = sec.Palette[existingPaletteIndex];
            if (existingBlock == blockId) return;

            int newPaletteIndex = GetOrAddPaletteIndex(sec, blockId);
            if (newPaletteIndex >= (1 << sec.BitsPerIndex))
            {
                GrowBits(sec);
                newPaletteIndex = GetOrAddPaletteIndex(sec, blockId);
            }

            if (existingBlock == ChunkSection.AIR) sec.NonAirCount++;
            WriteBits(sec, linear, newPaletteIndex);
            if (sec.NonAirCount == sec.VoxelCount && sec.VoxelCount != 0)
            {
                sec.CompletelyFull = true;
            }
        }

        // FAST BULK FILL (generation only!!!)
        // Assumes every targeted voxel is currently AIR (never previously written) and performs no reads.
        // Handles single column inside section for y in [yStart,yEnd].
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillColumnRangeInitial(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (sec == null) return; // caller guarantees allocation
            if (yEnd < yStart) return;

            if (blockId == ChunkSection.AIR) return; // nothing to do

            if (sec.IsAllAir) Initialize(sec); // lazily allocate structures
            sec.Kind = ChunkSection.RepresentationKind.Packed; // still packed until classification pass

            // Acquire / add palette index once
            if (!sec.PaletteLookup.TryGetValue(blockId, out int paletteIndex))
            {
                paletteIndex = sec.Palette.Count;
                sec.Palette.Add(blockId);
                sec.PaletteLookup[blockId] = paletteIndex;
                if (paletteIndex >= (1 << sec.BitsPerIndex))
                {
                    GrowBits(sec);
                    // Re-evaluate palette index after grow (dictionary still valid)
                    paletteIndex = sec.PaletteLookup[blockId];
                }
            }

            int count = yEnd - yStart + 1;
            sec.NonAirCount += count; // all were air by assumption
            if (sec.VoxelCount != 0 && sec.NonAirCount == sec.VoxelCount)
            {
                sec.CompletelyFull = true;
            }

            int plane = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE; // 256
            int baseXZ = localZ * ChunkSection.SECTION_SIZE + localX; // (z*16)+x
            int bpi = sec.BitsPerIndex;
            uint mask = (uint)((1 << bpi) - 1);
            uint pval = (uint)paletteIndex & mask;

            // Specialized tight loop; unrolled for common bpi==1
            if (bpi == 1)
            {
                for (int y = yStart; y <= yEnd; y++)
                {
                    int linear = y * plane + baseXZ; // y*256 + baseXZ
                    long bitPos = linear; // *1
                    int dataIndex = (int)(bitPos >> 5);
                    int bitOffset = (int)(bitPos & 31);
                    sec.BitData[dataIndex] |= (pval << bitOffset); // pval is 1
                }
                return;
            }

            for (int y = yStart; y <= yEnd; y++)
            {
                int linear = y * plane + baseXZ;
                long bitPos = (long)linear * bpi;
                int dataIndex = (int)(bitPos >> 5);
                int bitOffset = (int)(bitPos & 31);

                // Clear & set bits in primary uint
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

        public static void ClassifyRepresentation(ChunkSection sec)
        {
            if (sec == null) return;
            if (sec.NonAirCount == 0 || sec.IsAllAir)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty;
                return;
            }
            int total = sec.VoxelCount;
            if (total == 0) total = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE;
            if (sec.CompletelyFull && sec.Palette != null && sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR)
            {
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = sec.Palette[1];
                return;
            }
            if (sec.Palette != null && sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR && sec.NonAirCount == total)
            {
                sec.CompletelyFull = true;
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = sec.Palette[1];
                return;
            }
            // No sparse/dense expansion: remain packed
            sec.Kind = ChunkSection.RepresentationKind.Packed;
        }

        private static void EnsurePacked(ChunkSection sec)
        {
            if (sec.Kind == ChunkSection.RepresentationKind.Packed) return;
            if (sec.Kind == ChunkSection.RepresentationKind.Empty)
            {
                sec.IsAllAir = true; sec.Palette = null; sec.PaletteLookup = null; sec.BitData = null; sec.BitsPerIndex = 0; sec.NonAirCount = 0; sec.CompletelyFull = false; return;
            }
            if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
            {
                // Recreate packed data for a uniform block
                ushort blockId = sec.UniformBlockId;
                sec.IsAllAir = false;
                sec.VoxelCount = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE;
                sec.Palette = new List<ushort> { ChunkSection.AIR, blockId };
                sec.PaletteLookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 }, { blockId, 1 } };
                sec.BitsPerIndex = 1;
                long totalBits = (long)sec.VoxelCount * sec.BitsPerIndex;
                int uintCount = (int)((totalBits + 31) / 32);
                sec.BitData = RentBitData(uintCount);
                for (int i = 0; i < uintCount; i++) sec.BitData[i] = 0xFFFFFFFFu;
                sec.NonAirCount = sec.VoxelCount;
                sec.CompletelyFull = true;
                sec.Kind = ChunkSection.RepresentationKind.Packed;
                return;
            }
            // Other kinds (Sparse/DenseExpanded) currently not produced; fallback: mark empty.
            sec.Kind = ChunkSection.RepresentationKind.Packed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LinearIndex(int x, int y, int z)
            => (y * ChunkSection.SECTION_SIZE + z) * ChunkSection.SECTION_SIZE + x;

        private static int ReadBits(ChunkSection sec, int voxelIndex)
            => ReadBits(sec.BitData, sec.BitsPerIndex, voxelIndex);

        private static void GrowBits(ChunkSection sec)
        {
            int paletteCountMinusOne = sec.Palette.Count - 1;
            int needed = paletteCountMinusOne <= 0 ? 1 : (int)BitOperations.Log2((uint)paletteCountMinusOne) + 1;
            if (needed <= sec.BitsPerIndex) return;

            int oldBits = sec.BitsPerIndex;
            var oldData = sec.BitData;
            sec.BitsPerIndex = needed;
            AllocateBitData(sec);

            for (int i = 0; i < sec.VoxelCount; i++)
            {
                int pi = ReadBits(oldData, oldBits, i);
                WriteBits(sec, i, pi);
            }
        }

        private static void AllocateBitData(ChunkSection sec)
        {
            if (sec.BitsPerIndex <= 0)
            {
                sec.BitData = null;
                return;
            }
            long totalBits = (long)sec.VoxelCount * sec.BitsPerIndex;
            int uintCount = (int)((totalBits + 31) / 32);
            sec.BitData = RentBitData(uintCount);
            // Ensure clean state (fill with zeros) because pool may give dirty memory
            Array.Clear(sec.BitData, 0, uintCount);
        }

        private static int ReadBits(uint[] data, int bpi, int voxelIndex)
        {
            if (bpi == 0) return 0;
            long bitPos = (long)voxelIndex * bpi;
            int dataIndex = (int)(bitPos >> 5);
            int bitOffset = (int)(bitPos & 31);
            uint value = data[dataIndex] >> bitOffset;
            int mask = (1 << bpi) - 1;
            int remaining = 32 - bitOffset;
            if (remaining < bpi)
            {
                value |= data[dataIndex + 1] << remaining;
            }
            return (int)(value & (uint)mask);
        }

        private static void WriteBits(ChunkSection sec, int voxelIndex, int paletteIndex)
        {
            long bitPos = (long)voxelIndex * sec.BitsPerIndex;
            int dataIndex = (int)(bitPos >> 5);
            int bitOffset = (int)(bitPos & 31);
            uint mask = (uint)((1 << sec.BitsPerIndex) - 1);

            sec.BitData[dataIndex] &= ~(mask << bitOffset);
            sec.BitData[dataIndex] |= (uint)paletteIndex << bitOffset;

            int remaining = 32 - bitOffset;
            if (remaining < sec.BitsPerIndex)
            {
                int bitsInNext = sec.BitsPerIndex - remaining;
                uint nextMask = (uint)((1 << bitsInNext) - 1);
                sec.BitData[dataIndex + 1] &= ~nextMask;
                sec.BitData[dataIndex + 1] |= (uint)paletteIndex >> remaining;
            }
        }

        private static void Collapse(ChunkSection sec)
        {
            sec.IsAllAir = true;
            sec.Palette = null;
            sec.PaletteLookup = null;
            ReturnBitData(sec.BitData);
            sec.BitData = null;
            sec.BitsPerIndex = 0;
            sec.NonAirCount = 0;
            sec.CompletelyFull = false;
        }

        private static int GetOrAddPaletteIndex(ChunkSection sec, ushort blockId)
        {
            if (sec.PaletteLookup.TryGetValue(blockId, out int idx))
                return idx;
            idx = sec.Palette.Count;
            sec.Palette.Add(blockId);
            sec.PaletteLookup[blockId] = idx;
            return idx;
        }

        private static void Initialize(ChunkSection sec)
        {
            sec.IsAllAir = false;
            sec.VoxelCount = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE;
            sec.Palette = new List<ushort>(8) { ChunkSection.AIR };
            sec.PaletteLookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 } };
            sec.BitsPerIndex = 1;
            AllocateBitData(sec);
            sec.NonAirCount = 0;
            sec.CompletelyFull = false;
        }
    }
}

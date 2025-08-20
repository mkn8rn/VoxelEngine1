using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MVGE_GEN.Utils
{
    public static class SectionUtils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort GetBlock(ChunkSection sec, int x, int y, int z)
        {
            if (sec == null || sec.IsAllAir) return ChunkSection.AIR;
            int linear = LinearIndex(x, y, z);
            if (sec.Decoded != null && !sec.DecodedDirty)
            {
                return sec.Decoded[linear];
            }
            int paletteIndex = ReadBits(sec, linear);
            return sec.Palette[paletteIndex];
        }

        public static void SetBlock(ChunkSection sec, int x, int y, int z, ushort blockId)
        {
            if (sec == null) return;

            int linear = LinearIndex(x, y, z);

            if (blockId == ChunkSection.AIR)
            {
                if (sec.IsAllAir) return;
                int oldIdx = ReadBits(sec, linear);
                if (oldIdx == 0) return; // already air
                WriteBits(sec, linear, 0);
                if (sec.Decoded != null && !sec.DecodedDirty) sec.Decoded[linear] = ChunkSection.AIR; // patch decoded
                sec.NonAirCount--;
                if (sec.NonAirCount == 0)
                {
                    Collapse(sec);
                }
                return;
            }

            if (sec.IsAllAir)
            {
                Initialize(sec);
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
            if (sec.Decoded != null && !sec.DecodedDirty) sec.Decoded[linear] = blockId; // patch decoded
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
            // Existing decoded remains valid; no dirty flag needed since palette indices remapped identically.
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
            sec.BitData = new uint[uintCount];
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
            sec.BitData = null;
            sec.BitsPerIndex = 0;
            sec.NonAirCount = 0;
            sec.Decoded = null; // drop decoded cache
            sec.DecodedDirty = true;
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
            sec.DecodedDirty = true; // cache invalid
        }

        // Decode entire section into Decoded[] if needed.
        public static void EnsureDecoded(ChunkSection sec)
        {
            if (sec == null || sec.IsAllAir)
            {
                if (sec != null)
                {
                    sec.Decoded ??= new ushort[ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE];
                    if (sec.DecodedDirty)
                    {
                        Array.Fill(sec.Decoded, ChunkSection.AIR);
                        sec.DecodedDirty = false;
                    }
                }
                return;
            }
            if (sec.Decoded != null && !sec.DecodedDirty) return;
            sec.Decoded ??= new ushort[ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE];
            if (sec.Palette == null || sec.BitsPerIndex == 0)
            {
                Array.Fill(sec.Decoded, ChunkSection.AIR);
                sec.DecodedDirty = false;
                return;
            }
            // Fast path: only air + one solid (palette count 2 but NonAirCount may vary)
            if (sec.Palette.Count == 2 && sec.NonAirCount == sec.VoxelCount)
            {
                ushort solid = sec.Palette[1];
                Array.Fill(sec.Decoded, solid);
                sec.DecodedDirty = false;
                return;
            }
            // General decode
            int total = sec.VoxelCount;
            for (int i = 0; i < total; i++)
            {
                int pi = ReadBits(sec, i);
                sec.Decoded[i] = sec.Palette[pi];
            }
            sec.DecodedDirty = false;
        }
    }
}

using MVGE_GEN.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Utils
{
    public static partial class SectionUtils
    {
        // FAST BULK FILL (generation only!!!)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillColumnRangeInitial(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (sec == null) return; // caller guarantees allocation
            if (yEnd < yStart) return;

            if (blockId == ChunkSection.AIR) return; // nothing to do

            if (sec.IsAllAir) Initialize(sec); // lazily allocate structures
            sec.Kind = ChunkSection.RepresentationKind.Packed; // still packed until classification pass
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

            int count = yEnd - yStart + 1;
            sec.NonAirCount += count; // all were air by assumption
            if (sec.VoxelCount != 0 && sec.NonAirCount == sec.VoxelCount)
            {
                sec.CompletelyFull = true;
            }

            int plane = SECTION_SIZE * SECTION_SIZE; // 256
            int baseXZ = localZ * SECTION_SIZE + localX; // (z*16)+x
            int bpi = sec.BitsPerIndex;
            uint mask = (uint)((1 << bpi) - 1);
            uint pval = (uint)paletteIndex & mask;

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
    }
}

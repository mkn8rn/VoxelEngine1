using System.Collections.Generic;

namespace MVGE_INF.Models.Terrain
{
    public sealed class ChunkSection
    {
        public const int SECTION_SIZE = 20;
        public const ushort AIR = 0;

        public bool IsAllAir = true;

        public List<ushort> Palette;                // index -> blockId
        public Dictionary<ushort, int> PaletteLookup; // blockId -> palette index

        public uint[] BitData;
        public int BitsPerIndex;
        public int VoxelCount;
        public int NonAirCount;
    }
}
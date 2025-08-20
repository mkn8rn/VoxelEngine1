using System.Collections.Generic;

namespace MVGE_INF.Models.Terrain
{
    public sealed class ChunkSection
    {
        public const int SECTION_SIZE = 16;
        public const ushort AIR = 0;

        public bool IsAllAir = true;

        public List<ushort> Palette;                // index -> blockId
        public Dictionary<ushort, int> PaletteLookup; // blockId -> palette index

        public uint[] BitData;
        public int BitsPerIndex;
        public int VoxelCount;
        public int NonAirCount;

        // Lazy decoded full block array (4096 entries). Null until first needed.
        public ushort[] Decoded;
        public bool DecodedDirty = true; // when true, needs rebuild before use
    }
}
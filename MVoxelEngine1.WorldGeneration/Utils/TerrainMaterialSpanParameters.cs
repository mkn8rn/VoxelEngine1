using System;
using System.Collections.Generic;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration.Terrain
{
    internal readonly struct TerrainMaterialSpanParameters
    {
        internal TerrainMaterialSpanParameters(int stoneMinY, int stoneMaxY, int stoneMinDepth, int stoneMaxDepth, int soilMinY, int soilMaxY, int soilMinDepth, int soilMaxDepth, int waterLevel)
        {
            StoneMinY = stoneMinY;
            StoneMaxY = stoneMaxY;
            StoneMinDepth = stoneMinDepth;
            StoneMaxDepth = stoneMaxDepth;
            SoilMinY = soilMinY;
            SoilMaxY = soilMaxY;
            SoilMinDepth = soilMinDepth;
            SoilMaxDepth = soilMaxDepth;
            WaterLevel = waterLevel;
        }

        internal int StoneMinY { get; }
        internal int StoneMaxY { get; }
        internal int StoneMinDepth { get; }
        internal int StoneMaxDepth { get; }
        internal int SoilMinY { get; }
        internal int SoilMaxY { get; }
        internal int SoilMinDepth { get; }
        internal int SoilMaxDepth { get; }
        internal int WaterLevel { get; }

        internal static TerrainMaterialSpanParameters FromBiome(Biome biome) => new(biome.stoneMinYLevel, biome.stoneMaxYLevel, biome.stoneMinDepth, biome.stoneMaxDepth, biome.soilMinYLevel, biome.soilMaxYLevel, biome.soilMinDepth, biome.soilMaxDepth, biome.waterLevel);
    }
}

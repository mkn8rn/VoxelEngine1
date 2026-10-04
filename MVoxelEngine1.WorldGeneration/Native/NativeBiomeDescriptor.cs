using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeBiomeDescriptor
{
    internal NativeBiomeDescriptor(Biome source, int replacementRuleOffset, int replacementRuleCount)
    {
        Id = source.id;
        StoneMinY = source.stoneMinYLevel;
        StoneMaxY = source.stoneMaxYLevel;
        StoneMinDepth = source.stoneMinDepth;
        StoneMaxDepth = source.stoneMaxDepth;
        SoilMinY = source.soilMinYLevel;
        SoilMaxY = source.soilMaxYLevel;
        SoilMinDepth = source.soilMinDepth;
        SoilMaxDepth = source.soilMaxDepth;
        WaterLevel = source.waterLevel;
        ReplacementRuleOffset = replacementRuleOffset;
        ReplacementRuleCount = replacementRuleCount;
    }

    internal int Id { get; }
    internal int StoneMinY { get; }
    internal int StoneMaxY { get; }
    internal int StoneMinDepth { get; }
    internal int StoneMaxDepth { get; }
    internal int SoilMinY { get; }
    internal int SoilMaxY { get; }
    internal int SoilMinDepth { get; }
    internal int SoilMaxDepth { get; }
    internal int WaterLevel { get; }
    internal int ReplacementRuleOffset { get; }
    internal int ReplacementRuleCount { get; }
}

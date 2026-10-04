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
internal readonly struct NativeGameSnapshotHeader
{
    internal const uint ExpectedMagic = 0x4D41474E;
    internal const int ExpectedVersion = 2;
    internal NativeGameSnapshotHeader(int totalByteCount, int blockOffset, int blockCount, int biomeOffset, int biomeCount, int replacementRuleOffset, int replacementRuleCount, int specificIdOffset, int specificIdCount)
    {
        Magic = ExpectedMagic;
        Version = ExpectedVersion;
        TotalByteCount = totalByteCount;
        BlockOffset = blockOffset;
        BlockCount = blockCount;
        BiomeOffset = biomeOffset;
        BiomeCount = biomeCount;
        ReplacementRuleOffset = replacementRuleOffset;
        ReplacementRuleCount = replacementRuleCount;
        SpecificIdOffset = specificIdOffset;
        SpecificIdCount = specificIdCount;
    }

    internal uint Magic { get; }
    internal int Version { get; }
    internal int TotalByteCount { get; }
    internal int BlockOffset { get; }
    internal int BlockCount { get; }
    internal int BiomeOffset { get; }
    internal int BiomeCount { get; }
    internal int ReplacementRuleOffset { get; }
    internal int ReplacementRuleCount { get; }
    internal int SpecificIdOffset { get; }
    internal int SpecificIdCount { get; }
}

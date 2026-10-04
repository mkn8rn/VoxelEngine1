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
internal readonly struct NativeReplacementRule
{
    internal NativeReplacementRule(CompiledSimpleReplacementRule source, int specificIdOffset)
    {
        ReplacementId = source.ReplacementId;
        SpecificIdCount = checked((ushort)source.SpecificIdsSorted.Length);
        SpecificIdOffset = specificIdOffset;
        BaseTypeBitMask = source.BaseTypeBitMask;
        MinY = source.MinY;
        MaxY = source.MaxY;
        MicroBiomeId = source.MicroBiomeId ?? -1;
        Priority = source.Priority;
        GenerationType = source.GenerationType;
        RelativeMinDepth = source.RelativeMinDepth;
        RelativeMaxDepth = source.RelativeMaxDepth;
    }

    internal ushort ReplacementId { get; }
    internal ushort SpecificIdCount { get; }
    internal int SpecificIdOffset { get; }
    internal uint BaseTypeBitMask { get; }
    internal int MinY { get; }
    internal int MaxY { get; }
    internal int MicroBiomeId { get; }
    internal int Priority { get; }
    internal GenerationType GenerationType { get; }
    internal int RelativeMinDepth { get; }
    internal int RelativeMaxDepth { get; }
}

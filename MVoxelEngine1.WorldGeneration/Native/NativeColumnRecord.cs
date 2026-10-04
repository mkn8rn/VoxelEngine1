using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeColumnRecord
{
    internal int ChunkX;
    internal int ChunkZ;
    internal int ProfileOffset;
    internal int BiomeIndex;
    internal int GenerationEpoch;
    internal NativeColumnState State;
    internal int ReplacementMode;
    internal int SummaryComputed;
    internal NativeTerrainMaterialSet ResolvedMaterials;
}

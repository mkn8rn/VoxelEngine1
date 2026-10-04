using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;

internal readonly ref struct NativeProfileMeshContext
{
    internal NativeProfileMeshContext(ReadOnlySpan<BlockColumnProfile> profiles,
        NativeTerrainMaterialSet materials, int width, int height, int depth, int chunkStart)
    {
        Profiles = profiles;
        Materials = materials;
        Width = width;
        Depth = depth;
        ChunkStart = chunkStart;
        ChunkEnd = chunkStart + height - 1;
    }

    internal readonly ReadOnlySpan<BlockColumnProfile> Profiles;
    internal readonly NativeTerrainMaterialSet Materials;
    internal readonly int Width;
    internal readonly int Depth;
    internal readonly int ChunkStart;
    internal readonly int ChunkEnd;
}

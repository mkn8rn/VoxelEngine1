using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeTransparentProfileMesh
{
    internal static bool Supports(scoped in NativeTerrainMaterialSet materials) =>
        materials.Stone.Id != 0 && materials.Soil.Id != 0 && materials.Water.Id != 0 &&
        materials.Stone.Id != materials.Soil.Id && materials.Stone.Id != materials.Water.Id &&
        materials.Soil.Id != materials.Water.Id &&
        !materials.Stone.HasFlag(NativeBlockFlags.Opaque) &&
        !materials.Soil.HasFlag(NativeBlockFlags.Opaque) &&
        !materials.Water.HasFlag(NativeBlockFlags.Opaque);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void EmitInteriorSides(ReadOnlySpan<BlockColumnProfile> profiles,
        int width, int height, int depth, int chunkStart, scoped ref NativeGeneratedFaceWriter writer)
    {
        int chunkEnd = chunkStart + height - 1;
        for (int x = 0; x < width; x++)
        for (int z = 0; z < depth; z++)
        {
            ref readonly BlockColumnProfile source = ref profiles[x * depth + z];
            if (x + 1 < width)
                EmitPair(in source, in profiles[(x + 1) * depth + z], chunkStart, chunkEnd,
                    1, x, z, 0, x + 1, z, ref writer);
            if (z + 1 < depth)
                EmitPair(in source, in profiles[x * depth + z + 1], chunkStart, chunkEnd,
                    5, x, z, 4, x, z + 1, ref writer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitPair(scoped ref readonly BlockColumnProfile first, scoped ref readonly BlockColumnProfile second,
        int chunkStart, int chunkEnd, byte firstDirection, int firstX, int firstZ,
        byte secondDirection, int secondX, int secondZ, scoped ref NativeGeneratedFaceWriter writer)
    {
        EmitDifferences(first.StoneStart, first.StoneEnd, second.StoneStart, second.StoneEnd,
            0, chunkStart, chunkEnd, firstDirection, firstX, firstZ, secondDirection, secondX, secondZ, ref writer);
        EmitDifferences(first.SoilStart, first.SoilEnd, second.SoilStart, second.SoilEnd,
            1, chunkStart, chunkEnd, firstDirection, firstX, firstZ, secondDirection, secondX, secondZ, ref writer);
        EmitDifferences(first.WaterStart, first.WaterEnd, second.WaterStart, second.WaterEnd,
            2, chunkStart, chunkEnd, firstDirection, firstX, firstZ, secondDirection, secondX, secondZ, ref writer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitDifferences(int firstStart, int firstEnd, int secondStart, int secondEnd,
        int material, int chunkStart, int chunkEnd, byte firstDirection, int firstX, int firstZ,
        byte secondDirection, int secondX, int secondZ, scoped ref NativeGeneratedFaceWriter writer)
    {
        EmitDifference(firstStart, firstEnd, secondStart, secondEnd,
            material, chunkStart, chunkEnd, firstDirection, firstX, firstZ, ref writer);
        EmitDifference(secondStart, secondEnd, firstStart, firstEnd,
            material, chunkStart, chunkEnd, secondDirection, secondX, secondZ, ref writer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitDifference(int sourceStart, int sourceEnd, int neighborStart, int neighborEnd,
        int material, int chunkStart, int chunkEnd, byte direction, int x, int z,
        scoped ref NativeGeneratedFaceWriter writer)
    {
        if (sourceStart < 0 || sourceEnd < sourceStart)
            return;
        int start = Math.Max(sourceStart, chunkStart);
        int end = Math.Min(sourceEnd, chunkEnd);
        if (start > end)
            return;
        // With distinct transparent IDs, only the same material hides a face.
        if (neighborStart < 0 || neighborEnd < neighborStart || neighborEnd < start || neighborStart > end)
        {
            writer.EmitMaterialYRange(material, false, direction, x, start - chunkStart, end - chunkStart, z);
            return;
        }
        if (start < neighborStart)
            writer.EmitMaterialYRange(material, false, direction, x, start - chunkStart,
                neighborStart - chunkStart - 1, z);
        if (end > neighborEnd)
            writer.EmitMaterialYRange(material, false, direction, x, neighborEnd - chunkStart + 1,
                end - chunkStart, z);
    }
}

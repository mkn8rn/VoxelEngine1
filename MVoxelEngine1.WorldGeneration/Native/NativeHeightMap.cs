using Supprocom.OpenSimplexNoise;

namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeHeightMap
{
        internal static void FillHeightMap(
            ReadOnlySpan<byte> permutation,
            ReadOnlySpan<byte> permutation2D,
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            Span<float> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);
            int valueCount = checked(sizeX * sizeZ);
            if (destination.Length < valueCount)
            {
                throw new ArgumentException(
                    "The destination is smaller than the height map.",
                    nameof(destination));
            }

            const float scale = 0.001f;
            const float minHeight = 1f;
            const float maxHeight = 1000f;

            for (int x = 0; x < sizeX; x++)
            {
                int rowOffset = x * sizeZ;
                int worldX = unchecked(x + baseX);
                for (int z = 0; z < sizeZ; z++)
                {
                    float noiseValue = (float)OpenSimplexNoise.Evaluate(
                        permutation,
                        permutation2D,
                        worldX * scale,
                        unchecked(z + baseZ) * scale);
                    float normalizedValue = noiseValue * 0.5f + 0.5f;
                    destination[rowOffset + z] = normalizedValue *
                        (maxHeight - minHeight) + minHeight;
                }
            }
        }

}

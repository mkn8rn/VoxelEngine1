using System.Collections.Concurrent;
using Supprocom.OpenSimplexNoise;

namespace MVoxelEngine1.WorldGeneration.Terrain;

// Independent object-based height oracle and legacy save partition authority.
internal static class Quadrant
{
    public const int QUAD_SIZE = 16;
    private static readonly ConcurrentDictionary<long, OpenSimplexNoise> NoiseCache = new();
    private static OpenSimplexNoise GetNoise(long seed) =>
        NoiseCache.GetOrAdd(seed, static value => new OpenSimplexNoise(value));
    public static (int bx, int bz) GetBatchIndices(int cx, int cz) =>
        ((int)Math.Floor((double)cx / QUAD_SIZE), (int)Math.Floor((double)cz / QUAD_SIZE));
        internal static void FillHeightMap(
            long seed,
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            Span<float> destination) =>
            FillHeightMap(
                GetNoise(seed),
                baseX,
                baseZ,
                sizeX,
                sizeZ,
                destination);

        internal static void FillHeightMap(
            OpenSimplexNoise noise,
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            Span<float> destination)
        {
            ArgumentNullException.ThrowIfNull(noise);
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
                    float noiseValue = (float)noise.Evaluate(
                        worldX * scale,
                        unchecked(z + baseZ) * scale);
                    float normalizedValue = noiseValue * 0.5f + 0.5f;
                    destination[rowOffset + z] = normalizedValue *
                        (maxHeight - minHeight) + minHeight;
                }
            }
        }

}

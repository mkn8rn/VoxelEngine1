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
        internal TerrainMaterialSpanParameters(
            int stoneMinY,
            int stoneMaxY,
            int stoneMinDepth,
            int stoneMaxDepth,
            int soilMinY,
            int soilMaxY,
            int soilMinDepth,
            int soilMaxDepth,
            int waterLevel)
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

        internal static TerrainMaterialSpanParameters FromBiome(
            Biome biome) =>
            new(
                biome.stoneMinYLevel,
                biome.stoneMaxYLevel,
                biome.stoneMinDepth,
                biome.stoneMaxDepth,
                biome.soilMinYLevel,
                biome.soilMaxYLevel,
                biome.soilMinDepth,
                biome.soilMaxDepth,
                biome.waterLevel);
    }

    internal static class TerrainGenerationUtils
    {
        internal readonly struct NoiseAxisSample
        {
            internal NoiseAxisSample(int grid, float smooth)
            {
                Grid = grid;
                Smooth = smooth;
            }

            internal int Grid { get; }

            internal float Smooth { get; }
        }

        // ---------------- Soil smoothing constants ----------------
        // Larger => smoother, bigger coherent patches (8..24 is typical)
        private const int NoiseCellSize = 12;
        // Reduces the soil “reserve” on slopes; lower => smoother (0.2..1.00)
        private const float ReserveSlopeFactor = 0.40f;
        // Smooth noise impact on the soil reserve; lower => smoother (0.00..1.00)
        private const float ReserveNoiseAmp = 0.20f;
        // Maximum blocks to lower near-surface soil; 1..3 keeps top near surface, 7..9 is already quite harsh
        private const int MaxLowering = 6;
        // Exposure weights: reduce NoiseWeight for smoother results; keep sum ≈ 1
        private const float ExposureSlopeWeight = 0.60f;
        private const float ExposureNoiseWeight = 0.40f;

        // -------------------------------------------------------------------------
        // Derive world-space stone & soil spans for a single (cx,cz) column given its surface height.
        // Returns inclusive world Y spans: (-1,-1) for an absent material span. This is a simplified form
        // of the logic inside DeriveStoneSoilSpans used for batch profile construction: it performs the
        // stone depth allocation (respecting soil reserve & min/max depth constraints) and the soil span
        // determination above stone, but does NOT apply any chunk-local clipping or uniform invalidation.
        // -------------------------------------------------------------------------
        internal static (int stoneStart, int stoneEnd, int soilStart, int soilEnd, int waterStart, int waterEnd)
        DeriveWorldStoneSoilSpans(
            int surfaceY,
            Biome biome,
            int worldX,
            int worldZ,
            long seed,
            float slope01 = 0f)
        {
            return DeriveWorldStoneSoilSpansFromNoise(
                surfaceY,
                biome,
                slope01,
                SmoothValueNoise01(worldX, worldZ, seed, NoiseCellSize));
        }

        internal static (int stoneStart, int stoneEnd, int soilStart, int soilEnd, int waterStart, int waterEnd)
        DeriveWorldStoneSoilSpansFromNoise(
            int surfaceY,
            Biome biome,
            float slope01,
            float noise01)
        {
            TerrainMaterialSpanParameters parameters =
                TerrainMaterialSpanParameters.FromBiome(biome);
            return DeriveWorldStoneSoilSpansFromNoise(
                surfaceY,
                in parameters,
                slope01,
                noise01);
        }

        internal static (int stoneStart, int stoneEnd, int soilStart, int soilEnd, int waterStart, int waterEnd)
        DeriveWorldStoneSoilSpansFromNoise(
            int surfaceY,
            scoped in TerrainMaterialSpanParameters parameters,
            float slope01,
            float noise01)
        {
            // ---- Biome specs ----
            int stoneMinY = parameters.StoneMinY; int stoneMaxY = parameters.StoneMaxY;
            int soilMinY = parameters.SoilMinY; int soilMaxY = parameters.SoilMaxY;
            int soilMinDepthSpec = parameters.SoilMinDepth; int soilMaxDepthSpec = parameters.SoilMaxDepth;
            int stoneMinDepthSpec = parameters.StoneMinDepth; int stoneMaxDepthSpec = parameters.StoneMaxDepth;

            if (slope01 < 0f) slope01 = 0f; else if (slope01 > 1f) slope01 = 1f;

            // ---- Smooth value noise in [-1,1] (low frequency) ----
            float noiseSigned = noise01 * 2f - 1f; // [-1..1]

            // ---- Stone span ----
            int stoneBandStartWorld = stoneMinY > 0 ? stoneMinY : 0;
            int stoneBandEndWorld = stoneMaxY < surfaceY ? stoneMaxY : surfaceY;
            int availableStoneBand = stoneBandEndWorld - stoneBandStartWorld + 1;

            // Locally vary the soil reserve (so stone eats more/less of the band).
            // Less reserve on steeper slopes (more exposed stone), add smooth noise.
            int effectiveReserve = 0;
            if (availableStoneBand > 0 && soilMinDepthSpec > 0)
            {
                float reserveF = soilMinDepthSpec
                                 * (1f - ReserveSlopeFactor * slope01)
                                 * (1f + ReserveNoiseAmp * noiseSigned);
                if (reserveF < 0f) reserveF = 0f;
                if (reserveF > soilMinDepthSpec) reserveF = soilMinDepthSpec;
                effectiveReserve = (int)MathF.Floor(reserveF);
                if (effectiveReserve > availableStoneBand) effectiveReserve = availableStoneBand;
                if (effectiveReserve < 0) effectiveReserve = 0;
            }

            int stoneDepth = 0;
            if (availableStoneBand > 0)
            {
                int rawStone = availableStoneBand - effectiveReserve;
                if (rawStone < stoneMinDepthSpec) rawStone = stoneMinDepthSpec;
                if (rawStone > stoneMaxDepthSpec) rawStone = stoneMaxDepthSpec;
                if (rawStone > availableStoneBand) rawStone = availableStoneBand;
                if (rawStone < 0) rawStone = 0;
                stoneDepth = rawStone;
            }

            int stoneStart = stoneDepth > 0 ? stoneBandStartWorld : -1;
            int stoneEnd = stoneDepth > 0 ? (stoneBandStartWorld + stoneDepth - 1) : -1;

            // ---- Soil span (smoothly reduced, keeps near-surface fill) ----
            int soilStartWorld = stoneDepth > 0 ? (stoneEnd + 1) : stoneBandStartWorld;
            if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;

            int soilStart = -1, soilEnd = -1;
            if (soilStartWorld <= soilMaxY && soilStartWorld <= surfaceY)
            {
                int soilBandCapWorld = soilMaxY < surfaceY ? soilMaxY : surfaceY;
                if (soilBandCapWorld >= soilStartWorld)
                {
                    int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                    if (soilAvailable > 0)
                    {
                        int baseSoilDepth = Math.Min(soilMaxDepthSpec, soilAvailable);

                        // Smooth, small lowering to create coherent exposed-stone patches
                        float exposure = MathF.Max(0f,
                            ExposureSlopeWeight * slope01 +
                            ExposureNoiseWeight * (-noiseSigned));

                        int lowering = (int)MathF.Floor(MaxLowering * exposure);
                        if (lowering < 0) lowering = 0;
                        if (lowering > MaxLowering) lowering = MaxLowering;

                        int soilDepth = baseSoilDepth - lowering;
                        if (soilDepth < 0) soilDepth = 0;
                        if (soilDepth > soilAvailable) soilDepth = soilAvailable;

                        if (soilDepth > 0)
                        {
                            soilStart = soilStartWorld;
                            soilEnd = soilStartWorld + soilDepth - 1;
                        }
                    }
                }
            }

            // ---- Water span (fill from actual top solid up to biome water level) ----
            // If soil lowering created air under the analytic surface and the column is underwater,
            // we must start water right above the true top solid, not at surfaceY+1.
            int waterStart = -1, waterEnd = -1;
            {
                int topSolidForWater;
                if (soilEnd >= 0 || stoneEnd >= 0)
                {
                    // Pick the highest existing solid in this column
                    topSolidForWater = Math.Max(soilEnd, stoneEnd);
                }
                else
                {
                    // No solids produced by spans; fall back to the analytic surface
                    topSolidForWater = surfaceY;
                }

                if (parameters.WaterLevel > topSolidForWater)
                {
                    waterStart = topSolidForWater + 1; // starts immediately above the actual top solid (or surface fallback)
                    waterEnd = parameters.WaterLevel;  // inclusive
                }
            }

            return (stoneStart, stoneEnd, soilStart, soilEnd, waterStart, waterEnd);
        }

        internal static void FillSmoothValueNoise01(
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            long seed,
            Span<float> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);
            int valueCount = checked(sizeX * sizeZ);
            if (destination.Length < valueCount)
            {
                throw new ArgumentException(
                    "The destination is smaller than the requested noise map.",
                    nameof(destination));
            }

            NoiseAxisSample[] xBuffer =
                ArrayPool<NoiseAxisSample>.Shared.Rent(sizeX);
            NoiseAxisSample[]? zBuffer = null;
            float[]? latticeBuffer = null;
            try
            {
                zBuffer = ArrayPool<NoiseAxisSample>.Shared.Rent(sizeZ);
                latticeBuffer = ArrayPool<float>.Shared.Rent(
                    GetSmoothValueNoiseLatticeCapacity(sizeX, sizeZ));
                FillSmoothValueNoise01(
                    baseX,
                    baseZ,
                    sizeX,
                    sizeZ,
                    seed,
                    destination,
                    xBuffer.AsSpan(0, sizeX),
                    zBuffer.AsSpan(0, sizeZ),
                    latticeBuffer);
            }
            finally
            {
                if (latticeBuffer is not null)
                    ArrayPool<float>.Shared.Return(latticeBuffer);
                if (zBuffer is not null)
                    ArrayPool<NoiseAxisSample>.Shared.Return(zBuffer);
                ArrayPool<NoiseAxisSample>.Shared.Return(xBuffer);
            }
        }

        internal static void FillSmoothValueNoise01(
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            long seed,
            Span<float> destination,
            Span<NoiseAxisSample> xScratch,
            Span<NoiseAxisSample> zScratch,
            Span<float> latticeScratch)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);
            int valueCount = checked(sizeX * sizeZ);
            if (destination.Length < valueCount)
            {
                throw new ArgumentException(
                    "The destination is smaller than the requested noise map.",
                    nameof(destination));
            }
            if (xScratch.Length < sizeX)
                throw new ArgumentException("The X scratch range is too small.", nameof(xScratch));
            if (zScratch.Length < sizeZ)
                throw new ArgumentException("The Z scratch range is too small.", nameof(zScratch));
            int latticeCapacity = GetSmoothValueNoiseLatticeCapacity(
                sizeX,
                sizeZ);
            if (latticeScratch.Length < latticeCapacity)
            {
                throw new ArgumentException(
                    "The lattice scratch range is too small.",
                    nameof(latticeScratch));
            }

            int firstSizeX = GetFirstContiguousLength(baseX, sizeX);
            int firstSizeZ = GetFirstContiguousLength(baseZ, sizeZ);
            FillSmoothValueNoiseSegment(
                baseX,
                baseZ,
                firstSizeX,
                firstSizeZ,
                seed,
                destination,
                destinationOffset: 0,
                destinationRowStride: sizeZ,
                xScratch,
                zScratch,
                latticeScratch);

            int secondSizeZ = sizeZ - firstSizeZ;
            if (secondSizeZ > 0)
            {
                FillSmoothValueNoiseSegment(
                    baseX,
                    int.MinValue,
                    firstSizeX,
                    secondSizeZ,
                    seed,
                    destination,
                    destinationOffset: firstSizeZ,
                    destinationRowStride: sizeZ,
                    xScratch,
                    zScratch,
                    latticeScratch);
            }

            int secondSizeX = sizeX - firstSizeX;
            if (secondSizeX == 0)
                return;

            int secondXOffset = checked(firstSizeX * sizeZ);
            FillSmoothValueNoiseSegment(
                int.MinValue,
                baseZ,
                secondSizeX,
                firstSizeZ,
                seed,
                destination,
                destinationOffset: secondXOffset,
                destinationRowStride: sizeZ,
                xScratch,
                zScratch,
                latticeScratch);

            if (secondSizeZ > 0)
            {
                FillSmoothValueNoiseSegment(
                    int.MinValue,
                    int.MinValue,
                    secondSizeX,
                    secondSizeZ,
                    seed,
                    destination,
                    destinationOffset: checked(secondXOffset + firstSizeZ),
                    destinationRowStride: sizeZ,
                    xScratch,
                    zScratch,
                    latticeScratch);
            }
        }

        internal static int GetSmoothValueNoiseLatticeCapacity(
            int sizeX,
            int sizeZ)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);
            int gridCapacityX = checked(
                (sizeX - 1) / NoiseCellSize + 3);
            int gridCapacityZ = checked(
                (sizeZ - 1) / NoiseCellSize + 3);
            return checked(gridCapacityX * gridCapacityZ);
        }

        private static void FillSmoothValueNoiseSegment(
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            long seed,
            Span<float> destination,
            int destinationOffset,
            int destinationRowStride,
            Span<NoiseAxisSample> xScratch,
            Span<NoiseAxisSample> zScratch,
            Span<float> latticeScratch)
        {
            Span<NoiseAxisSample> xSamples = xScratch.Slice(0, sizeX);
            Span<NoiseAxisSample> zSamples = zScratch.Slice(0, sizeZ);
            int minimumGridX = int.MaxValue;
            int maximumGridX = int.MinValue;
            for (int x = 0; x < sizeX; x++)
            {
                int worldX = unchecked(baseX + x);
                int gridX = FloorDiv(worldX, NoiseCellSize);
                float fraction =
                    (worldX - gridX * NoiseCellSize) / (float)NoiseCellSize;
                xSamples[x] = new NoiseAxisSample(
                    gridX,
                    SmoothStep(fraction));
                if (gridX < minimumGridX) minimumGridX = gridX;
                if (gridX > maximumGridX) maximumGridX = gridX;
            }

            int minimumGridZ = int.MaxValue;
            int maximumGridZ = int.MinValue;
            for (int z = 0; z < sizeZ; z++)
            {
                int worldZ = unchecked(baseZ + z);
                int gridZ = FloorDiv(worldZ, NoiseCellSize);
                float fraction =
                    (worldZ - gridZ * NoiseCellSize) / (float)NoiseCellSize;
                zSamples[z] = new NoiseAxisSample(
                    gridZ,
                    SmoothStep(fraction));
                if (gridZ < minimumGridZ) minimumGridZ = gridZ;
                if (gridZ > maximumGridZ) maximumGridZ = gridZ;
            }

            int latticeSizeX = checked(maximumGridX - minimumGridX + 2);
            int latticeSizeZ = checked(maximumGridZ - minimumGridZ + 2);
            int latticeCount = checked(latticeSizeX * latticeSizeZ);
            Span<float> lattice = latticeScratch.Slice(0, latticeCount);
            for (int latticeX = 0; latticeX < latticeSizeX; latticeX++)
            {
                int gridX = unchecked(minimumGridX + latticeX);
                int rowOffset = latticeX * latticeSizeZ;
                for (int latticeZ = 0; latticeZ < latticeSizeZ; latticeZ++)
                {
                    int gridZ = unchecked(minimumGridZ + latticeZ);
                    lattice[rowOffset + latticeZ] =
                        HashToUnitFloat(gridX, gridZ, seed);
                }
            }

            for (int x = 0; x < sizeX; x++)
            {
                NoiseAxisSample xSample = xSamples[x];
                int latticeX = xSample.Grid - minimumGridX;
                int firstRow = latticeX * latticeSizeZ;
                int secondRow = firstRow + latticeSizeZ;
                int destinationRow = checked(
                    destinationOffset + x * destinationRowStride);
                for (int z = 0; z < sizeZ; z++)
                {
                    NoiseAxisSample zSample = zSamples[z];
                    int latticeZ = zSample.Grid - minimumGridZ;
                    float valueX0 = Lerp(
                        lattice[firstRow + latticeZ],
                        lattice[secondRow + latticeZ],
                        xSample.Smooth);
                    float valueX1 = Lerp(
                        lattice[firstRow + latticeZ + 1],
                        lattice[secondRow + latticeZ + 1],
                        xSample.Smooth);
                    destination[destinationRow + z] =
                        Lerp(valueX0, valueX1, zSample.Smooth);
                }
            }
        }

        private static int GetFirstContiguousLength(
            int baseCoordinate,
            int length)
        {
            long availableBeforeWrap =
                (long)int.MaxValue - baseCoordinate + 1L;
            return (int)Math.Min(length, availableBeforeWrap);
        }

        internal static float SmoothValueNoise01(
            int x,
            int z,
            long seed,
            int cell = NoiseCellSize)
        {
            int gridX = FloorDiv(x, cell);
            int gridZ = FloorDiv(z, cell);
            float fractionX = (x - gridX * cell) / (float)cell;
            float fractionZ = (z - gridZ * cell) / (float)cell;

            float value00 = HashToUnitFloat(gridX, gridZ, seed);
            float value10 = HashToUnitFloat(gridX + 1, gridZ, seed);
            float value01 = HashToUnitFloat(gridX, gridZ + 1, seed);
            float value11 = HashToUnitFloat(gridX + 1, gridZ + 1, seed);

            float valueX0 = Lerp(value00, value10, SmoothStep(fractionX));
            float valueX1 = Lerp(value01, value11, SmoothStep(fractionX));
            return Lerp(valueX0, valueX1, SmoothStep(fractionZ));
        }

        private static float HashToUnitFloat(int x, int z, long seed) =>
            (Hash(x, z, seed) & 0x3FFFFF) / 4194303f;

        private static uint Hash(int x, int z, long seed)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash ^= (uint)x; hash *= 16777619u;
                hash ^= (uint)z; hash *= 16777619u;
                hash ^= (uint)seed; hash *= 16777619u;
                hash ^= (uint)(seed >> 32); hash *= 16777619u;
                hash ^= hash >> 15;
                hash *= 0x2c1b3c6d;
                hash ^= hash >> 12;
                hash *= 0x297a2d39;
                hash ^= hash >> 15;
                return hash;
            }
        }

        private static float Lerp(float first, float second, float amount) =>
            first + (second - first) * amount;

        private static float SmoothStep(float value) =>
            value * value * (3f - 2f * value);

        private static int FloorDiv(int dividend, int divisor) =>
            (int)Math.Floor(dividend / (double)divisor);
    }
}

using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.WorldGeneration.Terrain;

namespace MVoxelEngine1.Tests
{
    public sealed class TerrainGenerationUtilsTests
    {
        private const int ProductionMapCount = 729;
        private const int ProductionMapSide = 27;
        private const int ProductionMapSize = 160;
        private const int PerformanceWorkerCount = 24;
        private const int PerformanceSampleCount = 6;

        [Theory]
        [InlineData(0, 0, 160, 160, 123456L)]
        [InlineData(-2080, -2080, 160, 160, 123456L)]
        [InlineData(-17, 23, 31, 29, -9223372036854775807L)]
        [InlineData(11, -13, 2, 2, 9223372036854775807L)]
        [InlineData(-12, -12, 1, 1, 0L)]
        [InlineData(2147483645, 0, 5, 3, 123456L)]
        [InlineData(0, 2147483646, 4, 4, 123456L)]
        [InlineData(2147483646, 2147483646, 4, 4, 123456L)]
        public void CachedSmoothValueNoiseMatchesScalarValuesExactly(
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            long seed)
        {
            var cached = new float[checked(sizeX * sizeZ)];

            TerrainGenerationUtils.FillSmoothValueNoise01(
                baseX,
                baseZ,
                sizeX,
                sizeZ,
                seed,
                cached);

            for (int x = 0; x < sizeX; x++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    float expected = ReferenceSmoothValueNoise01(
                        unchecked(baseX + x),
                        unchecked(baseZ + z),
                        seed);
                    int index = x * sizeZ + z;
                    Assert.Equal(
                        BitConverter.SingleToInt32Bits(expected),
                        BitConverter.SingleToInt32Bits(cached[index]));
                }
            }
        }

        [Fact]
        public void CachedSmoothValueNoiseRejectsAnUndersizedDestination()
        {
            var destination = new float[255];

            Assert.Throws<ArgumentException>(() =>
                TerrainGenerationUtils.FillSmoothValueNoise01(
                    0,
                    0,
                    16,
                    16,
                    123456,
                    destination));
        }

        [Theory]
        [InlineData(543, -2080, 1919, 0f)]
        [InlineData(777, -1, -13, 0.5f)]
        [InlineData(12, 1920, -1920, 1f)]
        public void PrecomputedNoisePreservesDerivedColumnSpans(
            int surface,
            int worldX,
            int worldZ,
            float slope)
        {
            Biome biome = CreateDiagnosticBiome();
            float noise = TerrainGenerationUtils.SmoothValueNoise01(
                worldX,
                worldZ,
                123456);

            var scalar = TerrainGenerationUtils.DeriveWorldStoneSoilSpans(
                surface,
                biome,
                worldX,
                worldZ,
                123456,
                slope);
            var precomputed =
                TerrainGenerationUtils.DeriveWorldStoneSoilSpansFromNoise(
                    surface,
                    biome,
                    slope,
                    noise);

            Assert.Equal(scalar, precomputed);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-2080, -2080)]
        [InlineData(-17, 23)]
        public void PrecomputedNoisePreservesCompleteProfileMaps(
            int baseX,
            int baseZ)
        {
            Biome biome = CreateDiagnosticBiome();
            var noise = new float[ProductionMapSize * ProductionMapSize];
            TerrainGenerationUtils.FillSmoothValueNoise01(
                baseX,
                baseZ,
                ProductionMapSize,
                ProductionMapSize,
                123456,
                noise);

            for (int x = 0; x < ProductionMapSize; x++)
            {
                for (int z = 0; z < ProductionMapSize; z++)
                {
                    int surface = 250 + ((x * 31 + z * 17) & 511);
                    float slope = ((x * 13 + z * 7) & 255) / 255f;
                    var scalar =
                        TerrainGenerationUtils.DeriveWorldStoneSoilSpans(
                            surface,
                            biome,
                            baseX + x,
                            baseZ + z,
                            123456,
                            slope);
                    var precomputed =
                        TerrainGenerationUtils.DeriveWorldStoneSoilSpansFromNoise(
                            surface,
                            biome,
                            slope,
                            noise[x * ProductionMapSize + z]);
                    Assert.Equal(scalar, precomputed);
                }
            }
        }

        [Fact(Explicit = true, Timeout = 180_000)]
        [Trait("Category", "Performance")]
        [Trait("Resource", "CPU")]
        public void CachedSmoothValueNoiseAvoidsProductionHashWork()
        {
            NoiseBatchEvidence scalarWarmup = RunProductionNoiseBatch(
                cached: false);
            NoiseBatchEvidence cachedWarmup = RunProductionNoiseBatch(
                cached: true);
            Assert.Equal(scalarWarmup.Checksum, cachedWarmup.Checksum);

            var samples = new NoisePairEvidence[PerformanceSampleCount];
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                bool cachedFirst = (sampleIndex & 1) != 0;
                NoiseBatchEvidence first = RunProductionNoiseBatch(cachedFirst);
                NoiseBatchEvidence second = RunProductionNoiseBatch(!cachedFirst);
                NoiseBatchEvidence scalar = cachedFirst ? second : first;
                NoiseBatchEvidence cached = cachedFirst ? first : second;
                Assert.Equal(scalar.Checksum, cached.Checksum);
                samples[sampleIndex] = new NoisePairEvidence(
                    sampleIndex,
                    cachedFirst,
                    scalar,
                    cached,
                    scalar.ElapsedMilliseconds / cached.ElapsedMilliseconds);
            }

            double scalarMean = samples.Average(
                sample => sample.Scalar.ElapsedMilliseconds);
            double cachedMean = samples.Average(
                sample => sample.Cached.ElapsedMilliseconds);
            var report = new NoiseReuseReport(
                123456,
                ProductionMapCount,
                ProductionMapSize,
                PerformanceWorkerCount,
                PerformanceSampleCount,
                samples,
                scalarMean,
                cachedMean,
                scalarMean / cachedMean,
                cachedMean < scalarMean,
                DateTimeOffset.UtcNow);
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "diagnostics");
            Directory.CreateDirectory(resultsDirectory);
            string resultPath = Path.Combine(
                resultsDirectory,
                $"terrain-value-noise-reuse-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Terrain value-noise result: {resultPath}");
            Console.WriteLine(
                $"Scalar mean: {scalarMean:R} ms. " +
                $"Cached mean: {cachedMean:R} ms.");
        }

        private static NoiseBatchEvidence RunProductionNoiseBatch(bool cached)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            Stopwatch stopwatch = Stopwatch.StartNew();
            var workers = new Task<long>[PerformanceWorkerCount];
            for (int workerIndex = 0;
                workerIndex < PerformanceWorkerCount;
                workerIndex++)
            {
                int capturedWorkerIndex = workerIndex;
                workers[workerIndex] = Task.Run(() => RunNoiseWorker(
                    capturedWorkerIndex,
                    cached));
            }

            Task.WaitAll(workers);
            stopwatch.Stop();
            long checksum = 0;
            foreach (Task<long> worker in workers)
                checksum = unchecked(checksum + worker.Result);
            return new NoiseBatchEvidence(
                cached ? "coarse-lattice-cache" : "scalar-four-hash",
                stopwatch.Elapsed.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                GC.CollectionCount(0) - gen0Before,
                GC.CollectionCount(1) - gen1Before,
                GC.CollectionCount(2) - gen2Before,
                checksum);
        }

        private static long RunNoiseWorker(int workerIndex, bool cached)
        {
            int start = (int)((long)workerIndex * ProductionMapCount /
                PerformanceWorkerCount);
            int end = (int)((long)(workerIndex + 1) * ProductionMapCount /
                PerformanceWorkerCount);
            int valueCount = ProductionMapSize * ProductionMapSize;
            long checksum = 0;
            for (int mapIndex = start; mapIndex < end; mapIndex++)
            {
                int mapX = mapIndex / ProductionMapSide;
                int mapZ = mapIndex % ProductionMapSide;
                int baseX = (mapX - ProductionMapSide / 2) * ProductionMapSize;
                int baseZ = (mapZ - ProductionMapSide / 2) * ProductionMapSize;
                if (cached)
                {
                    float[] values = ArrayPool<float>.Shared.Rent(valueCount);
                    try
                    {
                        TerrainGenerationUtils.FillSmoothValueNoise01(
                            baseX,
                            baseZ,
                            ProductionMapSize,
                            ProductionMapSize,
                            123456,
                            values.AsSpan(0, valueCount));
                        checksum = ConsumeNoise(
                            checksum,
                            values.AsSpan(0, valueCount));
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(values);
                    }
                }
                else
                {
                    for (int x = 0; x < ProductionMapSize; x++)
                    {
                        for (int z = 0; z < ProductionMapSize; z++)
                        {
                            float value = TerrainGenerationUtils.SmoothValueNoise01(
                                baseX + x,
                                baseZ + z,
                                123456);
                            checksum = unchecked(
                                checksum + BitConverter.SingleToInt32Bits(value));
                        }
                    }
                }
            }

            return checksum;
        }

        private static long ConsumeNoise(long checksum, ReadOnlySpan<float> values)
        {
            foreach (float value in values)
            {
                checksum = unchecked(
                    checksum + BitConverter.SingleToInt32Bits(value));
            }

            return checksum;
        }

        private static Biome CreateDiagnosticBiome() => new()
        {
            id = 1,
            name = "diagnostic",
            stoneMinYLevel = 0,
            stoneMaxYLevel = 1000,
            stoneMinDepth = 1,
            stoneMaxDepth = 1000,
            soilMinYLevel = 0,
            soilMaxYLevel = 1000,
            soilMinDepth = 4,
            soilMaxDepth = 10,
            waterLevel = 500,
            microbiomes = [],
            simpleReplacements = []
        };

        private static float ReferenceSmoothValueNoise01(
            int x,
            int z,
            long seed)
        {
            const int cell = 12;
            int gridX = (int)Math.Floor(x / (double)cell);
            int gridZ = (int)Math.Floor(z / (double)cell);
            float fractionX = (x - gridX * cell) / (float)cell;
            float fractionZ = (z - gridZ * cell) / (float)cell;
            uint hash00 = ReferenceHash(gridX, gridZ, seed);
            uint hash10 = ReferenceHash(gridX + 1, gridZ, seed);
            uint hash01 = ReferenceHash(gridX, gridZ + 1, seed);
            uint hash11 = ReferenceHash(gridX + 1, gridZ + 1, seed);
            float value00 = (hash00 & 0x3FFFFF) / 4194303f;
            float value10 = (hash10 & 0x3FFFFF) / 4194303f;
            float value01 = (hash01 & 0x3FFFFF) / 4194303f;
            float value11 = (hash11 & 0x3FFFFF) / 4194303f;
            float valueX0 = ReferenceLerp(
                value00,
                value10,
                ReferenceSmoothStep(fractionX));
            float valueX1 = ReferenceLerp(
                value01,
                value11,
                ReferenceSmoothStep(fractionX));
            return ReferenceLerp(
                valueX0,
                valueX1,
                ReferenceSmoothStep(fractionZ));
        }

        private static uint ReferenceHash(int x, int z, long seed)
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

        private static float ReferenceLerp(
            float first,
            float second,
            float amount) =>
            first + (second - first) * amount;

        private static float ReferenceSmoothStep(float value) =>
            value * value * (3f - 2f * value);

        private sealed record NoiseBatchEvidence(
            string Implementation,
            double ElapsedMilliseconds,
            long ManagedAllocatedBytes,
            int Gen0Collections,
            int Gen1Collections,
            int Gen2Collections,
            long Checksum);

        private sealed record NoisePairEvidence(
            int SampleIndex,
            bool CachedFirst,
            NoiseBatchEvidence Scalar,
            NoiseBatchEvidence Cached,
            double ScalarToCachedSpeedup);

        private sealed record NoiseReuseReport(
            long Seed,
            int MapCount,
            int MapSize,
            int WorkerCount,
            int SampleCount,
            NoisePairEvidence[] Samples,
            double ScalarMeanMilliseconds,
            double CachedMeanMilliseconds,
            double ScalarToCachedMeanSpeedup,
            bool CachedAdvantage,
            DateTimeOffset RecordedUtc);
    }
}

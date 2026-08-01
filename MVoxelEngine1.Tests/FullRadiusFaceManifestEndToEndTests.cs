using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MVoxelEngine1.Tests
{
    public class FullRadiusFaceManifestEndToEndTests
    {
        private const long MaximumWorkingSetBytes = 16L * 1024 * 1024 * 1024;
        private const string SharedWorldName = "FullRadiusFaceManifestWorld";

        [Fact]
        public void RecordedReferenceHasNoTransparentSideFaces()
        {
            using JsonDocument referenceDocument = LoadRecordedReference();
            JsonElement directions = referenceDocument.RootElement
                .GetProperty("faces")
                .GetProperty("transparentDirections");

            for (int direction = 0; direction < 6; direction++)
            {
                long faceCount = directions[direction]
                    .GetProperty("faceCount")
                    .GetInt64();
                if (direction == 3)
                    Assert.True(faceCount > 0);
                else
                    Assert.Equal(0, faceCount);
            }
        }

        [Fact(Explicit = true, Timeout = 630_000)]
        [Trait("Category", "Oracle")]
        [Trait("Resource", "CPU")]
        public async Task ProductionRadiusOptimizedFacesMatchReferenceAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "face-manifests",
                "full-radius");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string optimizedPath = Path.Combine(
                resultsDirectory,
                $"optimized-seed-123456-{runId}.json");
            string referencePath = Path.Combine(
                resultsDirectory,
                $"reference-seed-123456-{runId}.json");

            SimulatedGpuProcessResult optimizedResult = await RunAsync(
                workspace,
                optimizedPath,
                SharedWorldName,
                "Optimized",
                TimeSpan.FromMinutes(3));
            WriteMetrics(optimizedPath, "Optimized", optimizedResult);
            AssertProcess(optimizedPath, optimizedResult, "Optimized");

            SimulatedGpuProcessResult referenceResult = await RunAsync(
                workspace,
                referencePath,
                SharedWorldName,
                "Reference",
                TimeSpan.FromMinutes(6));
            WriteMetrics(referencePath, "Reference", referenceResult);
            AssertProcess(referencePath, referenceResult, "Reference");

            using JsonDocument optimizedDocument = JsonDocument.Parse(
                File.ReadAllText(optimizedPath));
            using JsonDocument referenceDocument = JsonDocument.Parse(
                File.ReadAllText(referencePath));
            JsonElement optimized = optimizedDocument.RootElement;
            JsonElement reference = referenceDocument.RootElement;
            AssertProductionManifest(optimized, "Optimized");
            AssertProductionManifest(reference, "Reference");
            AssertEquivalentManifests(reference, optimized);

            Console.WriteLine($"Full-radius Optimized manifest: {optimizedPath}");
            Console.WriteLine($"Full-radius Reference manifest: {referencePath}");
        }

        [Fact(Explicit = true, Timeout = 390_000)]
        [Trait("Category", "Oracle")]
        [Trait("Resource", "CPU")]
        public async Task CreateProductionRadiusReferenceOracleAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "face-manifests",
                "full-radius");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string referencePath = Path.Combine(
                resultsDirectory,
                $"reference-seed-123456-{runId}.json");

            SimulatedGpuProcessResult result = await RunAsync(
                workspace,
                referencePath,
                "FullRadiusReferenceOracleWorld",
                "Reference",
                TimeSpan.FromMinutes(6));
            WriteMetrics(referencePath, "Reference", result);
            AssertProcess(referencePath, result, "Reference");

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(referencePath));
            AssertProductionManifest(document.RootElement, "Reference");
            Console.WriteLine($"Full-radius Reference manifest: {referencePath}");
        }

        [Fact(Explicit = true, Timeout = 210_000)]
        [Trait("Category", "Oracle")]
        [Trait("Resource", "CPU")]
        public async Task CreateProductionRadiusOptimizedManifestAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "face-manifests",
                "full-radius");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string optimizedPath = Path.Combine(
                resultsDirectory,
                $"optimized-seed-123456-{runId}.json");

            SimulatedGpuProcessResult result = await RunAsync(
                workspace,
                optimizedPath,
                "FullRadiusOptimizedRepeatWorld",
                "Optimized",
                TimeSpan.FromMinutes(3));
            WriteMetrics(optimizedPath, "Optimized", result);
            AssertProcess(optimizedPath, result, "Optimized");

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(optimizedPath));
            AssertProductionManifest(document.RootElement, "Optimized");
            AssertMatchesRecordedReference(document.RootElement);
            Console.WriteLine($"Full-radius Optimized manifest: {optimizedPath}");
        }

        private static async Task<SimulatedGpuProcessResult> RunAsync(
            TestWorkspace workspace,
            string outputPath,
            string worldName,
            string faceGenerationMode,
            TimeSpan timeout)
        {
            ProcessStartInfo startInfo =
                SimulatedGpuUploadTestSupport.CreateFaceManifestStartInfo(
                    workspace,
                    outputPath,
                    worldName,
                    faceGenerationMode);
            return await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                timeout,
                TestContext.Current.CancellationToken,
                MaximumWorkingSetBytes);
        }

        private static void AssertProcess(
            string outputPath,
            SimulatedGpuProcessResult result,
            string mode)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.WindowObserved);
            Assert.InRange(result.PeakWorkingSetBytes, 1, MaximumWorkingSetBytes);
            Assert.True(
                File.Exists(outputPath),
                $"{mode} did not write {outputPath}. " +
                $"Output: {SimulatedGpuUploadTestSupport.Tail(result.StandardOutput)} " +
                $"Error: {SimulatedGpuUploadTestSupport.Tail(result.StandardError)}");
            Assert.Empty(SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath));
        }

        private static void AssertProductionManifest(
            JsonElement manifest,
            string mode)
        {
            Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Default", manifest.GetProperty("game").GetString());
            Assert.Equal(123456, manifest.GetProperty("seed").GetInt32());
            Assert.Equal(mode, manifest.GetProperty("faceGenerationMode").GetString());
            Assert.Equal(160, manifest.GetProperty("chunkSizeX").GetInt32());
            Assert.Equal(160, manifest.GetProperty("chunkSizeY").GetInt32());
            Assert.Equal(160, manifest.GetProperty("chunkSizeZ").GetInt32());
            Assert.Equal(12, manifest.GetProperty("lod1Radius").GetInt32());
            Assert.Equal(15_625, manifest.GetProperty("activeChunkCount").GetInt32());
            Assert.Equal(0, manifest.GetProperty("captureCenterChunkX").GetInt32());
            Assert.Equal(0, manifest.GetProperty("captureCenterChunkY").GetInt32());
            Assert.Equal(0, manifest.GetProperty("captureCenterChunkZ").GetInt32());
            Assert.True(
                manifest.GetProperty("faces").GetProperty("opaqueFaceCount").GetInt64() > 0);
            Assert.True(
                manifest.GetProperty("faces").GetProperty("transparentFaceCount").GetInt64() > 0);
            Assert.Contains(
                manifest.GetProperty("chunks").EnumerateArray(),
                chunk => chunk.GetProperty("fullyOccluded").GetBoolean());
        }

        private static void AssertEquivalentManifests(
            JsonElement reference,
            JsonElement optimized)
        {
            Assert.Equal(
                reference.GetProperty("activeCoordinateSha256").GetString(),
                optimized.GetProperty("activeCoordinateSha256").GetString());
            Assert.Equal(
                reference.GetProperty("gameInputSha256").GetString(),
                optimized.GetProperty("gameInputSha256").GetString());
            Assert.Equal(
                reference.GetProperty("blockRegistrySha256").GetString(),
                optimized.GetProperty("blockRegistrySha256").GetString());
            Assert.Equal(
                reference.GetProperty("faces").GetRawText(),
                optimized.GetProperty("faces").GetRawText());

            JsonElement.ArrayEnumerator referenceChunks =
                reference.GetProperty("chunks").EnumerateArray();
            JsonElement.ArrayEnumerator optimizedChunks =
                optimized.GetProperty("chunks").EnumerateArray();
            while (referenceChunks.MoveNext())
            {
                Assert.True(optimizedChunks.MoveNext());
                JsonElement expected = referenceChunks.Current;
                JsonElement actual = optimizedChunks.Current;
                Assert.Equal(expected.GetProperty("chunkX").GetInt32(), actual.GetProperty("chunkX").GetInt32());
                Assert.Equal(expected.GetProperty("chunkY").GetInt32(), actual.GetProperty("chunkY").GetInt32());
                Assert.Equal(expected.GetProperty("chunkZ").GetInt32(), actual.GetProperty("chunkZ").GetInt32());
                Assert.Equal(expected.GetProperty("fullyOccluded").GetBoolean(), actual.GetProperty("fullyOccluded").GetBoolean());
                Assert.Equal(expected.GetProperty("faces").GetRawText(), actual.GetProperty("faces").GetRawText());
            }

            Assert.False(optimizedChunks.MoveNext());
        }

        private static void AssertMatchesRecordedReference(JsonElement optimized)
        {
            using JsonDocument referenceDocument = LoadRecordedReference();
            JsonElement reference = referenceDocument.RootElement;

            Assert.Equal(
                reference.GetProperty("activeCoordinateSha256").GetString(),
                optimized.GetProperty("activeCoordinateSha256").GetString());
            Assert.Equal(
                reference.GetProperty("gameInputSha256").GetString(),
                optimized.GetProperty("gameInputSha256").GetString());
            Assert.Equal(
                reference.GetProperty("blockRegistrySha256").GetString(),
                optimized.GetProperty("blockRegistrySha256").GetString());
            Assert.True(
                JsonElement.DeepEquals(
                    reference.GetProperty("faces"),
                    optimized.GetProperty("faces")),
                "The production face digest differs from the recorded Reference oracle.");
        }

        private static JsonDocument LoadRecordedReference()
        {
            string referencePath = Path.Combine(
                TestPaths.RepositoryRoot,
                "MVoxelEngine1.Tests",
                "TestData",
                "FaceManifests",
                "default-seed-123456-lod1-radius-12.reference.json");
            return JsonDocument.Parse(File.ReadAllText(referencePath));
        }

        private static void WriteMetrics(
            string manifestPath,
            string mode,
            SimulatedGpuProcessResult result)
        {
            string metricsPath = manifestPath + ".metrics.json";
            var metrics = new
            {
                schemaVersion = 1,
                game = "Default",
                seed = 123456,
                faceGenerationMode = mode,
                chunkSize = new { x = 160, y = 160, z = 160 },
                lod1Radius = 12,
                maximumWorkingSetBytes = MaximumWorkingSetBytes,
                peakWorkingSetBytes = result.PeakWorkingSetBytes,
                manifestSha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(manifestPath))),
                recordedAtUtc = DateTimeOffset.UtcNow
            };
            File.WriteAllText(
                metricsPath,
                JsonSerializer.Serialize(
                    metrics,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

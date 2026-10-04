using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MVoxelEngine1.Tests
{
    public class FullRadiusFaceManifestEndToEndTests
    {
        private const long MaximumWorkingSetBytes = 16L * 1024 * 1024 * 1024;
        private const string SharedWorldName = "FullRadiusFaceManifestWorld";
        private const string InitialReferenceFileName = "default-seed-123456-lod1-radius-12.reference.json";
        private const string WasdReferenceFileName = "default-seed-123456-lod1-radius-12.wasd.reference.json";
        // These limits cover exhaustive post-GTRT identity/texture validation,
        // sorting and hashing; startup performance has a separate fixed gate.
        private static readonly TimeSpan OptimizedCaptureTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ReferenceCaptureTimeout = TimeSpan.FromMinutes(15);

        [Fact]
        public void RecordedReferenceAppliesDefaultReplacementsAndHasTransparentFacesInEveryDirection()
        {
            using JsonDocument referenceDocument = LoadRecordedReference();
            Assert.True(referenceDocument.RootElement.GetProperty("replacementRulesApplied").GetBoolean());
            Assert.Equal(0, referenceDocument.RootElement.GetProperty("faces").GetProperty("opaqueFaceCount").GetInt64());
            JsonElement directions = referenceDocument.RootElement
                .GetProperty("faces")
                .GetProperty("transparentDirections");

            for (int direction = 0; direction < 6; direction++)
            {
                long faceCount = directions[direction]
                    .GetProperty("faceCount")
                    .GetInt64();
                Assert.True(faceCount > 0);
            }
        }

        [Fact(Explicit = true, Timeout = 1_560_000)]
        [Trait("Category", "Oracle")]
        [Trait("Resource", "CPU")]
        public async Task ProductionRadiusOptimizedFacesMatchReferenceAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string resultsDirectory = Path.Combine(
                TestPaths.ResultsRoot,
                "face-manifests",
                "full-radius");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ",System.Globalization.CultureInfo.CurrentCulture);
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
                OptimizedCaptureTimeout).ConfigureAwait(true);
            WriteMetrics(optimizedPath, "Optimized", optimizedResult);
            AssertProcess(optimizedPath, optimizedResult, "Optimized");

            SimulatedGpuProcessResult referenceResult = await RunAsync(
                workspace,
                referencePath,
                SharedWorldName,
                "Reference",
                ReferenceCaptureTimeout).ConfigureAwait(true);
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

        [Fact(Explicit = true, Timeout = 930_000)]
        [Trait("Category", "Oracle")]
        [Trait("Resource", "CPU")]
        public async Task CreateProductionRadiusReferenceOracleAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string resultsDirectory = Path.Combine(
                TestPaths.ResultsRoot,
                "face-manifests",
                "full-radius");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ",System.Globalization.CultureInfo.CurrentCulture);
            string referencePath = Path.Combine(
                resultsDirectory,
                $"reference-seed-123456-{runId}.json");

            SimulatedGpuProcessResult result = await RunAsync(
                workspace,
                referencePath,
                "FullRadiusReferenceOracleWorld",
                "Reference",
                ReferenceCaptureTimeout).ConfigureAwait(true);
            WriteMetrics(referencePath, "Reference", result);
            AssertProcess(referencePath, result, "Reference");

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(referencePath));
            AssertProductionManifest(document.RootElement, "Reference");
            Console.WriteLine($"Full-radius Reference manifest: {referencePath}");
        }

        [Fact(Explicit = true, Timeout = 1_560_000)]
        [Trait("Category", "Oracle")]
        [Trait("Resource", "CPU")]
        public async Task ProductionRadiusTimedWasdFacesMatchReferenceAsync()
        {
            const string inputScript = "W:3,A:3,S:1,D:1,Space:3";
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string resultsDirectory = Path.Combine(TestPaths.ResultsRoot, "face-manifests", "full-radius-wasd");
            Directory.CreateDirectory(resultsDirectory);
            string referencePath = Path.Combine(resultsDirectory, "reference-seed-123456.json");
            string optimizedPath = Path.Combine(resultsDirectory, "optimized-seed-123456.json");
            SimulatedGpuProcessResult optimizedResult = await RunAsync(workspace, optimizedPath,
                "FullRadiusWasdManifestWorld", "Optimized", OptimizedCaptureTimeout, inputScript, 60).ConfigureAwait(true);
            WriteMetrics(optimizedPath, "Optimized", optimizedResult);
            AssertProcess(optimizedPath, optimizedResult, "Optimized");
            SimulatedGpuProcessResult referenceResult = await RunAsync(workspace, referencePath,
                "FullRadiusWasdManifestWorld", "Reference", ReferenceCaptureTimeout, inputScript, 60).ConfigureAwait(true);
            WriteMetrics(referencePath, "Reference", referenceResult);
            AssertProcess(referencePath, referenceResult, "Reference");
            using JsonDocument optimizedDocument = JsonDocument.Parse(File.ReadAllText(optimizedPath));
            using JsonDocument referenceDocument = JsonDocument.Parse(File.ReadAllText(referencePath));
            AssertProductionManifest(optimizedDocument.RootElement, "Optimized", -1, 1, -1);
            AssertProductionManifest(referenceDocument.RootElement, "Reference", -1, 1, -1);
            AssertEquivalentManifests(referenceDocument.RootElement, optimizedDocument.RootElement);
            AssertMatchesRecordedReference(optimizedDocument.RootElement, WasdReferenceFileName);
            foreach (SimulatedGpuProcessResult result in new[] { optimizedResult, referenceResult })
            {
                Assert.Contains("Player chunk position updated to: (-2, 0, -2)", result.StandardOutput,StringComparison.Ordinal);
                Assert.Contains("Player chunk position updated to: (-1, 1, -1)", result.StandardOutput,StringComparison.Ordinal);
                Assert.Contains("11.000000 simulated seconds", result.StandardOutput,StringComparison.Ordinal);
            }
            Console.WriteLine($"Full-radius WASD Optimized manifest: {optimizedPath}");
            Console.WriteLine($"Full-radius WASD Reference manifest: {referencePath}");
        }

        [Fact(Explicit = true, Timeout = 630_000)]
        [Trait("Category", "Oracle")]
        [Trait("Resource", "CPU")]
        public async Task CreateProductionRadiusOptimizedManifestAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string resultsDirectory = Path.Combine(
                TestPaths.ResultsRoot,
                "face-manifests",
                "full-radius");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ",System.Globalization.CultureInfo.CurrentCulture);
            string optimizedPath = Path.Combine(
                resultsDirectory,
                $"optimized-seed-123456-{runId}.json");

            SimulatedGpuProcessResult result = await RunAsync(
                workspace,
                optimizedPath,
                "FullRadiusOptimizedRepeatWorld",
                "Optimized",
                OptimizedCaptureTimeout).ConfigureAwait(true);
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
            TimeSpan timeout,
            string? inputScript = null,
            int? frameRate = null)
        {
            ProcessStartInfo startInfo =
                SimulatedGpuUploadTestSupport.CreateFaceManifestStartInfo(
                    workspace,
                    outputPath,
                    worldName,
                    faceGenerationMode,
                    inputScript,
                    frameRate);
            return await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                timeout,
                TestContext.Current.CancellationToken,
                MaximumWorkingSetBytes).ConfigureAwait(true);
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
            string mode,
            int centerX = 0,
            int centerY = 0,
            int centerZ = 0)
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
            Assert.Equal(centerX, manifest.GetProperty("captureCenterChunkX").GetInt32());
            Assert.Equal(centerY, manifest.GetProperty("captureCenterChunkY").GetInt32());
            Assert.Equal(centerZ, manifest.GetProperty("captureCenterChunkZ").GetInt32());
            Assert.Equal(0,
                manifest.GetProperty("faces").GetProperty("opaqueFaceCount").GetInt64());
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

        private static void AssertMatchesRecordedReference(
            JsonElement optimized,
            string referenceFileName = InitialReferenceFileName)
        {
            using JsonDocument referenceDocument = LoadRecordedReference(referenceFileName);
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

        private static JsonDocument LoadRecordedReference(string referenceFileName = InitialReferenceFileName)
        {
            string referencePath = Path.Combine(
                TestPaths.RepositoryRoot,
                "MVoxelEngine1.Tests",
                "TestData",
                "FaceManifests",
                referenceFileName);
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

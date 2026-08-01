using System.Diagnostics;
using System.Text.Json;

namespace MVoxelEngine1.Tests
{
    public class FaceManifestEndToEndTests
    {
        [Fact(Timeout = 160_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task OptimizedFacesMatchReferenceFacesAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                maximumWorldHeight: 768,
                lod1RenderDistance: 2,
                chunkSizeY: 256,
                chunkSizeX: 32,
                chunkSizeZ: 32);
            SimulatedGpuUploadTestSupport.SetWaterLevel(
                workspace.GameDataRoot,
                waterLevel: 551);
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "face-manifests");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string referencePath = Path.Combine(
                resultsDirectory,
                $"reference-seed-123456-{runId}.json");
            string optimizedPath = Path.Combine(
                resultsDirectory,
                $"optimized-seed-123456-{runId}.json");
            string referenceRepeatPath = Path.Combine(
                resultsDirectory,
                $"reference-repeat-seed-123456-{runId}.json");

            SimulatedGpuProcessResult referenceResult = await RunManifestAsync(
                workspace,
                referencePath,
                "ReferenceManifestWorld",
                "Reference");
            SimulatedGpuProcessResult optimizedResult = await RunManifestAsync(
                workspace,
                optimizedPath,
                "OptimizedManifestWorld",
                "Optimized");
            SimulatedGpuProcessResult referenceRepeatResult = await RunManifestAsync(
                workspace,
                referenceRepeatPath,
                "ReferenceRepeatManifestWorld",
                "Reference");

            AssertManifestProcess(referenceResult, referencePath, "Reference");
            AssertManifestProcess(optimizedResult, optimizedPath, "Optimized");
            AssertManifestProcess(
                referenceRepeatResult,
                referenceRepeatPath,
                "Reference repeat");

            using JsonDocument referenceDocument = JsonDocument.Parse(
                File.ReadAllText(referencePath));
            using JsonDocument optimizedDocument = JsonDocument.Parse(
                File.ReadAllText(optimizedPath));
            using JsonDocument referenceRepeatDocument = JsonDocument.Parse(
                File.ReadAllText(referenceRepeatPath));
            JsonElement reference = referenceDocument.RootElement;
            JsonElement optimized = optimizedDocument.RootElement;
            JsonElement referenceRepeat = referenceRepeatDocument.RootElement;

            Assert.Equal(1, reference.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Reference", reference.GetProperty("faceGenerationMode").GetString());
            Assert.Equal("Optimized", optimized.GetProperty("faceGenerationMode").GetString());
            Assert.Equal("Reference", referenceRepeat.GetProperty("faceGenerationMode").GetString());
            Assert.Equal(32, reference.GetProperty("chunkSizeX").GetInt32());
            Assert.Equal(32, reference.GetProperty("chunkSizeZ").GetInt32());
            Assert.Equal(
                reference.GetProperty("canonicalEncoding").GetString(),
                optimized.GetProperty("canonicalEncoding").GetString());
            Assert.Equal(
                reference.GetProperty("activeCoordinateSha256").GetString(),
                optimized.GetProperty("activeCoordinateSha256").GetString());
            Assert.Equal(
                reference.GetProperty("gameInputSha256").GetString(),
                optimized.GetProperty("gameInputSha256").GetString());
            Assert.Equal(
                reference.GetProperty("blockRegistrySha256").GetString(),
                optimized.GetProperty("blockRegistrySha256").GetString());

            JsonElement referenceFaces = reference.GetProperty("faces");
            JsonElement optimizedFaces = optimized.GetProperty("faces");
            Assert.True(referenceFaces.GetProperty("opaqueFaceCount").GetInt64() > 0);
            Assert.True(referenceFaces.GetProperty("transparentFaceCount").GetInt64() > 0);
            Assert.Contains(
                reference.GetProperty("chunks").EnumerateArray(),
                chunk =>
                    chunk.GetProperty("faces").GetProperty("opaqueFaceCount").GetInt64() > 0 &&
                    chunk.GetProperty("faces").GetProperty("transparentFaceCount").GetInt64() > 0);
            Assert.Contains(
                reference.GetProperty("chunks").EnumerateArray(),
                chunk => chunk.GetProperty("fullyOccluded").GetBoolean());
            JsonElement transparentDirections = referenceFaces.GetProperty(
                "transparentDirections");
            Assert.True(
                transparentDirections.EnumerateArray().Single(
                    direction => direction.GetProperty("direction").GetByte() == 3)
                    .GetProperty("faceCount").GetInt64() > 0);
            Assert.All(
                transparentDirections.EnumerateArray().Where(
                    direction => direction.GetProperty("direction").GetByte() != 3),
                direction => Assert.Equal(
                    0L,
                    direction.GetProperty("faceCount").GetInt64()));
            Assert.Equal(
                referenceFaces.GetProperty("sha256").GetString(),
                optimizedFaces.GetProperty("sha256").GetString());
            Assert.Equal(
                referenceFaces.GetProperty("sha256").GetString(),
                referenceRepeat.GetProperty("faces").GetProperty("sha256").GetString());
        }

        [Fact(Timeout = 160_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task OptimizedStreamingFacesMatchReferenceAfterTimedMovementAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                maximumWorldHeight: 768,
                lod1RenderDistance: 2,
                chunkSizeY: 256,
                chunkSizeX: 32,
                chunkSizeZ: 32);
            SimulatedGpuUploadTestSupport.SetWaterLevel(
                workspace.GameDataRoot,
                waterLevel: 551);
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "face-manifests");
            Directory.CreateDirectory(resultsDirectory);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string referencePath = Path.Combine(
                resultsDirectory,
                $"reference-movement-seed-123456-{runId}.json");
            string optimizedPath = Path.Combine(
                resultsDirectory,
                $"optimized-movement-seed-123456-{runId}.json");
            const string inputScript = "W:1.1,D:1.1,Space:4.5";

            SimulatedGpuProcessResult referenceResult = await RunManifestAsync(
                workspace,
                referencePath,
                "ReferenceMovementManifestWorld",
                "Reference",
                inputScript,
                10);
            SimulatedGpuProcessResult optimizedResult = await RunManifestAsync(
                workspace,
                optimizedPath,
                "OptimizedMovementManifestWorld",
                "Optimized",
                inputScript,
                10);

            AssertManifestProcess(referenceResult, referencePath, "Reference movement");
            AssertManifestProcess(optimizedResult, optimizedPath, "Optimized movement");

            using JsonDocument referenceDocument = JsonDocument.Parse(
                File.ReadAllText(referencePath));
            using JsonDocument optimizedDocument = JsonDocument.Parse(
                File.ReadAllText(optimizedPath));
            JsonElement reference = referenceDocument.RootElement;
            JsonElement optimized = optimizedDocument.RootElement;

            Assert.Equal("Reference", reference.GetProperty("faceGenerationMode").GetString());
            Assert.Equal("Optimized", optimized.GetProperty("faceGenerationMode").GetString());
            Assert.Equal(2, reference.GetProperty("captureCenterChunkX").GetInt32());
            Assert.Equal(1, reference.GetProperty("captureCenterChunkY").GetInt32());
            Assert.Equal(-3, reference.GetProperty("captureCenterChunkZ").GetInt32());
            Assert.Equal(
                reference.GetProperty("captureCenterChunkX").GetInt32(),
                optimized.GetProperty("captureCenterChunkX").GetInt32());
            Assert.Equal(
                reference.GetProperty("captureCenterChunkY").GetInt32(),
                optimized.GetProperty("captureCenterChunkY").GetInt32());
            Assert.Equal(
                reference.GetProperty("captureCenterChunkZ").GetInt32(),
                optimized.GetProperty("captureCenterChunkZ").GetInt32());
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
                reference.GetProperty("faces").GetProperty("transparentFaceCount").GetInt64() > 0);
            Assert.Contains(
                reference.GetProperty("chunks").EnumerateArray(),
                chunk => chunk.GetProperty("fullyOccluded").GetBoolean());
            Assert.Equal(
                reference.GetProperty("faces").GetProperty("sha256").GetString(),
                optimized.GetProperty("faces").GetProperty("sha256").GetString());
        }

        private static async Task<SimulatedGpuProcessResult> RunManifestAsync(
            TestWorkspace workspace,
            string outputPath,
            string worldName,
            string faceGenerationMode,
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
                TimeSpan.FromSeconds(inputScript is null ? 75 : 100),
                TestContext.Current.CancellationToken);
        }

        private static void AssertManifestProcess(
            SimulatedGpuProcessResult result,
            string outputPath,
            string faceGenerationMode)
        {
            Assert.True(
                result.ExitCode == 0,
                $"{faceGenerationMode} manifest failed. " +
                $"Output: {SimulatedGpuUploadTestSupport.Tail(result.StandardOutput)} " +
                $"Error: {SimulatedGpuUploadTestSupport.Tail(result.StandardError)}");
            Assert.False(result.WindowObserved);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath));
        }
    }
}

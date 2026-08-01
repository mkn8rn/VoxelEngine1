using System.Text.Json;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.Tests
{
    public class ReferenceFaceGeneratorEndToEndTests
    {
        [Fact(Timeout = 100_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task ReferenceModeKeepsMixedTransparentStreamingExactAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                maximumWorldHeight: 640,
                lod1RenderDistance: 1);
            SimulatedGpuUploadTestSupport.SetWaterLevel(
                workspace.GameDataRoot,
                waterLevel: 551);
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "reference-face-streaming");
            Directory.CreateDirectory(resultsDirectory);
            string outputPath = Path.Combine(
                resultsDirectory,
                $"reference-seed-123456-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            var startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "ReferenceRenderWorld",
                "Space:9,W:3",
                10);
            startInfo.ArgumentList.Add("--faceGenerationMode");
            startInfo.ArgumentList.Add("Reference");

            SimulatedGpuProcessResult result = await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(75),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.WindowObserved);
            Assert.Contains("Face generation mode: Reference.", result.StandardOutput);
            Assert.True(File.Exists(outputPath));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputPath));
            JsonElement root = document.RootElement;
            Assert.Equal("Reference", root.GetProperty("faceGenerationMode").GetString());

            JsonElement[] uploads = root.GetProperty("events")
                .EnumerateArray()
                .Where(element => element.GetProperty("type").GetString() == "simulatedGpuUpload")
                .ToArray();
            Assert.NotEmpty(uploads);
            Assert.All(
                uploads,
                upload => Assert.Equal(
                    "Reference",
                    upload.GetProperty("faceGenerationMode").GetString()));

            Assert.Contains(
                uploads,
                upload =>
                    upload.GetProperty("opaqueFaceCount").GetInt32() > 0 &&
                    upload.GetProperty("transparentFaceCount").GetInt32() > 0);
            Assert.Contains(
                uploads,
                upload => upload.GetProperty("transparentFaceCount").GetInt32() > 0);

            HashSet<ushort> nonOpaqueBlockIds = LoadNonOpaqueBlockIds(
                workspace.GameDataRoot);

            foreach (JsonElement upload in uploads)
            {
                foreach (JsonElement face in upload.GetProperty("opaqueFaces").EnumerateArray())
                {
                    ushort blockId = face.GetProperty("blockId").GetUInt16();
                    ushort neighborBlockId = face
                        .GetProperty("neighborBlockIdAtUpload")
                        .GetUInt16();
                    Assert.True(
                        blockId != (ushort)BaseBlockType.Empty &&
                        !nonOpaqueBlockIds.Contains(blockId),
                        "Reference mode put a non-opaque block in the opaque pass. " +
                        $"Chunk: {upload.GetProperty("chunkIndex").GetRawText()}. " +
                        $"Face: {face.GetRawText()}.");
                    Assert.True(
                        nonOpaqueBlockIds.Contains(neighborBlockId),
                        "Reference mode uploaded an opaque face against an opaque block. " +
                        $"Chunk: {upload.GetProperty("chunkIndex").GetRawText()}. " +
                        $"Face: {face.GetRawText()}.");
                }

                foreach (JsonElement face in upload.GetProperty("transparentFaces").EnumerateArray())
                {
                    ushort blockId = face.GetProperty("blockId").GetUInt16();
                    ushort neighborBlockId = face
                        .GetProperty("neighborBlockIdAtUpload")
                        .GetUInt16();
                    Assert.True(
                        blockId != neighborBlockId,
                        "Reference mode uploaded an equal transparent boundary. " +
                        $"Chunk: {upload.GetProperty("chunkIndex").GetRawText()}. " +
                        $"Face: {face.GetRawText()}.");
                    Assert.True(
                        blockId != (ushort)BaseBlockType.Empty &&
                        nonOpaqueBlockIds.Contains(blockId),
                        "Reference mode put an opaque block in the transparent pass. " +
                        $"Chunk: {upload.GetProperty("chunkIndex").GetRawText()}. " +
                        $"Face: {face.GetRawText()}.");
                    Assert.True(
                        neighborBlockId == (ushort)BaseBlockType.Empty ||
                        (nonOpaqueBlockIds.Contains(neighborBlockId) &&
                         neighborBlockId != blockId),
                        "Reference mode uploaded a hidden transparent face. " +
                        $"Chunk: {upload.GetProperty("chunkIndex").GetRawText()}. " +
                        $"Face: {face.GetRawText()}.");
                }
            }
        }

        private static HashSet<ushort> LoadNonOpaqueBlockIds(string gameDataRoot)
        {
            var result = new HashSet<ushort>
            {
                (ushort)BaseBlockType.Empty,
                (ushort)BaseBlockType.Gas,
                (ushort)BaseBlockType.Glass,
                (ushort)BaseBlockType.Water
            };
            string blockTypesDirectory = Path.Combine(
                gameDataRoot,
                "Default",
                "Data",
                "Blocks",
                "Types");

            foreach (string path in Directory.EnumerateFiles(
                         blockTypesDirectory,
                         "*.txt",
                         SearchOption.TopDirectoryOnly))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement definition = document.RootElement;
                if (definition.GetProperty("IsTransparent").GetBoolean())
                    result.Add(definition.GetProperty("ID").GetUInt16());
            }

            return result;
        }
    }
}

using System.Text.Json;

namespace MVoxelEngine1.Tests
{
    public class ReferenceFaceGeneratorEndToEndTests
    {
        [Fact(Timeout = 60_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task ReferenceModeReachesHeadlessUploadStreamAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                lod1RenderDistance: 0);
            string outputPath = Path.Combine(workspace.Root, "reference-render.json");
            var startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "ReferenceRenderWorld",
                "W:0.1",
                10);
            startInfo.ArgumentList.Add("--faceGenerationMode");
            startInfo.ArgumentList.Add("Reference");

            SimulatedGpuProcessResult result = await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(45),
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

            foreach (JsonElement upload in uploads)
            {
                foreach (JsonElement face in upload.GetProperty("transparentFaces").EnumerateArray())
                {
                    Assert.NotEqual(
                        face.GetProperty("blockId").GetUInt16(),
                        face.GetProperty("neighborBlockIdAtUpload").GetUInt16());
                }
            }
        }
    }
}

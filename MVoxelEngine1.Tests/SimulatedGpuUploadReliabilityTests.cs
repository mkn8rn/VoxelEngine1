using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MVoxelEngine1.Tests
{
    public class SimulatedGpuUploadReliabilityTests
    {
        [Fact(Timeout = 90_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task SlowWriterKeepsBoundedOrderedRecordsWithoutLossAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(workspace.GameDataRoot);
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "simulated-gpu-uploads");
            Directory.CreateDirectory(resultsDirectory);
            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string outputPath = Path.Combine(resultsDirectory, $"slow-writer-seed-123456-{timestamp}.json");
            ProcessStartInfo startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "SlowWriterWorld",
                "W:1",
                frameRate: 1000,
                writerDelayMilliseconds: 25);

            SimulatedGpuProcessResult result = await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(75),
                TestContext.Current.CancellationToken);
            Assert.True(
                result.ExitCode == 0,
                $"Application exited with code {result.ExitCode}. " +
                $"Output: {SimulatedGpuUploadTestSupport.Tail(result.StandardOutput)} " +
                $"Error: {SimulatedGpuUploadTestSupport.Tail(result.StandardError)}");
            Assert.False(result.WindowObserved);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputPath));
            JsonElement root = document.RootElement;
            SimulatedGpuUploadTestSupport.AssertCompleteOrderedStream(root);
            Assert.Equal(4, root.GetProperty("recordQueueCapacity").GetInt32());
            Assert.Equal("wait", root.GetProperty("recordQueueFullPolicy").GetString());
            Assert.Equal(25, root.GetProperty("writerDelayMilliseconds").GetInt32());
            JsonElement summary = root.GetProperty("summary");
            Assert.Equal(4, summary.GetProperty("peakRetainedRecordCount").GetInt32());
            long peakRetainedPayloadBytes = summary
                .GetProperty("peakRetainedRecordPayloadBytes")
                .GetInt64();
            Assert.True(peakRetainedPayloadBytes > 0);
            Assert.InRange(result.PeakWorkingSetBytes, 1, 1_073_741_824);

            string outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath)));
            string metricsPath = Path.Combine(
                resultsDirectory,
                $"slow-writer-seed-123456-{timestamp}.metrics.json");
            File.WriteAllText(
                metricsPath,
                JsonSerializer.Serialize(
                    new
                    {
                        outputPath,
                        outputSha256,
                        result.PeakWorkingSetBytes,
                        QueueCapacity = root.GetProperty("recordQueueCapacity").GetInt32(),
                        PeakRetainedRecordCount = summary
                            .GetProperty("peakRetainedRecordCount")
                            .GetInt32(),
                        PeakRetainedRecordPayloadBytes = peakRetainedPayloadBytes,
                        StreamRecordCount = summary.GetProperty("streamRecordCount").GetInt64(),
                        CompletionSequence = summary.GetProperty("completionSequence").GetInt64(),
                        SilentRecordLossAllowed = summary
                            .GetProperty("silentRecordLossAllowed")
                            .GetBoolean()
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Slow writer result: {outputPath}");
            Console.WriteLine($"Slow writer metrics: {metricsPath}");
        }

        [Fact(Timeout = 150_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task WaterFixtureStreamsCompleteTransparentFacesAndDrawIdentityAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                maximumWorldHeight: 640,
                lod1RenderDistance: 0);
            SimulatedGpuUploadTestSupport.SetWaterLevel(workspace.GameDataRoot, waterLevel: 551);
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "simulated-gpu-uploads");
            Directory.CreateDirectory(resultsDirectory);
            string outputPath = Path.Combine(
                resultsDirectory,
                $"transparent-seed-123456-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            ProcessStartInfo startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "TransparentWaterWorld",
                "Space:9,W+S:15",
                frameRate: 1);

            SimulatedGpuProcessResult result = await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(135),
                TestContext.Current.CancellationToken);
            Assert.True(
                result.ExitCode == 0,
                $"Application exited with code {result.ExitCode}. " +
                $"Output: {SimulatedGpuUploadTestSupport.Tail(result.StandardOutput)} " +
                $"Error: {SimulatedGpuUploadTestSupport.Tail(result.StandardError)}");
            Assert.False(result.WindowObserved);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputPath));
            JsonElement root = document.RootElement;
            SimulatedGpuUploadTestSupport.AssertCompleteOrderedStream(root);
            JsonElement[] events = root.GetProperty("events").EnumerateArray().ToArray();
            JsonElement[] uploads = events
                .Where(element => element.GetProperty("type").GetString() == "simulatedGpuUpload")
                .ToArray();
            JsonElement[] transparentUploads = uploads
                .Where(upload => upload.GetProperty("transparentFaceCount").GetInt32() > 0)
                .ToArray();
            Assert.True(
                transparentUploads.Length > 0,
                "No transparent upload was recorded. Output: " +
                SimulatedGpuUploadTestSupport.Tail(result.StandardOutput));

            var transparentUploadCounts = new Dictionary<long, int>();
            foreach (JsonElement upload in transparentUploads)
            {
                long renderDataId = upload.GetProperty("renderDataId").GetInt64();
                int expectedCount = upload.GetProperty("transparentFaceCount").GetInt32();
                JsonElement[] faces = upload.GetProperty("transparentFaces").EnumerateArray().ToArray();
                Assert.Equal(expectedCount, faces.Length);
                transparentUploadCounts[renderDataId] = expectedCount;
                Assert.All(faces, AssertCompleteTransparentFace);
            }

            var activeUploads = new HashSet<long>();
            bool transparentDrawObserved = false;
            foreach (JsonElement streamEvent in events)
            {
                string type = streamEvent.GetProperty("type").GetString()!;
                if (type == "simulatedGpuUpload")
                {
                    activeUploads.Add(streamEvent.GetProperty("renderDataId").GetInt64());
                }
                else if (type == "simulatedGpuDeletion")
                {
                    activeUploads.Remove(streamEvent.GetProperty("renderDataId").GetInt64());
                }
                else if (type == "renderFrame")
                {
                    foreach (JsonElement idElement in streamEvent
                        .GetProperty("transparentDrawRenderDataIds")
                        .EnumerateArray())
                    {
                        long renderDataId = idElement.GetInt64();
                        Assert.Contains(renderDataId, activeUploads);
                        Assert.True(transparentUploadCounts.TryGetValue(renderDataId, out int faceCount));
                        Assert.True(faceCount > 0);
                        transparentDrawObserved = true;
                    }
                }
            }

            Assert.True(transparentDrawObserved);
            Assert.Contains(
                transparentUploads,
                upload => upload.GetProperty("transparentFaces")
                    .EnumerateArray()
                    .Any(face => face.GetProperty("blockId").GetUInt16() == 11));
            Console.WriteLine($"Transparent render result: {outputPath}");
        }

        [Fact(Timeout = 45_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task WriterFailureDoesNotPublishFinalOrIncompleteOutputAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(workspace.GameDataRoot);
            string outputPath = Path.Combine(workspace.Root, "writer-failure.json");
            ProcessStartInfo startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "WriterFailureWorld",
                "W:0.25",
                frameRate: 30,
                writerFailAfterRecords: 1);

            SimulatedGpuProcessResult result = await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(result.WindowObserved);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath));
            Assert.Contains(
                "writer failure was requested",
                result.StandardError,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact(Timeout = 45_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task InterruptedProcessNeverPublishesPartialFinalOutputAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(workspace.GameDataRoot);
            string outputPath = Path.Combine(workspace.Root, "interrupted.json");
            ProcessStartInfo startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "InterruptedWorld",
                "W:30",
                frameRate: 60,
                writerDelayMilliseconds: 25);
            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "Application process did not start.");
            CancellationToken testCancellation = TestContext.Current.CancellationToken;
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(testCancellation);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(testCancellation);
            bool windowObserved = false;
            string[] incompleteFiles = Array.Empty<string>();
            try
            {
                var waitClock = Stopwatch.StartNew();
                while (!process.HasExited && waitClock.Elapsed < TimeSpan.FromSeconds(20))
                {
                    process.Refresh();
                    if (OperatingSystem.IsWindows())
                        windowObserved |= process.MainWindowHandle != IntPtr.Zero;
                    incompleteFiles = SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath);
                    if (incompleteFiles.Length > 0 && new FileInfo(incompleteFiles[0]).Length > 0)
                        break;

                    await Task.Delay(20, testCancellation);
                }

                Assert.False(process.HasExited);
                Assert.False(windowObserved);
                Assert.False(File.Exists(outputPath));
                Assert.NotEmpty(incompleteFiles);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(testCancellation);
                await standardOutputTask;
                await standardErrorTask;
            }

            Assert.False(File.Exists(outputPath));
        }

        private static void AssertCompleteTransparentFace(JsonElement face)
        {
            Assert.Equal("transparent", face.GetProperty("renderPass").GetString());
            Assert.Equal(3, face.GetProperty("offset").GetArrayLength());
            Assert.Equal(3, face.GetProperty("voxelWorld").GetArrayLength());
            Assert.Equal(3, face.GetProperty("neighborWorldAtUpload").GetArrayLength());
            Assert.InRange(face.GetProperty("faceDirection").GetByte(), (byte)0, (byte)5);
            Assert.False(string.IsNullOrWhiteSpace(face.GetProperty("faceName").GetString()));
            Assert.True(face.TryGetProperty("tileIndex", out _));
            Assert.True(face.TryGetProperty("blockId", out _));
            Assert.True(face.TryGetProperty("blockName", out _));
            Assert.True(face.TryGetProperty("neighborBlockIdAtUpload", out _));
            Assert.True(face.TryGetProperty("neighborBlockNameAtUpload", out _));
        }
    }
}

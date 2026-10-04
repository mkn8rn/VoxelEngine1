using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MVoxelEngine1.Tests
{
    public class SimulatedGpuUploadReliabilityTests
    {

    private static readonly System.Text.Json.JsonSerializerOptions EvidenceJsonOptions0 = new JsonSerializerOptions { WriteIndented = true };
        [Fact(Timeout = 90_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task SlowWriterKeepsBoundedOrderedRecordsWithoutLossAsync()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(workspace.GameDataRoot);
            string resultsDirectory = Path.Combine(
                TestPaths.ResultsRoot,
                "simulated-gpu-uploads");
            Directory.CreateDirectory(resultsDirectory);
            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ",System.Globalization.CultureInfo.CurrentCulture);
            string outputPath = Path.Combine(resultsDirectory, $"slow-writer-seed-123456-{timestamp}.json");
            ProcessStartInfo startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "SlowWriterWorld",
                "W:1",
                frameRate: 1000,
                writerDelayMilliseconds: 25);
            await ValidateSlowWriterKeepsBoundedOrderedRecordsWithoutLossAsyncEvidenceAsync(resultsDirectory, timestamp, outputPath, startInfo).ConfigureAwait(true);
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
                TestPaths.ResultsRoot,
                "simulated-gpu-uploads");
            Directory.CreateDirectory(resultsDirectory);
            string outputPath = Path.Combine(
                resultsDirectory,
                $"transparent-seed-123456-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            ProcessStartInfo startInfo = SimulatedGpuUploadTestSupport.CreateStartInfo(
                workspace,
                outputPath,
                "TransparentWaterWorld",
                // Radius zero must place the camera in the water surface chunk.
                "Space:9.25,W+S:15",
                frameRate: 1);

            SimulatedGpuProcessResult result = await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(135),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(
                result.ExitCode == 0,
                $"Application exited with code {result.ExitCode}. " +
                $"Output: {SimulatedGpuUploadTestSupport.Tail(result.StandardOutput)} " +
                $"Error: {SimulatedGpuUploadTestSupport.Tail(result.StandardError)}");
            Assert.False(result.WindowObserved);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath));

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            JsonElement root = document.RootElement;
            SimulatedGpuUploadTestSupport.AssertCompleteOrderedStream(root);
            JsonElement[] events = root.GetProperty("events").EnumerateArray().ToArray();
            JsonElement[] uploads = events
                .Where(element => string.Equals(element.GetProperty("type").GetString(), "simulatedGpuUpload", StringComparison.Ordinal))
                .ToArray();
            JsonElement[] transparentUploads = uploads
                .Where(upload => upload.GetProperty("transparentFaceCount").GetInt32() > 0)
                .ToArray();
            Assert.True(
                transparentUploads.Length > 0,
                "No transparent upload was recorded. Output: " +
                SimulatedGpuUploadTestSupport.Tail(result.StandardOutput));

            var transparentUploadCounts = new Dictionary<long, int>();
            ValidateWaterFixtureStreamsCompleteTransparentFacesAndDrawIdentityAsyncEvidence(outputPath, events, transparentUploads, transparentUploadCounts);
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
                TestContext.Current.CancellationToken).ConfigureAwait(true);
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

                    await Task.Delay(20, testCancellation).ConfigureAwait(true);
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

                await process.WaitForExitAsync(testCancellation).ConfigureAwait(true);
                await standardOutputTask.ConfigureAwait(true);
                await standardErrorTask.ConfigureAwait(true);
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

        private static async global::System.Threading.Tasks.Task ValidateSlowWriterKeepsBoundedOrderedRecordsWithoutLossAsyncEvidenceAsync(string resultsDirectory, string timestamp, string outputPath, global::System.Diagnostics.ProcessStartInfo startInfo)
        {

            SimulatedGpuProcessResult result = await SimulatedGpuUploadTestSupport.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(75),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(
                result.ExitCode == 0,
                $"Application exited with code {result.ExitCode}. " +
                $"Output: {SimulatedGpuUploadTestSupport.Tail(result.StandardOutput)} " +
                $"Error: {SimulatedGpuUploadTestSupport.Tail(result.StandardError)}");
            Assert.False(result.WindowObserved);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(SimulatedGpuUploadTestSupport.FindIncompleteFiles(outputPath));

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
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

            string outputSha256 = Convert.ToHexString(SHA256.HashData((await File.ReadAllBytesAsync(outputPath,TestContext.Current.CancellationToken).ConfigureAwait(true))));
            string metricsPath = Path.Combine(
                resultsDirectory,
                $"slow-writer-seed-123456-{timestamp}.metrics.json");
            await File.WriteAllTextAsync(
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
                    EvidenceJsonOptions0), TestContext.Current.CancellationToken).ConfigureAwait(true);
            Console.WriteLine($"Slow writer result: {outputPath}");
            Console.WriteLine($"Slow writer metrics: {metricsPath}");

        }

        private static void ValidateWaterFixtureStreamsCompleteTransparentFacesAndDrawIdentityAsyncEvidence(string outputPath, global::System.Text.Json.JsonElement[] events, global::System.Text.Json.JsonElement[] transparentUploads, global::System.Collections.Generic.Dictionary<long, int> transparentUploadCounts)
        {
            foreach (JsonElement upload in transparentUploads)
            {
                long renderDataId = upload.GetProperty("renderDataId").GetInt64();
                int expectedCount = upload.GetProperty("transparentFaceCount").GetInt32();
                JsonElement[] faces = upload.GetProperty("transparentFaces").EnumerateArray().ToArray();
                Assert.Equal(expectedCount, faces.Length);
                transparentUploadCounts[renderDataId] = expectedCount;
                Assert.All(faces, AssertCompleteTransparentFace);
                Assert.All(faces, face =>
                {
                    if (face.GetProperty("blockId").GetUInt16() == 11 &&
                        face.GetProperty("faceDirection").GetByte() == 3)
                        Assert.Equal(551, face.GetProperty("voxelWorld")[1].GetInt32());
                });
            }

            var activeUploads = new HashSet<long>();
            bool transparentDrawObserved = false;
            foreach (JsonElement streamEvent in events)
            {
                string type = streamEvent.GetProperty("type").GetString()!;
                if (string.Equals(type, "simulatedGpuUpload", StringComparison.Ordinal))
                {
                    activeUploads.Add(streamEvent.GetProperty("renderDataId").GetInt64());
                }
                else if (string.Equals(type, "simulatedGpuDeletion", StringComparison.Ordinal))
                {
                    activeUploads.Remove(streamEvent.GetProperty("renderDataId").GetInt64());
                }
                else if (string.Equals(type, "renderFrame", StringComparison.Ordinal))
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
                    .Any(face => face.GetProperty("blockId").GetUInt16() == 11 &&
                        face.GetProperty("faceDirection").GetByte() == 3 &&
                        face.GetProperty("voxelWorld")[1].GetInt32() == 551));
            Console.WriteLine($"Transparent render result: {outputPath}");

        }
    }
}

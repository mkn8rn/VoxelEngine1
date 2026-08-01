using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MVoxelEngine1.Tests
{
    public class SimulatedGpuUploadEndToEndTests
    {
        [Fact(Timeout = 90_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "CPU")]
        public async Task Seed123456StreamsRenderDataDuringTimedMovementWithoutWindowAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            ConfigureSmallWorld(workspace.GameDataRoot);
            string application = TestPaths.ApplicationExecutable;
            Assert.True(File.Exists(application), $"Application executable was not found at {application}.");

            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "simulated-gpu-uploads");
            Directory.CreateDirectory(resultsDirectory);
            string outputPath = Path.Combine(
                resultsDirectory,
                $"default-seed-123456-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            var startInfo = new ProcessStartInfo
            {
                FileName = application,
                WorkingDirectory = Path.GetDirectoryName(application)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            AddArgument(startInfo, "gameDataDirectory", workspace.GameDataRoot);
            AddArgument(startInfo, "game", "Default");
            AddArgument(startInfo, "worldName", "SimulatedUploadWorld");
            AddArgument(startInfo, "seed", "123456");
            AddArgument(startInfo, "renderStreamingIfAllowed", "false");
            AddArgument(startInfo, "windowWidth", "320");
            AddArgument(startInfo, "windowHeight", "240");
            AddArgument(startInfo, "simulatedGpuUploadOutput", outputPath);
            AddArgument(startInfo, "simulatedInput", "W:2,Space:3");
            AddArgument(startInfo, "simulatedFrameRate", "60");

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "Application process did not start.");
            CancellationToken testCancellation = TestContext.Current.CancellationToken;
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(testCancellation);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(testCancellation);
            bool windowObserved = false;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                testCancellation);
            try
            {
                while (!process.HasExited)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        process.Refresh();
                        windowObserved |= process.MainWindowHandle != IntPtr.Zero;
                    }

                    await Task.Delay(20, combinedCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(testCancellation);
                string timeoutOutput = await standardOutputTask;
                string timeoutError = await standardErrorTask;
                throw new TimeoutException($"Simulated GPU upload exceeded 75 seconds. Output: {Tail(timeoutOutput)} Error: {Tail(timeoutError)}");
            }

            string standardOutput = await standardOutputTask;
            string standardError = await standardErrorTask;
            Assert.True(
                process.ExitCode == 0,
                $"Application exited with code {process.ExitCode}. Output: {Tail(standardOutput)} Error: {Tail(standardError)}");
            Assert.False(windowObserved);
            Assert.Contains("started without an OpenTK window", standardOutput);
            Assert.True(File.Exists(outputPath), $"Simulated GPU output was not written to {outputPath}.");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputPath));
            JsonElement root = document.RootElement;
            Assert.Equal("simulatedGpuUpload", root.GetProperty("mode").GetString());
            Assert.Equal(123456, root.GetProperty("seed").GetInt32());
            Assert.False(root.GetProperty("windowCreated").GetBoolean());
            Assert.False(root.GetProperty("openGlCallsAllowed").GetBoolean());
            Assert.Equal(0, root.GetProperty("actualGpuUploadCount").GetInt32());

            JsonElement[] events = root.GetProperty("events").EnumerateArray().ToArray();
            JsonElement[] snapshots = events
                .Where(element => element.GetProperty("type").GetString() == "snapshot")
                .ToArray();
            Assert.Equal(2, snapshots.Length);
            Assert.Equal("initial", snapshots[0].GetProperty("name").GetString());
            Assert.Equal("final", snapshots[1].GetProperty("name").GetString());
            AssertVector(snapshots[0].GetProperty("camera").GetProperty("position"), 0, 0, 0);
            AssertVector(snapshots[1].GetProperty("camera").GetProperty("position"), 0, 180, -120, 0.2);
            Assert.Equal(11, snapshots[1].GetProperty("playerChunk").GetProperty("y").GetInt32());
            Assert.Equal(-8, snapshots[1].GetProperty("playerChunk").GetProperty("z").GetInt32());

            JsonElement[] renderFrames = events
                .Where(element => element.GetProperty("type").GetString() == "renderFrame")
                .ToArray();
            Assert.InRange(renderFrames.Length, 250, 301);
            Assert.All(
                renderFrames,
                frame => Assert.InRange(frame.GetProperty("deltaSeconds").GetDouble(), 0, 0.1));
            Assert.Contains(renderFrames, frame => HasInputKey(frame, "W"));
            Assert.Contains(renderFrames, frame => HasInputKey(frame, "Space"));
            Assert.All(renderFrames, frame => Assert.Equal(0, frame.GetProperty("actualGpuUploadsThisFrame").GetInt32()));

            JsonElement[] uploads = events
                .Where(element => element.GetProperty("type").GetString() == "simulatedGpuUpload")
                .ToArray();
            Assert.NotEmpty(uploads);
            Assert.Contains(uploads, upload => upload.GetProperty("frameIndex").GetInt64() > 0);
            Assert.All(uploads, upload => Assert.False(upload.GetProperty("actualGpuUploadPerformed").GetBoolean()));
            JsonElement face = FindFirstFace(uploads);
            Assert.Equal(3, face.GetProperty("offset").GetArrayLength());
            Assert.Equal(3, face.GetProperty("voxelWorld").GetArrayLength());
            Assert.InRange(face.GetProperty("faceDirection").GetByte(), (byte)0, (byte)5);
            Assert.True(face.TryGetProperty("tileIndex", out _));
            Assert.True(face.TryGetProperty("blockId", out _));
            Assert.True(face.TryGetProperty("neighborBlockIdAtUpload", out _));

            foreach (JsonElement snapshot in snapshots)
            {
                foreach (JsonElement chunk in snapshot.GetProperty("activeChunks").EnumerateArray())
                    Assert.False(chunk.GetProperty("openGlUploaded").GetBoolean());
            }

            JsonElement summary = root.GetProperty("summary");
            Assert.Equal(2, summary.GetProperty("snapshotCount").GetInt32());
            Assert.Equal((long)renderFrames.Length, summary.GetProperty("renderFrameCount").GetInt64());
            Assert.True(summary.GetProperty("simulatedGpuUploadCount").GetInt64() > 0);
            Assert.True(summary.GetProperty("simulatedGpuDeletionCount").GetInt64() > 0);
            Assert.Equal(0, summary.GetProperty("actualGpuUploadCount").GetInt32());

            string worldsDirectory = Path.Combine(workspace.GameDataRoot, "Default", "Saves", "Worlds");
            string worldFile = Assert.Single(Directory.GetFiles(worldsDirectory, "world.txt", SearchOption.AllDirectories));
            Assert.Equal("123456", File.ReadAllLines(worldFile)[3]);
            Console.WriteLine($"Simulated GPU upload result: {outputPath}");
        }

        private static void ConfigureSmallWorld(string gameDataRoot)
        {
            string defaultsPath = Path.Combine(gameDataRoot, "Default", "Defaults.txt");
            JsonObject defaults = JsonNode.Parse(File.ReadAllText(defaultsPath))!.AsObject();
            defaults["chunkMaxX"] = 16;
            defaults["chunkMaxY"] = 16;
            defaults["chunkMaxZ"] = 16;
            defaults["maxWorldHeight"] = 160;
            defaults["lod1RenderDistance"] = 1;
            defaults["lod2RenderDistance"] = 1;
            defaults["lod3RenderDistance"] = 1;
            defaults["lod4RenderDistance"] = 1;
            defaults["lod5RenderDistance"] = 4;
            defaults["regionWidthInChunks"] = 16;
            defaults["chunkGenerationBufferInitial"] = 1;
            defaults["chunkGenerationBufferRuntime"] = 1;
            File.WriteAllText(defaultsPath, defaults.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
        {
            startInfo.ArgumentList.Add($"--{name}");
            startInfo.ArgumentList.Add(value);
        }

        private static JsonElement FindFirstFace(IEnumerable<JsonElement> uploads)
        {
            foreach (JsonElement upload in uploads)
            {
                foreach (string propertyName in new[] { "opaqueFaces", "transparentFaces" })
                {
                    JsonElement.ArrayEnumerator faces = upload.GetProperty(propertyName).EnumerateArray();
                    if (faces.MoveNext())
                        return faces.Current;
                }
            }

            throw new InvalidDataException("No uploaded face was recorded.");
        }

        private static bool HasInputKey(JsonElement frame, string key)
        {
            return frame.GetProperty("inputKeys")
                .EnumerateArray()
                .Any(element => element.GetString() == key);
        }

        private static void AssertVector(
            JsonElement vector,
            double x,
            double y,
            double z,
            double tolerance = 0)
        {
            double[] values = vector.EnumerateArray().Select(element => element.GetDouble()).ToArray();
            Assert.Equal(3, values.Length);
            Assert.InRange(values[0], x - tolerance, x + tolerance);
            Assert.InRange(values[1], y - tolerance, y + tolerance);
            Assert.InRange(values[2], z - tolerance, z + tolerance);
        }

        private static string Tail(string value)
        {
            const int maximumLength = 8_000;
            return value.Length <= maximumLength ? value : value[^maximumLength..];
        }
    }
}

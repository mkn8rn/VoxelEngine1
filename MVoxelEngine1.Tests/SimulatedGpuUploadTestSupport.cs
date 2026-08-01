using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MVoxelEngine1.Tests
{
    internal sealed record SimulatedGpuProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool WindowObserved,
        long PeakWorkingSetBytes);

    internal static class SimulatedGpuUploadTestSupport
    {
        public static void ConfigureSmallWorld(
            string gameDataRoot,
            int maximumWorldHeight = 160,
            int lod1RenderDistance = 1)
        {
            string defaultsPath = Path.Combine(gameDataRoot, "Default", "Defaults.txt");
            JsonObject defaults = JsonNode.Parse(File.ReadAllText(defaultsPath))!.AsObject();
            defaults["chunkMaxX"] = 16;
            defaults["chunkMaxY"] = 16;
            defaults["chunkMaxZ"] = 16;
            defaults["maxWorldHeight"] = maximumWorldHeight;
            defaults["lod1RenderDistance"] = lod1RenderDistance;
            defaults["lod2RenderDistance"] = 1;
            defaults["lod3RenderDistance"] = 1;
            defaults["lod4RenderDistance"] = 1;
            defaults["lod5RenderDistance"] = 4;
            defaults["regionWidthInChunks"] = Math.Max(
                16,
                (maximumWorldHeight + 15) / 16);
            defaults["chunkGenerationBufferInitial"] = lod1RenderDistance;
            defaults["chunkGenerationBufferRuntime"] = lod1RenderDistance;
            File.WriteAllText(
                defaultsPath,
                defaults.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public static void SetWaterLevel(string gameDataRoot, int waterLevel)
        {
            string biomePath = Path.Combine(
                gameDataRoot,
                "Default",
                "Data",
                "Biomes",
                "Fallowlands",
                "Defaults.txt");
            JsonObject biome = JsonNode.Parse(File.ReadAllText(biomePath))!.AsObject();
            biome["water_level"] = waterLevel;
            File.WriteAllText(
                biomePath,
                biome.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public static ProcessStartInfo CreateStartInfo(
            TestWorkspace workspace,
            string outputPath,
            string worldName,
            string inputScript,
            int frameRate,
            int writerDelayMilliseconds = 0,
            int? writerFailAfterRecords = null)
        {
            string application = TestPaths.ApplicationExecutable;
            Assert.True(File.Exists(application), $"Application executable was not found at {application}.");

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
            AddArgument(startInfo, "worldName", worldName);
            AddArgument(startInfo, "seed", "123456");
            AddArgument(startInfo, "renderStreamingIfAllowed", "false");
            AddArgument(startInfo, "windowWidth", "320");
            AddArgument(startInfo, "windowHeight", "240");
            AddArgument(startInfo, "simulatedGpuUploadOutput", outputPath);
            AddArgument(startInfo, "simulatedInput", inputScript);
            AddArgument(startInfo, "simulatedFrameRate", frameRate.ToString());
            if (writerDelayMilliseconds > 0)
            {
                AddArgument(
                    startInfo,
                    "simulatedGpuWriterDelayMilliseconds",
                    writerDelayMilliseconds.ToString());
            }

            if (writerFailAfterRecords.HasValue)
            {
                AddArgument(
                    startInfo,
                    "simulatedGpuWriterFailAfterRecords",
                    writerFailAfterRecords.Value.ToString());
            }

            return startInfo;
        }

        public static async Task<SimulatedGpuProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken testCancellation)
        {
            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "Application process did not start.");
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(testCancellation);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(testCancellation);
            bool windowObserved = false;
            long peakWorkingSetBytes = 0;

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutSource.Token,
                testCancellation);
            try
            {
                while (!process.HasExited)
                {
                    process.Refresh();
                    peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
                    if (OperatingSystem.IsWindows())
                        windowObserved |= process.MainWindowHandle != IntPtr.Zero;

                    await Task.Delay(10, combinedCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(testCancellation);
                string timeoutOutput = await standardOutputTask;
                string timeoutError = await standardErrorTask;
                throw new TimeoutException(
                    $"Simulated GPU upload exceeded {timeout.TotalSeconds:0} seconds. " +
                    $"Output: {Tail(timeoutOutput)} Error: {Tail(timeoutError)}");
            }

            try
            {
                process.Refresh();
                peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.PeakWorkingSet64);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
            return new SimulatedGpuProcessResult(
                process.ExitCode,
                await standardOutputTask,
                await standardErrorTask,
                windowObserved,
                peakWorkingSetBytes);
        }

        public static string[] FindIncompleteFiles(string outputPath)
        {
            string directory = Path.GetDirectoryName(outputPath)!;
            string pattern = $".{Path.GetFileName(outputPath)}.*.incomplete";
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }

        public static void AssertCompleteOrderedStream(JsonElement root)
        {
            JsonElement[] events = root.GetProperty("events").EnumerateArray().ToArray();
            for (int index = 0; index < events.Length; index++)
                Assert.Equal(index, events[index].GetProperty("sequence").GetInt64());

            JsonElement summary = root.GetProperty("summary");
            Assert.Equal(events.Length, summary.GetProperty("completionSequence").GetInt64());
            Assert.Equal(events.Length + 1, summary.GetProperty("streamRecordCount").GetInt64());
            Assert.False(summary.GetProperty("silentRecordLossAllowed").GetBoolean());
        }

        public static string Tail(string value)
        {
            const int MaximumLength = 8_000;
            return value.Length <= MaximumLength ? value : value[^MaximumLength..];
        }

        private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
        {
            startInfo.ArgumentList.Add($"--{name}");
            startInfo.ArgumentList.Add(value);
        }
    }
}

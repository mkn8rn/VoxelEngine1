using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MVoxelEngine1.Infrastructure.Diagnostics;

namespace MVoxelEngine1.Tests
{
    public class DefaultGameEndToEndTests
    {
        [Fact(Timeout = 150_000)]
        [Trait("Category", "EndToEnd")]
        [Trait("Resource", "GPU")]
        public async Task DefaultGameSeed123456RecordsStartupPerformanceAsync()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string application = TestPaths.ApplicationExecutable;
            Assert.True(File.Exists(application), $"Application executable was not found at {application}.");

            string resultsDirectory = Path.Combine(TestPaths.RepositoryRoot, "TestResults", "benchmarks");
            Directory.CreateDirectory(resultsDirectory);
            string resultPath = Path.Combine(
                resultsDirectory,
                $"default-seed-123456-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");

            var startInfo = new ProcessStartInfo
            {
                FileName = application,
                WorkingDirectory = Path.GetDirectoryName(application)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            };
            startInfo.ArgumentList.Add("--gameDataDirectory");
            startInfo.ArgumentList.Add(workspace.GameDataRoot);
            startInfo.ArgumentList.Add("--game");
            startInfo.ArgumentList.Add("Default");
            startInfo.ArgumentList.Add("--worldName");
            startInfo.ArgumentList.Add("BenchmarkWorld");
            startInfo.ArgumentList.Add("--seed");
            startInfo.ArgumentList.Add("123456");
            startInfo.ArgumentList.Add("--renderStreamingIfAllowed");
            startInfo.ArgumentList.Add("false");
            startInfo.ArgumentList.Add("--windowWidth");
            startInfo.ArgumentList.Add("320");
            startInfo.ArgumentList.Add("--windowHeight");
            startInfo.ArgumentList.Add("240");
            startInfo.ArgumentList.Add("--benchmarkOutput");
            startInfo.ArgumentList.Add(resultPath);

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "Application process did not start.");
            CancellationToken testCancellation = TestContext.Current.CancellationToken;
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(testCancellation);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(testCancellation);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                testCancellation);
            try
            {
                await process.WaitForExitAsync(combinedCancellation.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(testCancellation);
                string timeoutOutput = await standardOutputTask;
                string timeoutError = await standardErrorTask;
                throw new TimeoutException($"Application benchmark exceeded 120 seconds. Output: {Tail(timeoutOutput)} Error: {Tail(timeoutError)}");
            }

            string standardOutput = await standardOutputTask;
            string standardError = await standardErrorTask;
            Assert.True(
                process.ExitCode == 0,
                $"Application exited with code {process.ExitCode}. Output: {Tail(standardOutput)} Error: {Tail(standardError)}");
            Assert.True(File.Exists(resultPath), $"Benchmark result was not written to {resultPath}.");

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            StartupPerformanceSnapshot? result = JsonSerializer.Deserialize<StartupPerformanceSnapshot>(
                File.ReadAllText(resultPath),
                jsonOptions);
            Assert.NotNull(result);
            Assert.Equal("Default", result.Game);
            Assert.Equal(123456, result.Seed);
            AssertPositiveFinite(result.GameLoadMilliseconds, nameof(result.GameLoadMilliseconds));
            Assert.True(result.InitialGenerationMilliseconds > 0);
            Assert.True(result.InitialChunkMeshBuildMilliseconds > 0);
            AssertPositiveFinite(result.BuildMilliseconds, nameof(result.BuildMilliseconds));
            AssertPositiveFinite(result.RenderMilliseconds, nameof(result.RenderMilliseconds));
            AssertPositiveFinite(result.CameraAppearanceMilliseconds, nameof(result.CameraAppearanceMilliseconds));
            AssertPositiveFinite(result.GpuStreamingStartMilliseconds, nameof(result.GpuStreamingStartMilliseconds));
            Assert.Equal(
                ReadConsoleTiming(standardOutput, "[World] Initial generation complete in "),
                result.InitialGenerationMilliseconds);
            Assert.Equal(
                ReadConsoleTiming(standardOutput, "[World] Chunk mesh build complete in "),
                result.InitialChunkMeshBuildMilliseconds);

            string worldsDirectory = Path.Combine(workspace.GameDataRoot, "Default", "Saves", "Worlds");
            string worldFile = Assert.Single(Directory.GetFiles(worldsDirectory, "world.txt", SearchOption.AllDirectories));
            Assert.Equal("123456", File.ReadAllLines(worldFile)[3]);

            Console.WriteLine($"Benchmark result: {resultPath}");
        }

        private static void AssertPositiveFinite(double value, string metricName)
        {
            Assert.True(double.IsFinite(value), $"{metricName} must be finite.");
            Assert.True(value > 0, $"{metricName} must be greater than zero.");
        }

        private static long ReadConsoleTiming(string output, string prefix)
        {
            Match match = Regex.Match(
                output,
                $"{Regex.Escape(prefix)}(?<milliseconds>[0-9]+) ms\\.");
            Assert.True(match.Success, $"Console timing was not found for '{prefix}'. Output: {Tail(output)}");
            return long.Parse(match.Groups["milliseconds"].Value);
        }

        private static string Tail(string value)
        {
            const int maximumLength = 8_000;
            return value.Length <= maximumLength ? value : value[^maximumLength..];
        }
    }
}

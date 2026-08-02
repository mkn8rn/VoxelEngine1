using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MVoxelEngine1.Infrastructure.Diagnostics;

namespace MVoxelEngine1.Tests
{
    public class DefaultGameEndToEndTests
    {
        [Fact(Explicit = true, Timeout = 150_000)]
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
            Assert.Equal(
                "9FC9BC59776B239177FDA25241AB0CDB350328283AC72785A66853B84DB5A562",
                result.GameInputSha256);
            Assert.Equal(
                "CC5C57FE8EE451A52B36CA85081E2EEBD436BD751B1EDA2C8025CEF71211A414",
                result.BlockRegistrySha256);
            Assert.Equal(2_000, result.TargetGenerationToRenderMilliseconds);
            Assert.Equal(
                16L * 1024 * 1024 * 1024,
                result.MaximumWorkingSetBytesLimit);
            AssertBenchmarkParameters(result.Parameters);
            AssertPositiveFinite(result.GameLoadMilliseconds, nameof(result.GameLoadMilliseconds));
            AssertPositiveFinite(
                result.SeedAcceptedMilliseconds,
                nameof(result.SeedAcceptedMilliseconds));
            AssertPositiveFinite(
                result.InitialGenerationStartMilliseconds,
                nameof(result.InitialGenerationStartMilliseconds));
            Assert.True(result.InitialGenerationMilliseconds > 0);
            AssertPositiveFinite(
                result.InitialGenerationCompleteMilliseconds,
                nameof(result.InitialGenerationCompleteMilliseconds));
            AssertPositiveFinite(
                result.InitialChunkMeshBuildStartMilliseconds,
                nameof(result.InitialChunkMeshBuildStartMilliseconds));
            Assert.True(result.InitialChunkMeshBuildMilliseconds > 0);
            AssertPositiveFinite(
                result.InitialChunkMeshBuildCompleteMilliseconds,
                nameof(result.InitialChunkMeshBuildCompleteMilliseconds));
            AssertPositiveFinite(result.BuildMilliseconds, nameof(result.BuildMilliseconds));
            AssertPositiveFinite(result.RenderMilliseconds, nameof(result.RenderMilliseconds));
            AssertPositiveFinite(result.CameraAppearanceMilliseconds, nameof(result.CameraAppearanceMilliseconds));
            AssertPositiveFinite(result.GpuStreamingStartMilliseconds, nameof(result.GpuStreamingStartMilliseconds));
            AssertPositiveFinite(
                result.GenerationToRenderMilliseconds,
                nameof(result.GenerationToRenderMilliseconds));
            AssertPositiveFinite(
                result.GenerationToRenderCompleteMilliseconds,
                nameof(result.GenerationToRenderCompleteMilliseconds));
            Assert.InRange(result.WorkingSetBytes, 1, 16L * 1024 * 1024 * 1024);
            Assert.InRange(result.PeakWorkingSetBytes, 1, 16L * 1024 * 1024 * 1024);
            Assert.True(result.PeakWorkingSetBytes >= result.WorkingSetBytes);
            Assert.True(result.ManagedHeapBytes > 0);
            Assert.True(result.TotalAllocatedBytes >= result.ManagedHeapBytes);
            AssertPositiveFinite(
                result.ProcessorTimeMilliseconds,
                nameof(result.ProcessorTimeMilliseconds));
            AssertGenerationDiagnostics(result.GenerationDiagnostics);
            AssertMeshDiagnostics(result.MeshDiagnostics);
            Assert.True(
                result.SeedAcceptedMilliseconds >= result.GameLoadMilliseconds);
            Assert.True(
                result.InitialGenerationStartMilliseconds >=
                result.SeedAcceptedMilliseconds);
            Assert.True(
                result.InitialGenerationCompleteMilliseconds >=
                result.InitialGenerationStartMilliseconds +
                result.InitialGenerationMilliseconds);
            Assert.True(
                result.InitialChunkMeshBuildStartMilliseconds >=
                result.InitialGenerationCompleteMilliseconds);
            Assert.True(
                result.InitialChunkMeshBuildCompleteMilliseconds >=
                result.InitialChunkMeshBuildStartMilliseconds +
                result.InitialChunkMeshBuildMilliseconds);
            Assert.True(
                result.GpuStreamingStartMilliseconds >=
                result.InitialChunkMeshBuildCompleteMilliseconds);
            Assert.True(
                result.GpuStreamingStartMilliseconds >=
                result.SeedAcceptedMilliseconds);
            Assert.True(
                result.GenerationToRenderCompleteMilliseconds >=
                result.GpuStreamingStartMilliseconds);
            Assert.True(
                result.CameraAppearanceMilliseconds >=
                result.GenerationToRenderCompleteMilliseconds);
            Assert.InRange(
                Math.Abs(
                    result.GenerationToRenderCompleteMilliseconds -
                    result.SeedAcceptedMilliseconds -
                    result.GenerationToRenderMilliseconds),
                0,
                0.0001);
            Assert.Equal(
                ReadConsoleTiming(standardOutput, "[World] Initial generation complete in "),
                result.InitialGenerationMilliseconds);
            Assert.Equal(
                ReadConsoleTiming(standardOutput, "[World] Chunk mesh build complete in "),
                result.InitialChunkMeshBuildMilliseconds);
            Assert.Equal(
                ReadConsoleDoubleTiming(
                    standardOutput,
                    "Generation to Render time (GTRT): "),
                result.GenerationToRenderMilliseconds);

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

        private static void AssertBenchmarkParameters(
            StartupBenchmarkParameters parameters)
        {
            Assert.Equal(160, parameters.ChunkSizeX);
            Assert.Equal(160, parameters.ChunkSizeY);
            Assert.Equal(160, parameters.ChunkSizeZ);
            Assert.Equal(12, parameters.Lod1Radius);
            Assert.Equal(18, parameters.Lod2Radius);
            Assert.Equal(36, parameters.Lod3Radius);
            Assert.Equal(54, parameters.Lod4Radius);
            Assert.Equal(486, parameters.Lod5Radius);
            Assert.Equal(12, parameters.InitialGenerationBuffer);
            Assert.Equal(12, parameters.RuntimeGenerationBuffer);
            Assert.Equal(32, parameters.BlockTileWidth);
            Assert.Equal(32, parameters.BlockTileHeight);
            Assert.False(parameters.RenderStreamingAllowed);
            Assert.False(parameters.RenderStreamingEnabled);
            Assert.Equal("Optimized", parameters.FaceGenerationMode);
            Assert.Equal(0.5f, parameters.WorldGenerationWorkersPerCore);
            Assert.Equal(2f, parameters.InitialWorldGenerationWorkersPerCore);
            Assert.Equal(1f, parameters.MeshBuildWorkersPerCore);
            Assert.Equal(2f, parameters.InitialMeshBuildWorkersPerCore);
            Assert.Equal(320, parameters.WindowWidth);
            Assert.Equal(240, parameters.WindowHeight);
            Assert.Equal(Environment.ProcessorCount, parameters.LogicalProcessorCount);
            Assert.False(parameters.ServerGarbageCollection);
            Assert.Equal("Interactive", parameters.GarbageCollectionLatencyMode);
        }

        private static void AssertGenerationDiagnostics(
            GenerationPerformanceSnapshot diagnostics)
        {
            Assert.True(diagnostics.Columns > 0);
            Assert.True(diagnostics.Chunks > 0);
            Assert.True(diagnostics.AllAirChunks > 0);
            Assert.True(diagnostics.AllStoneChunks > 0);
            Assert.True(diagnostics.NonUniformChunks > 0);
            Assert.Equal(
                diagnostics.Chunks,
                diagnostics.AllAirChunks +
                diagnostics.AllStoneChunks +
                diagnostics.AllSoilChunks +
                diagnostics.AllWaterChunks +
                diagnostics.NonUniformChunks);
            AssertNonNegativeFinite(
                diagnostics.AggregatedProfileMilliseconds,
                nameof(diagnostics.AggregatedProfileMilliseconds));
            AssertPositiveFinite(
                diagnostics.VerticalClassificationMilliseconds,
                nameof(diagnostics.VerticalClassificationMilliseconds));
            AssertPositiveFinite(
                diagnostics.SpanMapMilliseconds,
                nameof(diagnostics.SpanMapMilliseconds));
            AssertPositiveFinite(
                diagnostics.ChunkConstructionMilliseconds,
                nameof(diagnostics.ChunkConstructionMilliseconds));
            AssertPositiveFinite(
                diagnostics.UniformSectionMilliseconds,
                nameof(diagnostics.UniformSectionMilliseconds));
            AssertPositiveFinite(
                diagnostics.NonUniformGenerationMilliseconds,
                nameof(diagnostics.NonUniformGenerationMilliseconds));
            Assert.Equal(0, diagnostics.NonUniformColumnScanMilliseconds);
            Assert.Equal(0, diagnostics.NonUniformUniformSectionMilliseconds);
            Assert.Equal(0, diagnostics.NonUniformTerrainEmissionMilliseconds);
            Assert.Equal(0, diagnostics.NonUniformWaterEmissionMilliseconds);
            Assert.Equal(0, diagnostics.NonUniformCollapseMilliseconds);
            Assert.Equal(0, diagnostics.NonUniformFinalizeMilliseconds);
            Assert.Equal(0, diagnostics.FinalizedSections);
            Assert.Equal(0, diagnostics.ScratchSections);
            Assert.Equal(0, diagnostics.EscalatedScratchSections);
            Assert.Equal(0, diagnostics.EmptySections);
            Assert.Equal(0, diagnostics.PackedSections);
            Assert.Equal(0, diagnostics.MultiPackedSections);
            Assert.Equal(0, diagnostics.ExpandedSections);
            AssertPositiveFinite(
                diagnostics.BoundaryPlaneMilliseconds,
                nameof(diagnostics.BoundaryPlaneMilliseconds));
            AssertPositiveFinite(
                diagnostics.RegistrarMilliseconds,
                nameof(diagnostics.RegistrarMilliseconds));
        }

        private static void AssertMeshDiagnostics(
            MeshPerformanceSnapshot diagnostics)
        {
            Assert.True(diagnostics.BuiltChunks > 0);
            Assert.True(diagnostics.GeneratedSpanChunks > 0);
            Assert.True(diagnostics.SectionChunks > 0);
            Assert.Equal(
                diagnostics.BuiltChunks,
                diagnostics.GeneratedSpanChunks + diagnostics.SectionChunks);
            Assert.True(diagnostics.GeneratedSpanOpaqueFaces > 0);
            Assert.True(diagnostics.GeneratedSpanTransparentFaces > 0);
            Assert.InRange(
                diagnostics.GeneratedSpanOpaqueRectangles,
                1,
                diagnostics.GeneratedSpanOpaqueFaces);
            Assert.InRange(
                diagnostics.GeneratedSpanTransparentRectangles,
                1,
                diagnostics.GeneratedSpanTransparentFaces);
            AssertPositiveFinite(
                diagnostics.AggregatedBuildMilliseconds,
                nameof(diagnostics.AggregatedBuildMilliseconds));
            AssertPositiveFinite(
                diagnostics.GeneratedSpanBuildMilliseconds,
                nameof(diagnostics.GeneratedSpanBuildMilliseconds));
            AssertPositiveFinite(
                diagnostics.SectionBuildMilliseconds,
                nameof(diagnostics.SectionBuildMilliseconds));
            AssertNonNegativeFinite(
                diagnostics.GeneratedSpanCountPassMilliseconds,
                nameof(diagnostics.GeneratedSpanCountPassMilliseconds));
            AssertPositiveFinite(
                diagnostics.GeneratedSpanPreparationMilliseconds,
                nameof(diagnostics.GeneratedSpanPreparationMilliseconds));
            AssertPositiveFinite(
                diagnostics.GeneratedSpanWritePassMilliseconds,
                nameof(diagnostics.GeneratedSpanWritePassMilliseconds));
        }

        private static void AssertNonNegativeFinite(double value, string metricName)
        {
            Assert.True(double.IsFinite(value), $"{metricName} must be finite.");
            Assert.True(value >= 0, $"{metricName} must not be negative.");
        }

        private static long ReadConsoleTiming(string output, string prefix)
        {
            Match match = Regex.Match(
                output,
                $"{Regex.Escape(prefix)}(?<milliseconds>[0-9]+) ms\\.");
            Assert.True(match.Success, $"Console timing was not found for '{prefix}'. Output: {Tail(output)}");
            return long.Parse(match.Groups["milliseconds"].Value);
        }

        private static double ReadConsoleDoubleTiming(string output, string prefix)
        {
            Match match = Regex.Match(
                output,
                $"{Regex.Escape(prefix)}(?<milliseconds>[0-9]+(?:\\.[0-9]+)?) ms\\.");
            Assert.True(match.Success, $"Console timing was not found for '{prefix}'. Output: {Tail(output)}");
            return double.Parse(
                match.Groups["milliseconds"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Tail(string value)
        {
            const int maximumLength = 8_000;
            return value.Length <= maximumLength ? value : value[^maximumLength..];
        }
    }
}

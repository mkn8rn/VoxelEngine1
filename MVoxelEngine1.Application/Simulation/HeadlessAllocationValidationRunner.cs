using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Application.Simulation;

internal static class HeadlessAllocationValidationRunner
{
    internal static void Run(string outputPath)
    {
        if (StartupPerformanceRecorder.IsRunning)
            throw new InvalidOperationException("Allocation validation cannot run with a performance recorder.");
        GameDataStartup.Load();
        var textureAtlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        ChunkRender.terrainTextureAtlas = textureAtlas;
        StartupBenchmarkParameters parameters = StartupPerformanceRecorder.CaptureParameters();
        string gameInputs = RuntimeInputHasher.HashGameInputs();
        string registry = RuntimeInputHasher.HashBlockRegistry();
        using var monitor = new NativeGtrtAllocationMonitor();
        using NativeWorld world = NativeWorld.CreateHeadless(textureAtlas, monitor);
        NativeGtrtAllocationEvidence evidence = monitor.Capture();
        bool passed = evidence.CoordinatorManagedBytes == 0 && evidence.ProcessManagedBytes == 0 &&
            evidence.NoGcRegionCompleted && evidence.Workers.All(static worker => worker.TotalBytes == 0) &&
            StartupPerformanceRecorder.WindowConstructionCount == 0 && StartupPerformanceRecorder.ActualGpuUploadCount == 0;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        var report = new
        {
            schemaVersion = 1,
            mode = "headless-native-allocation-validation",
            passed,
            performanceRecorderRunning = StartupPerformanceRecorder.IsRunning,
            windowConstructionCount = StartupPerformanceRecorder.WindowConstructionCount,
            actualGpuUploadCount = StartupPerformanceRecorder.ActualGpuUploadCount,
            game = FlagManager.flags.game,
            seed = FlagManager.flags.seed,
            gameInputSha256 = gameInputs,
            blockRegistrySha256 = registry,
            parameters,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            nativeAllocationManagement = typeof(Supprocom.NativeAllocationManagement.NativeTransfer<>).Assembly.GetName().Version?.ToString(),
            evidence,
            workingSetBytes = process.WorkingSet64,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            recordedAtUtc = DateTimeOffset.UtcNow
        };
        WriteAtomic(outputPath, report);
        Console.WriteLine($"Native allocation gate {(passed ? "passed" : "failed")}: {Path.GetFullPath(outputPath)}");
        if (!passed)
            throw new InvalidOperationException("The native allocation interval did not meet the exact zero-byte gate.");
    }

    private static void WriteAtomic<T>(string outputPath, T report)
    {
        string finalPath = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("The allocation report directory is invalid.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.incomplete");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, report, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

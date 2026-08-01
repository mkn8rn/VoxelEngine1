using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Flags;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.Application.Simulation;
using System;
using System.Runtime;

namespace MVoxelEngine1.Application
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Environment.SetEnvironmentVariable("APP_ENVIRONMENT", "Development");
            ConsoleFlags.Parse(args);
            EnvironmentFlags.LoadEnvironmentFlags();
            FlagManager.ApplyFlags(args);

            if (!string.IsNullOrWhiteSpace(FlagManager.flags.faceManifestOutput))
            {
                ValidateFaceManifestFlags();
                FaceManifestRunner.Run(FlagManager.flags.faceManifestOutput);
                return;
            }

            if (!string.IsNullOrWhiteSpace(FlagManager.flags.simulatedGpuUploadOutput))
            {
                ValidateSimulatedGpuUploadFlags();
                string inputScript = string.IsNullOrWhiteSpace(FlagManager.flags.simulatedInput)
                    ? TimedPlayerInputScript.DefaultScript
                    : FlagManager.flags.simulatedInput;
                IReadOnlyList<TimedPlayerInputStep> steps = TimedPlayerInputScript.Parse(inputScript);
                int frameRate = FlagManager.flags.simulatedFrameRate ?? 60;
                if (frameRate <= 0 || frameRate > 1000)
                    throw new InvalidOperationException("The simulated frame rate must be from 1 through 1000.");
                int writerDelayMilliseconds = FlagManager.flags.simulatedGpuWriterDelayMilliseconds ?? 0;
                if (writerDelayMilliseconds < 0 || writerDelayMilliseconds > 1000)
                    throw new InvalidOperationException("The simulated GPU writer delay must be from 0 through 1000 milliseconds.");
                int? writerFailAfterRecords = FlagManager.flags.simulatedGpuWriterFailAfterRecords;
                if (writerFailAfterRecords is <= 0)
                    throw new InvalidOperationException("The simulated GPU writer failure record count must be positive.");

                await SimulatedGpuUploadRunner.RunAsync(
                    FlagManager.flags.simulatedGpuUploadOutput,
                    inputScript,
                    steps,
                    frameRate,
                    writerDelayMilliseconds,
                    writerFailAfterRecords);
                return;
            }

            if (!string.IsNullOrWhiteSpace(FlagManager.flags.benchmarkOutput))
            {
                if (string.IsNullOrWhiteSpace(FlagManager.flags.game))
                    throw new InvalidOperationException("Benchmark game is not set.");
                if (!FlagManager.flags.seed.HasValue)
                    throw new InvalidOperationException("Benchmark seed is not set.");
                if (FlagManager.flags.renderStreamingIfAllowed is not false)
                    throw new InvalidOperationException("Benchmark mode requires renderStreamingIfAllowed=false.");

                StartupPerformanceRecorder.Begin(FlagManager.flags.game, FlagManager.flags.seed.Value);
            }

            using (Window game = new Window())
            {
                game.Run();
            }
        }

        private static void ValidateSimulatedGpuUploadFlags()
        {
            if (!string.IsNullOrWhiteSpace(FlagManager.flags.benchmarkOutput))
                throw new InvalidOperationException("Benchmark mode and simulated GPU upload mode cannot run together.");
            if (string.IsNullOrWhiteSpace(FlagManager.flags.game))
                throw new InvalidOperationException("The simulated GPU upload game is not set.");
            if (string.IsNullOrWhiteSpace(FlagManager.flags.worldName))
                throw new InvalidOperationException("The simulated GPU upload world name is not set.");
            if (!FlagManager.flags.seed.HasValue)
                throw new InvalidOperationException("The simulated GPU upload seed is not set.");
            if (FlagManager.flags.renderStreamingIfAllowed is null)
                throw new InvalidOperationException("The render streaming flag is not set.");
            if (FlagManager.flags.windowWidth is null || FlagManager.flags.windowWidth <= 0)
                throw new InvalidOperationException("The simulated window width must be positive.");
            if (FlagManager.flags.windowHeight is null || FlagManager.flags.windowHeight <= 0)
                throw new InvalidOperationException("The simulated window height must be positive.");
        }

        private static void ValidateFaceManifestFlags()
        {
            if (!string.IsNullOrWhiteSpace(FlagManager.flags.benchmarkOutput))
                throw new InvalidOperationException("Benchmark mode and face manifest mode cannot run together.");
            if (!string.IsNullOrWhiteSpace(FlagManager.flags.simulatedGpuUploadOutput))
                throw new InvalidOperationException("Simulated GPU upload mode and face manifest mode cannot run together.");
            if (string.IsNullOrWhiteSpace(FlagManager.flags.game))
                throw new InvalidOperationException("The face manifest game is not set.");
            if (string.IsNullOrWhiteSpace(FlagManager.flags.worldName))
                throw new InvalidOperationException("The face manifest world name is not set.");
            if (!FlagManager.flags.seed.HasValue)
                throw new InvalidOperationException("The face manifest seed is not set.");
            if (!FlagManager.flags.faceGenerationMode.HasValue)
                throw new InvalidOperationException("The face generation mode is not set.");
            if (FlagManager.flags.renderStreamingIfAllowed is not false)
                throw new InvalidOperationException("Face manifest mode requires renderStreamingIfAllowed=false.");
        }
    }
}

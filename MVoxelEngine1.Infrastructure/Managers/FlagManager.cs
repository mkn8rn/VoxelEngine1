using MVoxelEngine1.Infrastructure.Flags;
using MVoxelEngine1.Infrastructure.Models;
using System;
using System.Runtime;

namespace MVoxelEngine1.Infrastructure.Managers
{
    public static class FlagManager
    {
        public static ProgramFlags flags { get; private set; } = new ProgramFlags();
        public static void ApplyFlags(string[] args)
        {
            var consoleFlags = ConsoleFlags.consoleFlags;
            var envFlags = EnvironmentFlags.environmentFlags;

            T? PreferValue<T>(T? console, T? env) where T : struct
                => console.HasValue ? console : env;
            string PreferString(string? console, string? env)
                => !string.IsNullOrEmpty(console) ? console! : !string.IsNullOrEmpty(env) ? env! : string.Empty;

            var preparedFlags = new ProgramFlags
            {
                game = PreferString(consoleFlags.game, envFlags.game),
                gameDataDirectory = PreferString(consoleFlags.gameDataDirectory, envFlags.gameDataDirectory),
                worldName = PreferString(consoleFlags.worldName, envFlags.worldName),
                seed = PreferValue(consoleFlags.seed, envFlags.seed),
                benchmarkOutput = PreferString(consoleFlags.benchmarkOutput, envFlags.benchmarkOutput),
                allocationValidationOutput = PreferString(consoleFlags.allocationValidationOutput, envFlags.allocationValidationOutput),
                graphicsBenchmarkOutput = PreferString(
                    consoleFlags.graphicsBenchmarkOutput,
                    envFlags.graphicsBenchmarkOutput),
                faceManifestOutput = PreferString(consoleFlags.faceManifestOutput, envFlags.faceManifestOutput),
                simulatedGpuUploadOutput = PreferString(consoleFlags.simulatedGpuUploadOutput, envFlags.simulatedGpuUploadOutput),
                simulatedInput = PreferString(consoleFlags.simulatedInput, envFlags.simulatedInput),
                simulatedFrameRate = PreferValue(consoleFlags.simulatedFrameRate, envFlags.simulatedFrameRate),
                simulatedGpuWriterDelayMilliseconds = PreferValue(consoleFlags.simulatedGpuWriterDelayMilliseconds, envFlags.simulatedGpuWriterDelayMilliseconds),
                simulatedGpuWriterFailAfterRecords = PreferValue(consoleFlags.simulatedGpuWriterFailAfterRecords, envFlags.simulatedGpuWriterFailAfterRecords),
                faceGenerationMode = PreferValue(consoleFlags.faceGenerationMode, envFlags.faceGenerationMode),
                windowWidth = PreferValue(consoleFlags.windowWidth, envFlags.windowWidth),
                windowHeight = PreferValue(consoleFlags.windowHeight, envFlags.windowHeight),
                useFacePooling = PreferValue(consoleFlags.useFacePooling, envFlags.useFacePooling),
                faceAmountToPool = PreferValue(consoleFlags.faceAmountToPool, envFlags.faceAmountToPool),
                worldGenWorkersPerCore = PreferValue(consoleFlags.worldGenWorkersPerCore, envFlags.worldGenWorkersPerCore),
                worldGenWorkersPerCoreInitial = PreferValue(consoleFlags.worldGenWorkersPerCoreInitial, envFlags.worldGenWorkersPerCoreInitial),
                meshRenderWorkersPerCore = PreferValue(consoleFlags.meshRenderWorkersPerCore, envFlags.meshRenderWorkersPerCore),
                meshRenderWorkersPerCoreInitial = PreferValue(consoleFlags.meshRenderWorkersPerCoreInitial, envFlags.meshRenderWorkersPerCoreInitial),
                renderStreamingIfAllowed = PreferValue(consoleFlags.renderStreamingIfAllowed, envFlags.renderStreamingIfAllowed),
                GCConcurrent = PreferValue(consoleFlags.GCConcurrent, envFlags.GCConcurrent),
                GCLatencyMode = PreferValue(consoleFlags.GCLatencyMode, envFlags.GCLatencyMode),
                GCHeapHardLimit = PreferString(consoleFlags.GCHeapHardLimit, envFlags.GCHeapHardLimit),
                GCHeapAffinitizeMask = PreferString(consoleFlags.GCHeapAffinitizeMask, envFlags.GCHeapAffinitizeMask),
                GCLargeObjectHeapCompactionMode = PreferValue(consoleFlags.GCLargeObjectHeapCompactionMode, envFlags.GCLargeObjectHeapCompactionMode),
                GCHeapSegmentSize = PreferString(consoleFlags.GCHeapSegmentSize, envFlags.GCHeapSegmentSize),
                GCStress = PreferString(consoleFlags.GCStress, envFlags.GCStress),
                GCLogEnabled = PreferValue(consoleFlags.GCLogEnabled, envFlags.GCLogEnabled),
                GCLogFile = PreferString(consoleFlags.GCLogFile, envFlags.GCLogFile),
                GCHeapCount = PreferString(consoleFlags.GCHeapCount, envFlags.GCHeapCount),
                GCMode = PreferValue(consoleFlags.GCMode, envFlags.GCMode)
            };

            if (!string.IsNullOrEmpty(preparedFlags.game))
                Console.WriteLine($"Set game: {preparedFlags.game}");
            if (!string.IsNullOrEmpty(preparedFlags.gameDataDirectory))
                Console.WriteLine($"Set gameDataDirectory: {preparedFlags.gameDataDirectory}");
            if (!string.IsNullOrEmpty(preparedFlags.worldName))
                Console.WriteLine($"Set worldName: {preparedFlags.worldName}");
            if (preparedFlags.seed.HasValue)
                Console.WriteLine($"Set seed: {preparedFlags.seed.Value}");
            if (!string.IsNullOrEmpty(preparedFlags.benchmarkOutput))
                Console.WriteLine($"Set benchmarkOutput: {preparedFlags.benchmarkOutput}");
            if (!string.IsNullOrEmpty(preparedFlags.allocationValidationOutput))
                Console.WriteLine($"Set allocationValidationOutput: {preparedFlags.allocationValidationOutput}");
            if (!string.IsNullOrEmpty(preparedFlags.graphicsBenchmarkOutput))
            {
                Console.WriteLine(
                    $"Set graphicsBenchmarkOutput: " +
                    $"{preparedFlags.graphicsBenchmarkOutput}");
            }
            if (!string.IsNullOrEmpty(preparedFlags.faceManifestOutput))
                Console.WriteLine($"Set faceManifestOutput: {preparedFlags.faceManifestOutput}");
            if (!string.IsNullOrEmpty(preparedFlags.simulatedGpuUploadOutput))
                Console.WriteLine($"Set simulatedGpuUploadOutput: {preparedFlags.simulatedGpuUploadOutput}");
            if (!string.IsNullOrEmpty(preparedFlags.simulatedInput))
                Console.WriteLine($"Set simulatedInput: {preparedFlags.simulatedInput}");
            if (preparedFlags.simulatedFrameRate.HasValue)
                Console.WriteLine($"Set simulatedFrameRate: {preparedFlags.simulatedFrameRate.Value}");
            if (preparedFlags.simulatedGpuWriterDelayMilliseconds.HasValue)
                Console.WriteLine($"Set simulatedGpuWriterDelayMilliseconds: {preparedFlags.simulatedGpuWriterDelayMilliseconds.Value}");
            if (preparedFlags.simulatedGpuWriterFailAfterRecords.HasValue)
                Console.WriteLine($"Set simulatedGpuWriterFailAfterRecords: {preparedFlags.simulatedGpuWriterFailAfterRecords.Value}");
            if (preparedFlags.faceGenerationMode.HasValue)
                Console.WriteLine($"Set faceGenerationMode: {preparedFlags.faceGenerationMode.Value}");
            if (preparedFlags.windowWidth.HasValue)
                Console.WriteLine($"Set windowWidth: {preparedFlags.windowWidth.Value}");
            if (preparedFlags.windowHeight.HasValue)
                Console.WriteLine($"Set windowHeight: {preparedFlags.windowHeight.Value}");
            if (preparedFlags.useFacePooling.HasValue)
                Console.WriteLine($"Set useFacePooling: {preparedFlags.useFacePooling.Value}");
            if (preparedFlags.faceAmountToPool.HasValue)
                Console.WriteLine($"Set faceAmountToPool: {preparedFlags.faceAmountToPool.Value}");
            if (preparedFlags.worldGenWorkersPerCore.HasValue)
                Console.WriteLine($"Set worldGenWorkersPerCore: {preparedFlags.worldGenWorkersPerCore.Value}");
            if (preparedFlags.worldGenWorkersPerCoreInitial.HasValue)
                Console.WriteLine($"Set worldGenWorkersPerCoreInitial: {preparedFlags.worldGenWorkersPerCoreInitial.Value}");
            if (preparedFlags.meshRenderWorkersPerCore.HasValue)
                Console.WriteLine($"Set meshRenderWorkersPerCore: {preparedFlags.meshRenderWorkersPerCore.Value}");
            if (preparedFlags.meshRenderWorkersPerCoreInitial.HasValue)
                Console.WriteLine($"Set meshRenderWorkersPerCoreInitial: {preparedFlags.meshRenderWorkersPerCoreInitial.Value}");
            if (preparedFlags.renderStreamingIfAllowed.HasValue)
                Console.WriteLine($"Set renderStreamingIfAllowed: {preparedFlags.renderStreamingIfAllowed.Value}");
            if (preparedFlags.GCConcurrent.HasValue)
                Console.WriteLine($"Set GCConcurrent: {preparedFlags.GCConcurrent.Value}");
            if (preparedFlags.GCLatencyMode.HasValue)
                Console.WriteLine($"Set GCLatencyMode: {preparedFlags.GCLatencyMode.Value}");
            if (!string.IsNullOrEmpty(preparedFlags.GCHeapHardLimit))
                Console.WriteLine($"Set GCHeapHardLimit: {preparedFlags.GCHeapHardLimit}");
            if (!string.IsNullOrEmpty(preparedFlags.GCHeapAffinitizeMask))
                Console.WriteLine($"Set GCHeapAffinitizeMask: {preparedFlags.GCHeapAffinitizeMask}");
            if (preparedFlags.GCLargeObjectHeapCompactionMode.HasValue)
                Console.WriteLine($"Set GCLargeObjectHeapCompactionMode: {preparedFlags.GCLargeObjectHeapCompactionMode.Value}");
            CompleteApplyFlagsPhase(preparedFlags);
        }

        private static void CompleteApplyFlagsPhase(global::MVoxelEngine1.Infrastructure.Models.ProgramFlags preparedFlags)
        {
            if (!string.IsNullOrEmpty(preparedFlags.GCHeapSegmentSize))
                Console.WriteLine($"Set GCHeapSegmentSize: {preparedFlags.GCHeapSegmentSize}");
            if (!string.IsNullOrEmpty(preparedFlags.GCStress))
                Console.WriteLine($"Set GCStress: {preparedFlags.GCStress}");
            if (preparedFlags.GCLogEnabled.HasValue)
                Console.WriteLine($"Set GCLogEnabled: {preparedFlags.GCLogEnabled.Value}");
            if (!string.IsNullOrEmpty(preparedFlags.GCLogFile))
                Console.WriteLine($"Set GCLogFile: {preparedFlags.GCLogFile}");
            if (!string.IsNullOrEmpty(preparedFlags.GCHeapCount))
                Console.WriteLine($"Set GCHeapCount: {preparedFlags.GCHeapCount}");
            if (preparedFlags.GCMode.HasValue)
                Console.WriteLine($"Set GCMode: {preparedFlags.GCMode.Value}");

            if (preparedFlags.GCLatencyMode.HasValue)
                GCSettings.LatencyMode = (System.Runtime.GCLatencyMode)preparedFlags.GCLatencyMode.Value;

            if (preparedFlags.GCLargeObjectHeapCompactionMode.HasValue)
                GCSettings.LargeObjectHeapCompactionMode = (System.Runtime.GCLargeObjectHeapCompactionMode)preparedFlags.GCLargeObjectHeapCompactionMode.Value;

            if (preparedFlags.GCMode.HasValue)
                Environment.SetEnvironmentVariable("COMPlus_gcServer", ((int)preparedFlags.GCMode.Value).ToString(System.Globalization.CultureInfo.CurrentCulture));

            if (preparedFlags.GCConcurrent.HasValue)
                Environment.SetEnvironmentVariable("COMPlus_GCConcurrent", ((int)preparedFlags.GCConcurrent.Value).ToString(System.Globalization.CultureInfo.CurrentCulture));

            if (preparedFlags.GCLogEnabled.HasValue)
                Environment.SetEnvironmentVariable("COMPlus_GCLogEnabled", ((int)preparedFlags.GCLogEnabled.Value).ToString(System.Globalization.CultureInfo.CurrentCulture));

            if (!string.IsNullOrEmpty(preparedFlags.GCHeapHardLimit))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapHardLimit", preparedFlags.GCHeapHardLimit);

            if (!string.IsNullOrEmpty(preparedFlags.GCHeapAffinitizeMask))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapAffinitizeMask", preparedFlags.GCHeapAffinitizeMask);

            if (!string.IsNullOrEmpty(preparedFlags.GCHeapSegmentSize))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapSegmentSize", preparedFlags.GCHeapSegmentSize);

            if (!string.IsNullOrEmpty(preparedFlags.GCStress))
                Environment.SetEnvironmentVariable("COMPlus_GCStress", preparedFlags.GCStress);

            if (!string.IsNullOrEmpty(preparedFlags.GCLogFile))
                Environment.SetEnvironmentVariable("COMPlus_GCLogFile", preparedFlags.GCLogFile);

            if (!string.IsNullOrEmpty(preparedFlags.GCHeapCount))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapCount", preparedFlags.GCHeapCount);

            FlagManager.flags = preparedFlags;

        }
    }
}

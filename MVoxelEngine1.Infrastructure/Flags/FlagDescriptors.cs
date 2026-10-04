using System;
using System.Collections.Generic;
using System.Globalization;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Infrastructure.Flags
{
    internal static class FlagDescriptors
    {
        public static readonly FlagDescriptor[] All = new FlagDescriptor[]
        {
            new FlagDescriptor("game", (f, v) => f.game = v),
            new FlagDescriptor("gameDataDirectory", (f, v) => f.gameDataDirectory = v),
            new FlagDescriptor("worldName", (f, v) => f.worldName = v),
            new FlagDescriptor("seed", (f, v) =>
            {
                if (int.TryParse(v, out var x))
                    f.seed = x;
            }),
            new FlagDescriptor("benchmarkOutput", (f, v) => f.benchmarkOutput = v),
            new FlagDescriptor("allocationValidationOutput", (f, v) => f.allocationValidationOutput = v),
            new FlagDescriptor("graphicsBenchmarkOutput", (f, v) => f.graphicsBenchmarkOutput = v),
            new FlagDescriptor("faceManifestOutput", (f, v) => f.faceManifestOutput = v),
            new FlagDescriptor("simulatedGpuUploadOutput", (f, v) => f.simulatedGpuUploadOutput = v),
            new FlagDescriptor("simulatedInput", (f, v) => f.simulatedInput = v),
            new FlagDescriptor("simulatedFrameRate", (f, v) =>
            {
                if (int.TryParse(v, out var x))
                    f.simulatedFrameRate = x;
            }),
            new FlagDescriptor("simulatedGpuWriterDelayMilliseconds", (f, v) =>
            {
                if (int.TryParse(v, out var x))
                    f.simulatedGpuWriterDelayMilliseconds = x;
            }),
            new FlagDescriptor("simulatedGpuWriterFailAfterRecords", (f, v) =>
            {
                if (int.TryParse(v, out var x))
                    f.simulatedGpuWriterFailAfterRecords = x;
            }),
            new FlagDescriptor("faceGenerationMode", (f, v) =>
            {
                if (Enum.TryParse<FaceGenerationMode>(v, true, out var x) && Enum.IsDefined(x))
                    f.faceGenerationMode = x;
            }),
            new FlagDescriptor("windowWidth", (f, v) =>
            {
                if (int.TryParse(v, out var x))
                    f.windowWidth = x;
            }),
            new FlagDescriptor("windowHeight", (f, v) =>
            {
                if (int.TryParse(v, out var x))
                    f.windowHeight = x;
            }),
            new FlagDescriptor("worldGenWorkersPerCore", (f, v) => f.worldGenWorkersPerCore = ParseWorkerMultiplier(v)),
            new FlagDescriptor("WorldGenWorkersPerCoreInitial", (f, v) => f.worldGenWorkersPerCoreInitial = ParseWorkerMultiplier(v)),
            new FlagDescriptor("meshRenderWorkersPerCore", (f, v) => f.meshRenderWorkersPerCore = ParseWorkerMultiplier(v)),
            new FlagDescriptor("MeshRenderWorkersPerCoreInitial", (f, v) => f.meshRenderWorkersPerCoreInitial = ParseWorkerMultiplier(v)),
            new FlagDescriptor("renderStreamingIfAllowed", (f, v) =>
            {
                if (bool.TryParse(v, out var x))
                    f.renderStreamingIfAllowed = x;
            }),
            new FlagDescriptor("GCConcurrent", (f, v) =>
            {
                if (Enum.TryParse<GCConcurrent>(v, true, out var x))
                    f.GCConcurrent = x;
            }),
            new FlagDescriptor("GCLatencyMode", (f, v) =>
            {
                if (Enum.TryParse<GCLatencyMode>(v, true, out var x))
                    f.GCLatencyMode = x;
            }),
            new FlagDescriptor("GCHeapHardLimit", (f, v) => f.GCHeapHardLimit = v),
            new FlagDescriptor("GCHeapAffinitizeMask", (f, v) => f.GCHeapAffinitizeMask = v),
            new FlagDescriptor("GCLargeObjectHeapCompactionMode", (f, v) =>
            {
                if (Enum.TryParse<GCLargeObjectHeapCompactionMode>(v, true, out var x))
                    f.GCLargeObjectHeapCompactionMode = x;
            }),
            new FlagDescriptor("GCHeapSegmentSize", (f, v) => f.GCHeapSegmentSize = v),
            new FlagDescriptor("GCStress", (f, v) => f.GCStress = v),
            new FlagDescriptor("GCLogEnabled", (f, v) =>
            {
                if (Enum.TryParse<GCLogEnabled>(v, true, out var x))
                    f.GCLogEnabled = x;
            }),
            new FlagDescriptor("GCLogFile", (f, v) => f.GCLogFile = v),
            new FlagDescriptor("GCHeapCount", (f, v) => f.GCHeapCount = v),
            new FlagDescriptor("GCMode", (f, v) =>
            {
                if (Enum.TryParse<GCMode>(v, true, out var x))
                    f.GCMode = x;
            }),
        };
        private static readonly Dictionary<string, FlagDescriptor> _byName = CreateLookup();
        private static float? ParseWorkerMultiplier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            // Existing environment files use a decimal comma. Treat it as a
            // decimal separator in every culture, never as a thousands separator.
            string normalized = value.Contains(',') ? value.Replace(',', '.') : value;
            if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                throw new FormatException("The worker multiplier must be a decimal number.");
            if (!float.IsFinite(result) || result < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "The worker multiplier must be finite and nonnegative.");
            return result;
        }

        private static Dictionary<string, FlagDescriptor> CreateLookup()
        {
            var dict = new Dictionary<string, FlagDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in All)
                dict[d.Name] = d;
            return dict;
        }

        public static bool TryGet(string name, out FlagDescriptor descriptor) => _byName.TryGetValue(name, out descriptor!);
        public static void Apply(ProgramFlags flags, string name, string value)
        {
            if (TryGet(name, out var d))
                d.Apply(flags, value);
        }
    }
}

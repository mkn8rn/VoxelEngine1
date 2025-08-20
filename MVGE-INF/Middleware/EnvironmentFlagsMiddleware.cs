using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using MVGE_INF.Models;

namespace MVGE_INF.Middleware
{
    public class EnvironmentFlagsMiddleware
    {
        private static readonly string EnvFolder = Path.GetDirectoryName(typeof(EnvironmentFlagsMiddleware).Assembly.Location)!;
        private static readonly string EnvFileName = IsDevelopment() ? ".env.development" : ".env";
        private static readonly string EnvFilePath = Path.Combine(EnvFolder, EnvFileName);

        public static ProgramFlags environmentFlags { get; private set; } = new ProgramFlags();

        public static bool IsDevelopment()
        {
            var aspnetEnv = System.Environment.GetEnvironmentVariable("APP_ENVIRONMENT");
            return string.Equals(aspnetEnv, "Development", StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<string, string> ReadEnvironmentVariables()
        {
            var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(EnvFilePath))
                throw new FileNotFoundException($"Environment file not found: {EnvFilePath}");

            foreach (var line in File.ReadAllLines(EnvFilePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                    envVars[parts[0].Trim()] = parts[1].Trim();
            }
            return envVars;
        }

        public static void LoadEnvironmentFlags()
        {
            var env = ReadEnvironmentVariables();
            var flags = new ProgramFlags();

            string? GetEnv(string key) => env.TryGetValue(key.ToUpperInvariant(), out var value) ? value : null;

            if (GetEnv("game") is string game)
                flags.game = game;
            if (GetEnv("gamesDirectory") is string gamesDirectory)
                flags.gamesDirectory = gamesDirectory;
            if (GetEnv("windowWidth") is string windowWidth && int.TryParse(windowWidth, out var windowWidthVal))
                flags.windowWidth = windowWidthVal;
            if (GetEnv("windowHeight") is string windowHeight && int.TryParse(windowHeight, out var windowHeightVal))
                flags.windowHeight = windowHeightVal;
            if (GetEnv("useFacePooling") is string useFacePooling && bool.TryParse(useFacePooling, out var useFacePoolingVal))
                flags.useFacePooling = useFacePoolingVal;
            if (GetEnv("faceAmountToPool") is string faceAmountToPool && int.TryParse(faceAmountToPool, out var faceAmountToPoolVal))
                flags.faceAmountToPool = faceAmountToPoolVal;
            if (GetEnv("worldGenWorkersPerCore") is string wg && float.TryParse(wg, out var wgVal))
                flags.worldGenWorkersPerCore = wgVal;
            if (GetEnv("meshRenderWorkersPerCore") is string mr && float.TryParse(mr, out var mrVal))
                flags.meshRenderWorkersPerCore = mrVal;
            if (GetEnv("renderStreamingIfAllowed") is string rs && bool.TryParse(rs, out var rsVal))
                flags.renderStreamingIfAllowed = rsVal;
            if (GetEnv("GCConcurrent") is string gcConcurrent && Enum.TryParse<GCConcurrent>(gcConcurrent, true, out var gcConcurrentVal))
                flags.GCConcurrent = gcConcurrentVal;
            if (GetEnv("GCLatencyMode") is string gcLatencyMode && Enum.TryParse<GCLatencyMode>(gcLatencyMode, true, out var gcLatencyModeVal))
                flags.GCLatencyMode = gcLatencyModeVal;
            if (GetEnv("GCHeapHardLimit") is string gcHeapHardLimit)
                flags.GCHeapHardLimit = gcHeapHardLimit;
            if (GetEnv("GCHeapAffinitizeMask") is string gcHeapAffinitizeMask)
                flags.GCHeapAffinitizeMask = gcHeapAffinitizeMask;
            if (GetEnv("GCLargeObjectHeapCompactionMode") is string gcLohCompactionMode && Enum.TryParse<GCLargeObjectHeapCompactionMode>(gcLohCompactionMode, true, out var gcLohCompactionModeVal))
                flags.GCLargeObjectHeapCompactionMode = gcLohCompactionModeVal;
            if (GetEnv("GCHeapSegmentSize") is string gcHeapSegmentSize)
                flags.GCHeapSegmentSize = gcHeapSegmentSize;
            if (GetEnv("GCStress") is string gcStress)
                flags.GCStress = gcStress;
            if (GetEnv("GCLogEnabled") is string gcLogEnabled && Enum.TryParse<GCLogEnabled>(gcLogEnabled, true, out var gcLogEnabledVal))
                flags.GCLogEnabled = gcLogEnabledVal;
            if (GetEnv("GCLogFile") is string gcLogFile)
                flags.GCLogFile = gcLogFile;
            if (GetEnv("GCHeapCount") is string gcHeapCount)
                flags.GCHeapCount = gcHeapCount;
            if (GetEnv("GCMode") is string gcMode && Enum.TryParse<GCMode>(gcMode, true, out var gcModeVal))
                flags.GCMode = gcModeVal;

            environmentFlags = flags;
        }
    }
}

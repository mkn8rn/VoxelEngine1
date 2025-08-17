using MVGE_INF.Middleware;
using MVGE_INF.Models;
using System;
using System.Runtime;

namespace MVGE_INF.Managers
{
    public class FlagManager
    {
        public static ProgramFlags Flags { get; private set; } = new ProgramFlags();
        public static void ApplyFlags(string[] args)
        {
            var consoleFlags = ConsoleFlagsMiddleware.Flags;
            var envFlags = EnvironmentFlagsMiddleware.EnvironmentFlags;

            T? PreferValue<T>(T? console, T? env) where T : struct
                => console.HasValue ? console : env;
            string PreferString(string? console, string? env)
                => !string.IsNullOrEmpty(console) ? console! : !string.IsNullOrEmpty(env) ? env! : string.Empty;

            var flags = new ProgramFlags
            {
                game = PreferString(consoleFlags.game, envFlags.game),
                gamesDirectory = PreferString(consoleFlags.gamesDirectory, envFlags.gamesDirectory),
                windowWidth = PreferValue(consoleFlags.windowWidth, envFlags.windowWidth),
                windowHeight = PreferValue(consoleFlags.windowHeight, envFlags.windowHeight),
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

            if (flags.GCLatencyMode.HasValue)
                GCSettings.LatencyMode = (System.Runtime.GCLatencyMode)flags.GCLatencyMode.Value;

            if (flags.GCLargeObjectHeapCompactionMode.HasValue)
                GCSettings.LargeObjectHeapCompactionMode = (System.Runtime.GCLargeObjectHeapCompactionMode)flags.GCLargeObjectHeapCompactionMode.Value;

            if (flags.GCMode.HasValue)
                Environment.SetEnvironmentVariable("COMPlus_gcServer", ((int)flags.GCMode.Value).ToString());

            if (flags.GCConcurrent.HasValue)
                Environment.SetEnvironmentVariable("COMPlus_GCConcurrent", ((int)flags.GCConcurrent.Value).ToString());

            if (flags.GCLogEnabled.HasValue)
                Environment.SetEnvironmentVariable("COMPlus_GCLogEnabled", ((int)flags.GCLogEnabled.Value).ToString());

            if (!string.IsNullOrEmpty(flags.GCHeapHardLimit))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapHardLimit", flags.GCHeapHardLimit);

            if (!string.IsNullOrEmpty(flags.GCHeapAffinitizeMask))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapAffinitizeMask", flags.GCHeapAffinitizeMask);

            if (!string.IsNullOrEmpty(flags.GCHeapSegmentSize))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapSegmentSize", flags.GCHeapSegmentSize);

            if (!string.IsNullOrEmpty(flags.GCStress))
                Environment.SetEnvironmentVariable("COMPlus_GCStress", flags.GCStress);

            if (!string.IsNullOrEmpty(flags.GCLogFile))
                Environment.SetEnvironmentVariable("COMPlus_GCLogFile", flags.GCLogFile);

            if (!string.IsNullOrEmpty(flags.GCHeapCount))
                Environment.SetEnvironmentVariable("COMPlus_GCHeapCount", flags.GCHeapCount);

            Flags = flags;
        }
    }
}

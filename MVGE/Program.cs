using MVGE;
using MVGE.Middleware;
using System;
using System.Runtime;

namespace GloriousTribesApp
{

    class Program
    {
        static void Main(string[] args)
        {
            FlagsMiddleware.Parse(args);
            var flags = FlagsMiddleware.Flags;

            if (flags.GCLatencyMode.HasValue)
                GCSettings.LatencyMode = (GCLatencyMode)flags.GCLatencyMode.Value;

            if (flags.GCLargeObjectHeapCompactionMode.HasValue)
                GCSettings.LargeObjectHeapCompactionMode = (GCLargeObjectHeapCompactionMode)flags.GCLargeObjectHeapCompactionMode.Value;

            // GCMode: default to workstation unless explicitly set.
            // This is according to the documentation better for client-side applications.
            if (flags.GCMode.HasValue)
                Environment.SetEnvironmentVariable("COMPlus_gcServer", ((int)flags.GCMode.Value).ToString());
            else
                Environment.SetEnvironmentVariable("COMPlus_gcServer", "0");

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

            using (GameManager game = new GameManager())
            {
                game.Run();
            }
        }
    }

}
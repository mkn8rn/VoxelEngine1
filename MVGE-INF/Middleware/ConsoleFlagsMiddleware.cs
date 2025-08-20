using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVGE_INF.Models;

namespace MVGE_INF.Middleware
{
    public static class ConsoleFlagsMiddleware
    {
        public static ProgramFlags consoleFlags { get; private set; } = new ProgramFlags();

        public static void Parse(string[] args)
        {
            var flags = new ProgramFlags();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--game")
                    flags.game = args[i + 1];
                else if (args[i] == "--gamesDirectory")
                    flags.gamesDirectory = args[i + 1];
                else if (args[i] == "--windowWidth")
                {
                    if (int.TryParse(args[i + 1], out var val))
                        flags.windowWidth = val;
                }
                else if (args[i] == "--windowHeight")
                {
                    if (int.TryParse(args[i + 1], out var val))
                        flags.windowHeight = val;
                }
                else if (args[i] == "--GCConcurrent")
                {
                    if (Enum.TryParse<GCConcurrent>(args[i + 1], true, out var val))
                        flags.GCConcurrent = val;
                }
                else if (args[i] == "--GCLatencyMode")
                {
                    if (Enum.TryParse<GCLatencyMode>(args[i + 1], true, out var val))
                        flags.GCLatencyMode = val;
                }
                else if (args[i] == "--GCHeapHardLimit")
                    flags.GCHeapHardLimit = args[i + 1];
                else if (args[i] == "--GCHeapAffinitizeMask")
                    flags.GCHeapAffinitizeMask = args[i + 1];
                else if (args[i] == "--GCLargeObjectHeapCompactionMode")
                {
                    if (Enum.TryParse<GCLargeObjectHeapCompactionMode>(args[i + 1], true, out var val))
                        flags.GCLargeObjectHeapCompactionMode = val;
                }
                else if (args[i] == "--GCHeapSegmentSize")
                    flags.GCHeapSegmentSize = args[i + 1];
                else if (args[i] == "--GCStress")
                    flags.GCStress = args[i + 1];
                else if (args[i] == "--GCLogEnabled")
                {
                    if (Enum.TryParse<GCLogEnabled>(args[i + 1], true, out var val))
                        flags.GCLogEnabled = val;
                }
                else if (args[i] == "--GCLogFile")
                    flags.GCLogFile = args[i + 1];
                else if (args[i] == "--GCHeapCount")
                    flags.GCHeapCount = args[i + 1];
                else if (args[i] == "--GCMode")
                {
                    if (Enum.TryParse<GCMode>(args[i + 1], true, out var val))
                        flags.GCMode = val;
                }
                else if (args[i] == "--useFacePooling")
                {
                    if (bool.TryParse(args[i + 1], out var val))
                        flags.useFacePooling = val;
                }
                else if (args[i] == "--faceAmountToPool")
                {
                    if (int.TryParse(args[i + 1], out var val))
                        flags.faceAmountToPool = val;
                }
                else if (args[i] == "--worldGenWorkersPerCore")
                {
                    if (float.TryParse(args[i + 1], out var val))
                        flags.worldGenWorkersPerCore = val;
                }
                else if (args[i] == "--meshRenderWorkersPerCore")
                {
                    if (float.TryParse(args[i + 1], out var val))
                        flags.meshRenderWorkersPerCore = val;
                }
                else if (args[i] == "--renderStreamingIfAllowed")
                {
                    if (bool.TryParse(args[i + 1], out var val))
                        flags.renderStreamingIfAllowed = val;
                }
            }
            consoleFlags = flags;
        }
    }
}

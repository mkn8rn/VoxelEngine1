using System;
using System.Collections.Generic;
using MVGE_INF.Models;

namespace MVGE_INF.Flags
{
    internal sealed class FlagDescriptor
    {
        public string Name { get; }
        private readonly Action<ProgramFlags, string> _apply;

        public FlagDescriptor(string name, Action<ProgramFlags, string> apply)
        {
            Name = name;
            _apply = apply;
        }

        public void Apply(ProgramFlags flags, string value) => _apply(flags, value);
    }

    internal static class FlagDescriptors
    {
        public static readonly FlagDescriptor[] All = new FlagDescriptor[]
        {
            new FlagDescriptor("game", (f,v) => f.game = v),
            new FlagDescriptor("gamesDirectory", (f,v) => f.gamesDirectory = v),
            new FlagDescriptor("windowWidth", (f,v) => { if (int.TryParse(v, out var x)) f.windowWidth = x; }),
            new FlagDescriptor("windowHeight", (f,v) => { if (int.TryParse(v, out var x)) f.windowHeight = x; }),
            new FlagDescriptor("worldGenWorkersPerCore", (f,v) => { if (float.TryParse(v, out var x)) f.worldGenWorkersPerCore = x; }),
            new FlagDescriptor("WorldGenWorkersPerCoreInitial", (f,v) => { if (float.TryParse(v, out var x)) f.worldGenWorkersPerCoreInitial = x; }),
            new FlagDescriptor("meshRenderWorkersPerCore", (f,v) => { if (float.TryParse(v, out var x)) f.meshRenderWorkersPerCore = x; }),
            new FlagDescriptor("MeshRenderWorkersPerCoreInitial", (f,v) => { if (float.TryParse(v, out var x)) f.meshRenderWorkersPerCoreInitial = x; }),
            new FlagDescriptor("renderStreamingIfAllowed", (f,v) => { if (bool.TryParse(v, out var x)) f.renderStreamingIfAllowed = x; }),
            new FlagDescriptor("GCConcurrent", (f,v) => { if (Enum.TryParse<GCConcurrent>(v, true, out var x)) f.GCConcurrent = x; }),
            new FlagDescriptor("GCLatencyMode", (f,v) => { if (Enum.TryParse<GCLatencyMode>(v, true, out var x)) f.GCLatencyMode = x; }),
            new FlagDescriptor("GCHeapHardLimit", (f,v) => f.GCHeapHardLimit = v),
            new FlagDescriptor("GCHeapAffinitizeMask", (f,v) => f.GCHeapAffinitizeMask = v),
            new FlagDescriptor("GCLargeObjectHeapCompactionMode", (f,v) => { if (Enum.TryParse<GCLargeObjectHeapCompactionMode>(v, true, out var x)) f.GCLargeObjectHeapCompactionMode = x; }),
            new FlagDescriptor("GCHeapSegmentSize", (f,v) => f.GCHeapSegmentSize = v),
            new FlagDescriptor("GCStress", (f,v) => f.GCStress = v),
            new FlagDescriptor("GCLogEnabled", (f,v) => { if (Enum.TryParse<GCLogEnabled>(v, true, out var x)) f.GCLogEnabled = x; }),
            new FlagDescriptor("GCLogFile", (f,v) => f.GCLogFile = v),
            new FlagDescriptor("GCHeapCount", (f,v) => f.GCHeapCount = v),
            new FlagDescriptor("GCMode", (f,v) => { if (Enum.TryParse<GCMode>(v, true, out var x)) f.GCMode = x; }),
        };

        private static readonly Dictionary<string, FlagDescriptor> _byName = CreateLookup();

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

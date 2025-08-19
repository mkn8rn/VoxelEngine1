using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models
{
    public enum GCMode
    {
        Workstation = 0,
        Server = 1,
    }

    public enum GCLatencyMode
    {
        Batch = 0,
        Interactive = 1,
        LowLatency = 2,
        SustainedLowLatency = 3,
        NoGCRegion = 4
    }

    public enum GCLargeObjectHeapCompactionMode
    {
        Default = 0,
        CompactOnce = 1
    }

    public enum GCConcurrent
    {
        Disabled = 0,
        Enabled = 1
    }

    public enum GCLogEnabled
    {
        Disabled = 0,
        Enabled = 1
    }

    public class ProgramFlags
    {
        public string? game;
        public string? gamesDirectory;

        // Window settings
        public int? windowWidth;
        public int? windowHeight;

        // Face pooling
        public bool? useFacePooling;
        public int? faceAmountToPool;

        // GC settings
        public GCConcurrent? GCConcurrent;
        public GCLatencyMode? GCLatencyMode;
        public string? GCHeapHardLimit;
        public string? GCHeapAffinitizeMask;
        public GCLargeObjectHeapCompactionMode? GCLargeObjectHeapCompactionMode;
        public string? GCHeapSegmentSize;
        public string? GCStress;
        public GCLogEnabled? GCLogEnabled;
        public string? GCLogFile;
        public string? GCHeapCount;
        public GCMode? GCMode;
    }
}

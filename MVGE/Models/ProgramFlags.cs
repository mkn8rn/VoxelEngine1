using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE.Models
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
        public string? game { get; set; }
        public string? gamesDirectory { get; set; }
        public int? windowWidth { get; set; }
        public int? windowHeight { get; set; }
        public GCConcurrent? GCConcurrent { get; set; }
        public GCLatencyMode? GCLatencyMode { get; set; }
        public string? GCHeapHardLimit { get; set; }
        public string? GCHeapAffinitizeMask { get; set; }
        public GCLargeObjectHeapCompactionMode? GCLargeObjectHeapCompactionMode { get; set; }
        public string? GCHeapSegmentSize { get; set; }
        public string? GCStress { get; set; }
        public GCLogEnabled? GCLogEnabled { get; set; }
        public string? GCLogFile { get; set; }
        public string? GCHeapCount { get; set; }
        public GCMode? GCMode { get; set; }
    }
}

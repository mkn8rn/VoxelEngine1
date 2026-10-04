using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models
{
    public class ProgramFlags
    {
        public string? game { get; set; }
        public string? gameDataDirectory { get; set; }
        public string? worldName { get; set; }
        public int? seed { get; set; }
        public string? benchmarkOutput { get; set; }
        public string? allocationValidationOutput { get; set; }
        public string? graphicsBenchmarkOutput { get; set; }
        public string? faceManifestOutput { get; set; }
        public string? simulatedGpuUploadOutput { get; set; }
        public string? simulatedInput { get; set; }
        public int? simulatedFrameRate { get; set; }
        public int? simulatedGpuWriterDelayMilliseconds { get; set; }
        public int? simulatedGpuWriterFailAfterRecords { get; set; }
        public FaceGenerationMode? faceGenerationMode { get; set; }
        // Window settings
        public int? windowWidth { get; set; }
        public int? windowHeight { get; set; }
        // Render settings
        public bool? useFacePooling { get; set; }
        public int? faceAmountToPool { get; set; }
        public float? worldGenWorkersPerCore { get; set; }
        public float? worldGenWorkersPerCoreInitial { get; set; }
        public float? meshRenderWorkersPerCore { get; set; }
        public float? meshRenderWorkersPerCoreInitial { get; set; }
        public bool? renderStreamingIfAllowed { get; set; }
        // GC settings
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

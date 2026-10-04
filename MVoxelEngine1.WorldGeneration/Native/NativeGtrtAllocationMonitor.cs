using System.Runtime;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
public sealed class NativeGtrtAllocationMonitor : IDisposable
{
    private readonly Action publicationObserver;
    private NativeWorkerAllocationSample[] samples = [];
    private NativeGtrtPipeline? pipeline;
    private NativePreUploadPacket firstPacket;
    private NativeSessionAllocationMetrics metrics;
    private long coordinatorStart;
    private long processStart;
    private long coordinatorBytes;
    private long processBytes;
    private int coordinatorThread;
    private int generation0;
    private int generation1;
    private int generation2;
    private int generation0Changes;
    private int generation1Changes;
    private int generation2Changes;
    private bool ownsNoGcRegion;
    private bool prepared;
    private bool started;
    private bool completed;
    private bool noGcRegionCompleted;
    public NativeGtrtAllocationMonitor() => publicationObserver = RecordPublication;
    internal void Prepare(NativeGtrtPipeline source)
    {
        if (prepared)
            throw new InvalidOperationException("The allocation monitor is already prepared.");
        prepared = true;
        pipeline = source;
        samples = new NativeWorkerAllocationSample[source.PreparedWorkerCount];
        source.CopyWorkerAllocationSamples(samples);
        source.ObserveSeedPublication(publicationObserver);
        _ = GC.GetAllocatedBytesForCurrentThread();
        _ = GC.GetTotalAllocatedBytes(precise: true);
        coordinatorThread = Environment.CurrentManagedThreadId;
        if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
        {
            if (!GC.TryStartNoGCRegion(16 * 1024 * 1024))
                throw new InvalidOperationException("The native allocation gate could not reserve its no-GC interval.");
            ownsNoGcRegion = true;
        }
    }

    private void RecordPublication()
    {
        generation0 = GC.CollectionCount(0);
        generation1 = GC.CollectionCount(1);
        generation2 = GC.CollectionCount(2);
        coordinatorStart = GC.GetAllocatedBytesForCurrentThread();
        processStart = GC.GetTotalAllocatedBytes(precise: true);
        started = true;
    }

    internal void BeforeRequiredUpload(in MVoxelEngine1.Graphics.Terrain.NativeChunkRenderPacketDescriptor descriptor)
    {
        if (completed || descriptor.OpaqueWordCount + descriptor.TransparentWordCount == 0)
            return;
        if (!started || pipeline is null)
            throw new InvalidOperationException("The native allocation interval did not begin at publication.");
        processBytes = GC.GetTotalAllocatedBytes(precise: true) - processStart;
        coordinatorBytes = GC.GetAllocatedBytesForCurrentThread() - coordinatorStart;
        generation0Changes = GC.CollectionCount(0) - generation0;
        generation1Changes = GC.CollectionCount(1) - generation1;
        generation2Changes = GC.CollectionCount(2) - generation2;
        firstPacket = pipeline.DescribePreUpload(in descriptor);
        EndNoGcRegion();
        noGcRegionCompleted = generation0Changes == 0 && generation1Changes == 0 && generation2Changes == 0;
        pipeline.CopyWorkerAllocationSamples(samples);
        metrics = pipeline.GetAllocationMetrics();
        completed = true;
    }

    public NativeGtrtAllocationEvidence Capture()
    {
        if (!completed)
            throw new InvalidOperationException("The native allocation interval has no required pre-upload endpoint.");
        return new NativeGtrtAllocationEvidence("validated native seed publication", "immediately before the first nonempty packet renderer/upload callback", coordinatorThread, coordinatorBytes, processBytes, generation0Changes, generation1Changes, generation2Changes, noGcRegionCompleted, Array.AsReadOnly(samples), firstPacket, metrics);
    }

    public void Dispose() => EndNoGcRegion();
    private void EndNoGcRegion()
    {
        if (!ownsNoGcRegion)
            return;
        ownsNoGcRegion = false;
        GC.EndNoGCRegion();
    }
}

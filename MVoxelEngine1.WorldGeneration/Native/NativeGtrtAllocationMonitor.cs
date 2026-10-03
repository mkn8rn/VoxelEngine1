using System.Runtime;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

public readonly record struct NativeSessionAllocationMetrics(
    long OwnerLengthBytes,
    long OwnerCapacityBytes,
    int ResidentColumns,
    int RequiredChunks,
    int ProfileCount,
    int PacketWordCapacity,
    int PacketWordHighWaterCount,
    int MaterializedChunks,
    int MaterializedSections,
    int MaterializedRawSections,
    int MaterializedPaletteEntries,
    int MaterializedPackedWords)
{
    internal static NativeSessionAllocationMetrics Capture(NativeGtrtSession session)
    {
        NativeSessionAllocationMetrics metrics = default;
        session.Access((scoped NativeLeaseView<byte> owner) =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            metrics = new NativeSessionAllocationMetrics(
                session.OwnerLength, session.OwnerCapacity, view.Columns.Length,
                view.RequiredChunkCount, view.Profiles.Length, view.PacketWordCapacity,
                view.State.PacketWordCursor, view.State.MaterializedChunkCount,
                view.State.MaterializedSectionCount, view.State.MaterializedRawSectionCount,
                view.State.MaterializedPaletteCursor, view.State.MaterializedPackedWordCursor);
        });
        return metrics;
    }
}

public sealed record NativeGtrtAllocationEvidence(
    string IntervalStart,
    string IntervalEnd,
    int CoordinatorManagedThreadId,
    long CoordinatorManagedBytes,
    long ProcessManagedBytes,
    int Generation0Collections,
    int Generation1Collections,
    int Generation2Collections,
    bool NoGcRegionCompleted,
    IReadOnlyList<NativeWorkerAllocationSample> Workers,
    NativePreUploadPacket FirstRequiredPacket,
    NativeSessionAllocationMetrics NativeStorage);

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
        return new NativeGtrtAllocationEvidence(
            "validated native seed publication", "immediately before the first nonempty packet renderer/upload callback",
            coordinatorThread, coordinatorBytes, processBytes, generation0Changes, generation1Changes,
            generation2Changes, noGcRegionCompleted, Array.AsReadOnly(samples), firstPacket, metrics);
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

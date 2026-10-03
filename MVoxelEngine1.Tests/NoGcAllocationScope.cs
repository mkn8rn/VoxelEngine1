namespace MVoxelEngine1.Tests;

internal sealed class NoGcAllocationScope : IDisposable
{
    private readonly int generation0;
    private readonly int generation1;
    private readonly int generation2;
    private bool completed;

    internal NoGcAllocationScope()
    {
        // Background GC can retire an unused allocation context as phantom bytes
        // on .NET 10.0.12. Preserve exact zero-byte assertions with a stable
        // collection-free interval, rather than subtracting or tolerating bytes.
        if (!GC.TryStartNoGCRegion(16 * 1024 * 1024))
            throw new InvalidOperationException("The allocation test could not reserve its no-GC interval.");
        generation0 = GC.CollectionCount(0);
        generation1 = GC.CollectionCount(1);
        generation2 = GC.CollectionCount(2);
    }

    public void Dispose()
    {
        if (completed)
            return;
        completed = true;
        bool collectionOccurred = generation0 != GC.CollectionCount(0) ||
            generation1 != GC.CollectionCount(1) || generation2 != GC.CollectionCount(2);
        GC.EndNoGCRegion();
        if (collectionOccurred)
            throw new InvalidOperationException("A collection invalidated the allocation test interval.");
    }
}

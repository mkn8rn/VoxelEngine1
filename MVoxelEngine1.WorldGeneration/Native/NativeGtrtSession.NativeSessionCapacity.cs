using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal sealed partial class NativeGtrtSession
{
    // One NAM byte owner has an Int32 length. Leave headroom below that limit.
    internal const int MaximumSessionByteCount = 2_000_000_000;
    internal void EnsureMaterializedCapacity(NativeMaterializedStorageRequirements requirements, int maximumByteCount = MaximumSessionByteCount)
    {
        ObjectDisposedException.ThrowIf(storage is null, this);
        NativeSessionStorageInfo info = storage.Read(ReadGrowthInfo);
        NativeGtrtSessionHeader previous = info.Header;
        NativeTerrainMaterialSet materials = info.Materials;
        if (requirements.ChunkCount <= previous.MaterializedChunkCapacity && requirements.SectionCount <= previous.MaterializedSectionCapacity && requirements.RawSectionCount <= previous.MaterializedRawSectionCapacity && requirements.PaletteCount <= previous.MaterializedPaletteCapacity && requirements.PackedWordCount <= previous.MaterializedPackedWordCapacity)
            return;
        NativeGtrtSessionLayout layout = SelectExpandedLayout(previous, materials, requirements, maximumByteCount);
        ReplaceStorage(previous, layout);
    }

    internal void ExpandPacketStorage(int maximumByteCount = MaximumSessionByteCount)
    {
        ObjectDisposedException.ThrowIf(storage is null, this);
        NativeSessionStorageInfo info = storage.Read(ReadGrowthInfo);
        ReplaceStorage(info.Header, SelectExpandedPacketLayout(info.Header, info.Materials, maximumByteCount));
    }

    internal static NativeGtrtSessionLayout SelectExpandedPacketLayout(NativeGtrtSessionHeader previous, NativeTerrainMaterialSet materials, int maximumByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumByteCount);
        int availableWords = Math.Max(0, maximumByteCount - previous.TotalByteCount) / sizeof(uint);
        int additionalWords = Math.Min(previous.PacketWordCapacity, availableWords) & ~1;
        if (additionalWords == 0)
            throw new InvalidOperationException("The native packet storage limit would be exceeded.");
        var requirements = new NativeMaterializedStorageRequirements(previous.MaterializedChunkCapacity, previous.MaterializedSectionCapacity, previous.MaterializedRawSectionCapacity, previous.MaterializedPaletteCapacity, previous.MaterializedPackedWordCapacity);
        int capacity = checked(previous.PacketWordCapacity + additionalWords);
        NativeGtrtSessionLayout layout = CreateExpandedLayout(previous, materials, requirements, geometric: false, packetWordCapacity: capacity);
        if (layout.TotalByteCount > maximumByteCount)
            layout = CreateExpandedLayout(previous, materials, requirements, geometric: false, packetWordCapacity: capacity - 2);
        if (layout.TotalByteCount > maximumByteCount || layout.PacketWordCapacity <= previous.PacketWordCapacity)
            throw new InvalidOperationException("The native packet storage limit would be exceeded.");
        return layout;
    }

    private void ReplaceStorage(NativeGtrtSessionHeader previous, NativeGtrtSessionLayout layout)
    {
        ObjectDisposedException.ThrowIf(storage is null, this);
        pendingGrowthHeader = previous;
        pendingGrowthLayout = layout;
        try
        {
            storage.Access(CreateGrowthStorage);
            previousGrowthStorage = NativeTransfer<byte>.Move(ref storage);
            try
            {
                storage = NativeTransfer<byte>.Move(ref pendingGrowthStorage);
            }
            finally
            {
                previousGrowthStorage!.Dispose();
                previousGrowthStorage = null;
            }
        }
        finally
        {
            pendingGrowthStorage?.Dispose();
            pendingGrowthStorage = null;
        }
    }

    internal static NativeGtrtSessionLayout SelectExpandedLayout(NativeGtrtSessionHeader previous, NativeTerrainMaterialSet materials, NativeMaterializedStorageRequirements requirements, int maximumByteCount)
    {
        NativeGtrtSessionLayout layout;
        try
        {
            try
            {
                layout = CreateExpandedLayout(previous, materials, requirements, geometric: true);
            }
            catch (OverflowException)
            {
                layout = CreateExpandedLayout(previous, materials, requirements, geometric: false);
            }

            if (layout.TotalByteCount > maximumByteCount)
                layout = CreateExpandedLayout(previous, materials, requirements, geometric: false);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("The native edit storage limit would be exceeded.", exception);
        }

        if (layout.TotalByteCount > maximumByteCount)
            throw new InvalidOperationException("The native edit storage limit would be exceeded.");
        return layout;
    }

    private NativeGtrtSessionHeader pendingGrowthHeader;
    private NativeGtrtSessionLayout pendingGrowthLayout;
    private NativeTransfer<byte>? pendingGrowthStorage;
    private NativeTransfer<byte>? previousGrowthStorage;
    private static NativeSessionStorageInfo ReadGrowthInfo(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        ref NativeGtrtSessionState state = ref view.State;
        if (state.TransactionOpen != 0 || state.ClaimedGenerationCount != 0 || state.ClaimedMeshCount != 0 || state.PacketConsumerCount != 0 || state.DisposalState != 0 || state.PacketAllocationLock != 0)
            throw new InvalidOperationException("Native storage can grow only while the session is idle.");
        foreach (ref readonly NativeChunkRecord chunk in view.Chunks)
        {
            if (chunk.State is NativeChunkState.MeshReady or NativeChunkState.PacketReady or NativeChunkState.Active)
                throw new InvalidOperationException("Native packets must retire before storage grows.");
        }

        return new NativeSessionStorageInfo(view.SessionHeader, view.Materials);
    }

    private void CreateGrowthStorage(scoped NativeLeaseView<byte> owner)
    {
        using NativeBuilder<byte> builder = new(preLease: pendingGrowthLayout.TotalByteCount);
        var copy = new NativeSessionCopyState(owner.AsSpan(), pendingGrowthHeader, pendingGrowthLayout);
        builder.Write<NativeSessionCopyState>(pendingGrowthLayout.TotalByteCount, in copy, static (scoped NativeBuilderWriter<byte> writer, scoped in NativeSessionCopyState current) =>
        {
            NativeGtrtSessionCloner.Clone(writer.AsSpan(), current.Source, current.Previous, current.Layout);
            writer.Commit(current.Layout.TotalByteCount);
        });
        NativeTransfer<byte>? created = builder.Complete();
        try
        {
            pendingGrowthStorage = NativeTransfer<byte>.Move(ref created);
        }
        finally
        {
            created?.Dispose();
        }
    }

    private static NativeGtrtSessionLayout CreateExpandedLayout(NativeGtrtSessionHeader previous, NativeTerrainMaterialSet materials, NativeMaterializedStorageRequirements requirements, bool geometric, int? packetWordCapacity = null) => new(previous.ChunkSizeX, previous.ChunkSizeY, previous.ChunkSizeZ, previous.Lod1Radius, materials, previous.GenerationWorkerCount, previous.MeshWorkerCount, packetWordCapacity ?? previous.PacketWordCapacity, previous.GameSnapshotByteCount, GrowCapacity(previous.MaterializedChunkCapacity, requirements.ChunkCount, geometric), GrowCapacity(previous.MaterializedSectionCapacity, requirements.SectionCount, geometric), GrowCapacity(previous.MaterializedRawSectionCapacity, requirements.RawSectionCount, geometric), GrowCapacity(previous.MaterializedPaletteCapacity, requirements.PaletteCount, geometric), GrowCapacity(previous.MaterializedPackedWordCapacity, requirements.PackedWordCount, geometric));
    private static int GrowCapacity(int current, int required, bool geometric) => required <= current ? current : Math.Max(required, geometric ? checked(current * 2) : current);
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal sealed partial class NativeGtrtSession : IDisposable
{
    private static readonly NativeLeaseAction<byte> RequestCancellationAction = RequestCancellationCore;
    private readonly NativeLeaseAction<byte> prepareRunAction;
    private readonly NativeLeaseAction<byte> initializeGameSnapshotAction;
    private readonly NativeLeaseAction<byte> prepareForDisposalAction;
    private NativeTransfer<byte>? storage;
    private long pendingSeed;
    private int pendingCenterChunkX;
    private int pendingCenterChunkY;
    private int pendingCenterChunkZ;
    private byte[]? pendingGameSnapshot;
    private bool runPrepared;
    private int publishSeedCalled;
    private bool disposalPrepared;
    private Action? publicationObserver;
    private NativeGtrtSession(NativeTransfer<byte>? source)
    {
        try
        {
            storage = NativeTransfer<byte>.Move(ref source);
            prepareRunAction = PrepareRunCore;
            initializeGameSnapshotAction = InitializeGameSnapshotCore;
            prepareForDisposalAction = PrepareForDisposalCore;
        }
        finally
        {
            source?.Dispose();
        }
    }

    internal static NativeGtrtSession Create(GameSettings settings, NativeTerrainMaterialSet materials, int generationWorkerCount = 1, int meshWorkerCount = 1) => Create(NativeGtrtSessionLayout.Create(settings, materials, generationWorkerCount, meshWorkerCount));
    internal static NativeGtrtSession Create(GameSettings settings, NativeGameSnapshot game, int generationWorkerCount = 1, int meshWorkerCount = 1, int materializedChunkCapacity = NativeGtrtSessionLayout.DefaultMaterializedChunkCapacity, int materializedSectionCapacity = NativeGtrtSessionLayout.DefaultMaterializedSectionCapacity, int materializedRawSectionCapacity = NativeGtrtSessionLayout.DefaultMaterializedSectionCapacity, int materializedPaletteCapacity = 0, int materializedPackedWordCapacity = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(game);
        byte[] gameSnapshot = game.CopyBytes();
        var gameView = new NativeGameSnapshotView(gameSnapshot);
        NativeGtrtSession session = Create(NativeGtrtSessionLayout.Create(settings, gameView.GetGeneratedMaterials(), generationWorkerCount, meshWorkerCount, gameSnapshot.Length, materializedChunkCapacity, materializedSectionCapacity, materializedRawSectionCapacity, materializedPaletteCapacity, materializedPackedWordCapacity));
        try
        {
            session.InitializeGameSnapshot(gameSnapshot);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal static NativeGtrtSession Create(NativeGtrtSessionLayout layout, NativeGameSnapshot game)
    {
        ArgumentNullException.ThrowIfNull(game);
        byte[] gameSnapshot = game.CopyBytes();
        if (layout.GameSnapshotByteCount != gameSnapshot.Length)
        {
            throw new ArgumentException("The native game snapshot capacity does not match the source.", nameof(layout));
        }

        NativeGtrtSession session = Create(layout);
        try
        {
            session.InitializeGameSnapshot(gameSnapshot);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal static NativeGtrtSession Create(NativeGtrtSessionLayout layout)
    {
        using NativeBuilder<byte> builder = new(preLease: layout.TotalByteCount);
        builder.Write<NativeGtrtSessionLayout>(layout.TotalByteCount, in layout, static (scoped NativeBuilderWriter<byte> writer, scoped in NativeGtrtSessionLayout current) =>
        {
            scoped NativeGtrtSessionInitializer initializer = new(writer.AsSpan());
            initializer.Initialize(in current);
            writer.Commit(current.TotalByteCount);
        });
        NativeTransfer<byte>? transfer = null;
        try
        {
            transfer = builder.Complete();
            return new NativeGtrtSession(NativeTransfer<byte>.Move(ref transfer));
        }
        finally
        {
            transfer?.Dispose();
        }
    }

    internal void PublishSeed(long seed)
    {
        if (Interlocked.Exchange(ref publishSeedCalled, 1) != 0)
        {
            throw new InvalidOperationException("The native GTRT seed is already published.");
        }

        PrepareRun(seed, 0, 0, 0);
    }

    internal void PrepareRun(long seed, int centerChunkX, int centerChunkY, int centerChunkZ)
    {
        pendingSeed = seed;
        pendingCenterChunkX = centerChunkX;
        pendingCenterChunkY = centerChunkY;
        pendingCenterChunkZ = centerChunkZ;
        runPrepared = false;
        ObjectDisposedException.ThrowIf(storage is null, this);
        storage.Access(prepareRunAction);
        if (!runPrepared)
        {
            throw new InvalidOperationException("The native GTRT session could not prepare its run.");
        }
    }

    private void InitializeGameSnapshot(byte[] gameSnapshot)
    {
        pendingGameSnapshot = gameSnapshot;
        try
        {
            ObjectDisposedException.ThrowIf(storage is null, this);
            storage.Access(initializeGameSnapshotAction);
        }
        finally
        {
            pendingGameSnapshot = null;
        }
    }

    internal void Access(NativeLeaseAction<byte> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(storage is null, this);
        storage.Access(action);
    }

    internal int OwnerLength => storage?.Length ?? 0;
    internal int OwnerCapacity => storage?.Capacity ?? 0;

    internal void ObservePublication(Action observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (publishSeedCalled != 0 || runPrepared)
            throw new InvalidOperationException("The publication observer must be prepared before the seed.");
        publicationObserver = observer;
    }

    internal void RequestCancellation()
    {
        if (storage is null)
            return;
        storage.Access(RequestCancellationAction);
    }

    public void Dispose()
    {
        if (storage is null)
            return;
        disposalPrepared = false;
        storage.Access(prepareForDisposalAction);
        if (!disposalPrepared)
        {
            throw new InvalidOperationException("The native GTRT session still has active mesh work.");
        }

        try
        {
            storage.Dispose();
        }
        finally
        {
            storage = null;
            try
            {
                pendingGrowthStorage?.Dispose();
            }
            finally
            {
                pendingGrowthStorage = null;
                try
                {
                    previousGrowthStorage?.Dispose();
                }
                finally
                {
                    previousGrowthStorage = null;
                }
            }
        }
    }

    private void PrepareRunCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        if (view.State.PublicationState == 0)
            publicationObserver?.Invoke();
        runPrepared = view.TryPrepareRun(pendingSeed, pendingCenterChunkX, pendingCenterChunkY, pendingCenterChunkZ);
    }

    private void InitializeGameSnapshotCore(scoped NativeLeaseView<byte> owner)
    {
        // This is the only borrow that writes the snapshot before publication.
        // Its post-copy access validates the completed bytes; subsequent borrows cache that view.
        var view = new NativeGtrtSessionView(owner.AsSpan(), gameSnapshotInitialization: true);
        if (view.State.PublicationState != 0 || pendingGameSnapshot is null || pendingGameSnapshot.Length != view.GameSnapshotBytes.Length)
        {
            throw new InvalidOperationException("The native game snapshot cannot be initialized.");
        }

        pendingGameSnapshot.CopyTo(view.GameSnapshotBytes);
        _ = view.GameSnapshot;
    }

    private static void RequestCancellationCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        view.RequestCancellation();
    }

    private void PrepareForDisposalCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        disposalPrepared = view.TryPrepareForDisposal();
    }
}

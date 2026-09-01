using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeGtrtSessionTests
{
    private static readonly NativeLeaseAction<byte> ExecuteSchedulerAction =
        ExecuteScheduler;

    [Fact]
    public void DenseLayoutMapsEveryResidentCoordinateExactlyOnce()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);

        Assert.Equal(2, layout.ResidentRadius);
        Assert.Equal(5, layout.ColumnWidth);
        Assert.Equal(3, layout.VerticalChunkCount);
        Assert.Equal(25, layout.ColumnCount);
        Assert.Equal(75, layout.ChunkCount);
        Assert.Equal(3, layout.RequiredColumnWidth);
        Assert.Equal(9, layout.RequiredColumnCount);
        Assert.Equal(27, layout.RequiredChunkCount);
        Assert.Equal(400, layout.ProfileCount);
        Assert.Equal(0, layout.GetColumnIndex(-2, -2));
        Assert.Equal(24, layout.GetColumnIndex(2, 2));
        Assert.Equal(0, layout.GetChunkIndex(-2, -1, -2));
        Assert.Equal(74, layout.GetChunkIndex(2, 1, 2));
        Assert.Equal(-1, layout.GetColumnIndex(-3, 0));
        Assert.Equal(-1, layout.GetChunkIndex(0, 2, 0));
    }

    [Fact]
    public void NativeSessionOwnsInitializedGridJobsProfilesAndSeed()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);
        var session = NativeGtrtSession.Create(layout);
        try
        {
            session.PublishSeed(123456);
            session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                Assert.Equal(123456, view.State.Seed);
                Assert.Equal(1, view.State.SessionEpoch);
                Assert.Equal(1, view.State.PublicationState);
                Assert.Equal(layout.ColumnCount, view.State.RemainingColumns);
                Assert.Equal(
                    layout.RequiredChunkCount,
                    view.State.RemainingChunks);
                Assert.Equal(layout.ColumnCount, view.Columns.Length);
                Assert.Equal(layout.ProfileCount, view.Profiles.Length);
                Assert.Equal(layout.ChunkCount, view.Chunks.Length);
                Assert.Equal(layout.ColumnCount, view.GenerationJobs.Length);
                Assert.Equal(layout.ChunkCount, view.MeshJobs.Length);
                Assert.Equal(layout.ChunkCount, view.Packets.Length);
                Assert.Equal(
                    layout.RequiredChunkCount,
                    view.MeshReadySlots.Length);

                for (int index = 0; index < view.Columns.Length; index++)
                {
                    NativeColumnRecord column = view.Columns[index];
                    Assert.Equal(index, view.GetColumnIndex(
                        column.ChunkX,
                        column.ChunkZ));
                    Assert.Equal(index * layout.ProfilesPerColumn,
                        column.ProfileOffset);
                    Assert.Equal(-1, column.BiomeIndex);
                    Assert.Equal(NativeColumnState.Empty, column.State);
                    Assert.Equal(index, view.GenerationJobs[index].RecordIndex);
                    Assert.Equal(
                        NativeWorkKind.GenerateColumn,
                        view.GenerationJobs[index].Kind);
                    Assert.Equal(
                        NativeWorkState.Scheduled,
                        view.GenerationJobs[index].State);
                }

                for (int index = 0; index < view.Chunks.Length; index++)
                {
                    NativeChunkRecord chunk = view.Chunks[index];
                    Assert.Equal(index, view.GetChunkIndex(
                        chunk.ChunkX,
                        chunk.ChunkY,
                        chunk.ChunkZ));
                    Assert.Equal(chunk.ColumnIndex * layout.ProfilesPerColumn,
                        chunk.ProfileOffset);
                    Assert.Equal(index, chunk.PacketIndex);
                    Assert.Equal(NativeChunkState.Empty, chunk.State);
                    Assert.Equal(index, view.MeshJobs[index].RecordIndex);
                    Assert.Equal(
                        NativeWorkKind.BuildChunkMesh,
                        view.MeshJobs[index].Kind);

                    bool required =
                        Math.Abs(chunk.ChunkX) <= layout.Lod1Radius &&
                        Math.Abs(chunk.ChunkZ) <= layout.Lod1Radius;
                    Assert.Equal(
                        required
                            ? NativeChunkFlags.InitialMeshRequired
                            : NativeChunkFlags.None,
                        (NativeChunkFlags)chunk.Flags);
                    Assert.Equal(
                        required ? 5 : 0,
                        chunk.RemainingDependencies);
                    Assert.Equal(
                        required
                            ? NativeWorkState.Waiting
                            : NativeWorkState.Canceled,
                        view.MeshJobs[index].State);
                }

                foreach (var profile in view.Profiles)
                {
                    Assert.Equal(-1, profile.StoneStart);
                    Assert.Equal(-1, profile.StoneEnd);
                    Assert.Equal(-1, profile.SoilStart);
                    Assert.Equal(-1, profile.SoilEnd);
                    Assert.Equal(-1, profile.WaterStart);
                    Assert.Equal(-1, profile.WaterEnd);
                }
            });

            Assert.Throws<InvalidOperationException>(() =>
                session.PublishSeed(123456));
        }
        finally
        {
            session.Dispose();
        }

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            session.Access(static _ => { }));
    }

    [Fact]
    public void ConcurrentWorkersClaimEveryNativeJobExactlyOnce()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);
        using var session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        int[] generationVisits = new int[layout.ColumnCount];
        RunWorkers(
            workerCount: 4,
            () => session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                while (view.TryClaimGeneration(out NativeWorkItem work))
                {
                    Interlocked.Increment(
                        ref generationVisits[work.RecordIndex]);
                    Assert.True(view.TryCompleteGeneration(in work));
                }
            }));

        int[] meshVisits = new int[layout.ChunkCount];
        RunWorkers(
            workerCount: 4,
            () => session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                while (view.TryClaimMesh(out NativeWorkItem work))
                {
                    Interlocked.Increment(
                        ref meshVisits[work.RecordIndex]);
                    Assert.True(view.TryCompleteMesh(in work));
                }
            }));

        Assert.All(generationVisits, count => Assert.Equal(1, count));
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.FailureCode);
            Assert.Equal(0, view.State.RemainingColumns);
            Assert.Equal(0, view.State.RemainingChunks);
            Assert.Equal(
                layout.RequiredChunkCount,
                view.State.ReadyPacketCount);

            for (int index = 0; index < view.Chunks.Length; index++)
            {
                NativeChunkRecord chunk = view.Chunks[index];
                bool required =
                    ((NativeChunkFlags)chunk.Flags).HasFlag(
                        NativeChunkFlags.InitialMeshRequired);
                Assert.Equal(required ? 1 : 0, meshVisits[index]);
                Assert.Equal(
                    required
                        ? NativeChunkState.PacketReady
                        : NativeChunkState.Generated,
                    chunk.State);
                Assert.Equal(
                    required
                        ? NativeWorkState.Completed
                        : NativeWorkState.Canceled,
                    view.MeshJobs[index].State);
            }
        });
    }

    [Fact]
    public void CancellationStopsNativeClaims()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);
        using var session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            view.RequestCancellation();
            Assert.Equal(1, view.State.CancellationState);
            Assert.False(view.TryClaimGeneration(out _));
            Assert.False(view.TryClaimMesh(out _));
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void StaleGenerationCompletionPublishesNativeFailure()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);
        using var session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.TryClaimGeneration(out NativeWorkItem work));
            work.Epoch++;
            Assert.False(view.TryCompleteGeneration(in work));
            Assert.Equal(
                (int)NativeGtrtFailureCode.InvalidGenerationCompletion,
                view.State.FailureCode);
        });
    }

    [Fact]
    public void NativeSchedulerKernelAllocatesNoManagedBytesAfterWarmup()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);
        using (var warmup = NativeGtrtSession.Create(layout))
        {
            warmup.PublishSeed(123456);
            warmup.Access(ExecuteSchedulerAction);
        }

        using var measured = NativeGtrtSession.Create(layout);
        measured.PublishSeed(123456);
        long before = GC.GetAllocatedBytesForCurrentThread();
        measured.Access(ExecuteSchedulerAction);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        measured.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.FailureCode);
            Assert.Equal(0, view.State.RemainingColumns);
            Assert.Equal(0, view.State.RemainingChunks);
        });
    }

    private static void ExecuteScheduler(
        scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        while (view.TryClaimGeneration(out NativeWorkItem generation))
            view.TryCompleteGeneration(in generation);

        while (view.TryClaimMesh(out NativeWorkItem mesh))
            view.TryCompleteMesh(in mesh);
    }

    private static void RunWorkers(
        int workerCount,
        Action action)
    {
        var workers = new Task[workerCount];
        for (int index = 0; index < workers.Length; index++)
            workers[index] = Task.Run(action);

        Task.WaitAll(workers);
    }
}

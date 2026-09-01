using System.Security.Cryptography;
using MVoxelEngine1.WorldGeneration.Native;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeGtrtSessionTests
{
    private static readonly NativeLeaseAction<byte> ExecuteSchedulerAction =
        ExecuteScheduler;
    private static readonly NativeLeaseAction<byte> ExecutePacketLifecycleAction =
        ExecutePacketLifecycle;

    [Fact]
    public void DenseLayoutMapsEveryResidentCoordinateExactlyOnce()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());

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
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
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
                Assert.Equal(
                    layout.ColumnCount,
                    view.ColumnSummaries.Length);
                Assert.Equal(
                    layout.GenerationWorkerCount,
                    view.GenerationWorkspaces.Length);
                Assert.Equal(
                    layout.GenerationFloatCountPerWorker,
                    view.GetGenerationFloatScratch(0).Length);
                Assert.Equal(
                    layout.ChunkSizeX,
                    view.GetGenerationXScratch(0).Length);
                Assert.Equal(
                    layout.ChunkSizeZ,
                    view.GetGenerationZScratch(0).Length);
                Assert.Equal(
                    layout.GenerationLatticeCountPerWorker,
                    view.GetGenerationLatticeScratch(0).Length);
                Assert.Equal(layout.ChunkCount, view.Chunks.Length);
                Assert.Equal(layout.ColumnCount, view.GenerationJobs.Length);
                Assert.Equal(layout.ChunkCount, view.MeshJobs.Length);
                Assert.Equal(
                    layout.MeshWorkerCount,
                    view.MeshWorkspaces.Length);
                Assert.Equal(
                    layout.ProfilesPerColumn,
                    view.GetMeshBottomFaceScratch(0).Length);
                Assert.Equal(
                    layout.ProfilesPerColumn,
                    view.GetMeshTopFaceScratch(0).Length);
                Assert.Equal(layout.ChunkCount, view.Packets.Length);
                Assert.Equal(
                    layout.PacketWordCapacity,
                    view.PacketWords.Length);
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
    public void GenerationWorkersOwnDisjointScratchPartitions()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            generationWorkerCount: 2);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.TryAcquireGenerationWorkspace(0));
            Assert.True(view.TryAcquireGenerationWorkspace(1));

            Span<float> firstValues =
                view.GetGenerationFloatScratch(0);
            Span<float> secondValues =
                view.GetGenerationFloatScratch(1);
            firstValues.Fill(11f);
            secondValues.Fill(22f);

            Span<TerrainGenerationUtils.NoiseAxisSample> firstX =
                view.GetGenerationXScratch(0);
            Span<TerrainGenerationUtils.NoiseAxisSample> secondX =
                view.GetGenerationXScratch(1);
            firstX.Fill(new TerrainGenerationUtils.NoiseAxisSample(31, 0.31f));
            secondX.Fill(new TerrainGenerationUtils.NoiseAxisSample(32, 0.32f));

            Span<TerrainGenerationUtils.NoiseAxisSample> firstZ =
                view.GetGenerationZScratch(0);
            Span<TerrainGenerationUtils.NoiseAxisSample> secondZ =
                view.GetGenerationZScratch(1);
            firstZ.Fill(new TerrainGenerationUtils.NoiseAxisSample(41, 0.41f));
            secondZ.Fill(new TerrainGenerationUtils.NoiseAxisSample(42, 0.42f));

            Span<float> firstLattice =
                view.GetGenerationLatticeScratch(0);
            Span<float> secondLattice =
                view.GetGenerationLatticeScratch(1);
            firstLattice.Fill(51f);
            secondLattice.Fill(52f);

            Assert.All(firstValues.ToArray(), value => Assert.Equal(11f, value));
            Assert.All(secondValues.ToArray(), value => Assert.Equal(22f, value));
            Assert.All(firstX.ToArray(), value => Assert.Equal(31, value.Grid));
            Assert.All(secondX.ToArray(), value => Assert.Equal(32, value.Grid));
            Assert.All(firstZ.ToArray(), value => Assert.Equal(41, value.Grid));
            Assert.All(secondZ.ToArray(), value => Assert.Equal(42, value.Grid));
            Assert.All(firstLattice.ToArray(), value => Assert.Equal(51f, value));
            Assert.All(secondLattice.ToArray(), value => Assert.Equal(52f, value));

            view.ReleaseGenerationWorkspace(0);
            view.ReleaseGenerationWorkspace(1);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void MeshWorkersOwnDisjointScratchAndExactPacketRanges()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            generationWorkerCount: 1,
            meshWorkerCount: 2,
            packetWordCapacity: 64);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.TryAcquireMeshWorkspace(0));
            Assert.True(view.TryAcquireMeshWorkspace(1));
            view.GetMeshBottomFaceScratch(0).Fill(11);
            view.GetMeshTopFaceScratch(0).Fill(12);
            view.GetMeshBottomFaceScratch(1).Fill(21);
            view.GetMeshTopFaceScratch(1).Fill(22);
            Assert.All(
                view.GetMeshBottomFaceScratch(0).ToArray(),
                value => Assert.Equal(11, value));
            Assert.All(
                view.GetMeshTopFaceScratch(0).ToArray(),
                value => Assert.Equal(12, value));
            Assert.All(
                view.GetMeshBottomFaceScratch(1).ToArray(),
                value => Assert.Equal(21, value));
            Assert.All(
                view.GetMeshTopFaceScratch(1).ToArray(),
                value => Assert.Equal(22, value));
            view.ReleaseMeshWorkspace(0);
            view.ReleaseMeshWorkspace(1);

            while (view.TryClaimGeneration(out NativeWorkItem generation))
                Assert.True(view.TryCompleteGeneration(in generation));

            Assert.True(view.TryClaimMesh(out NativeWorkItem first));
            Assert.True(view.TryBeginPacket(
                in first,
                opaqueWordCount: 4,
                opaqueFaceCount: 2,
                transparentWordCount: 2,
                transparentFaceCount: 1,
                out NativePacketWriteView firstPacket));
            firstPacket.OpaqueWords.Fill(101);
            firstPacket.TransparentWords.Fill(102);
            Assert.True(view.TryCompleteMesh(in first));

            Assert.True(view.TryClaimMesh(out NativeWorkItem second));
            Assert.True(view.TryBeginPacket(
                in second,
                opaqueWordCount: 2,
                opaqueFaceCount: 1,
                transparentWordCount: 4,
                transparentFaceCount: 2,
                out NativePacketWriteView secondPacket));
            secondPacket.OpaqueWords.Fill(201);
            secondPacket.TransparentWords.Fill(202);
            Assert.True(view.TryCompleteMesh(in second));

            Assert.True(view.TryReadPacket(
                first.RecordIndex,
                out NativePacketReadView firstRead));
            Assert.Equal(
                first.Epoch,
                view.Chunks[first.RecordIndex].MeshEpoch);
            Assert.Equal(0, firstRead.Record.OpaqueWordOffset);
            Assert.Equal(4, firstRead.Record.TransparentWordOffset);
            Assert.True(firstRead.OpaqueWords.SequenceEqual(
                stackalloc uint[] { 101, 101, 101, 101 }));
            Assert.True(firstRead.TransparentWords.SequenceEqual(
                stackalloc uint[] { 102, 102 }));

            Assert.True(view.TryReadPacket(
                second.RecordIndex,
                out NativePacketReadView secondRead));
            Assert.Equal(
                second.Epoch,
                view.Chunks[second.RecordIndex].MeshEpoch);
            Assert.Equal(6, secondRead.Record.OpaqueWordOffset);
            Assert.Equal(8, secondRead.Record.TransparentWordOffset);
            Assert.True(secondRead.OpaqueWords.SequenceEqual(
                stackalloc uint[] { 201, 201 }));
            Assert.True(secondRead.TransparentWords.SequenceEqual(
                stackalloc uint[] { 202, 202, 202, 202 }));
            Assert.Equal(12, view.State.PacketWordCursor);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void PacketStorageRecyclesOnlyAfterExclusiveRetirement()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            packetWordCapacity: 64);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            CompleteGeneration(ref view);

            Assert.True(view.TryClaimMesh(out NativeWorkItem first));
            Assert.True(view.TryBeginPacket(
                in first,
                opaqueWordCount: 4,
                opaqueFaceCount: 2,
                transparentWordCount: 2,
                transparentFaceCount: 1,
                out NativePacketWriteView firstWrite));
            firstWrite.OpaqueWords.Fill(101);
            firstWrite.TransparentWords.Fill(102);
            Assert.True(view.TryCompleteMesh(in first));
            Assert.Equal(1, view.State.ReadyPacketCount);

            Assert.True(view.TryActivatePacket(
                first.RecordIndex,
                out NativePacketReadView firstRead));
            Assert.True(firstRead.OpaqueWords.SequenceEqual(
                stackalloc uint[] { 101, 101, 101, 101 }));
            Assert.True(firstRead.TransparentWords.SequenceEqual(
                stackalloc uint[] { 102, 102 }));
            Assert.False(view.TryActivatePacket(first.RecordIndex, out _));
            Assert.False(view.TryRecyclePacketStorage());
            Assert.Equal(6, view.State.PacketWordCursor);

            Assert.True(view.TryRetirePacket(first.RecordIndex));
            Assert.False(view.TryRetirePacket(first.RecordIndex));
            Assert.Equal(0, view.State.ReadyPacketCount);
            Assert.True(view.TryRecyclePacketStorage());
            Assert.Equal(0, view.State.PacketWordCursor);
            Assert.Equal(
                NativeRenderPacketState.Empty,
                view.Packets[first.RecordIndex].State);

            Assert.True(view.TryClaimMesh(out NativeWorkItem second));
            Assert.True(view.TryBeginPacket(
                in second,
                opaqueWordCount: 2,
                opaqueFaceCount: 1,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out _));
            Assert.Equal(
                0,
                view.Packets[second.RecordIndex].OpaqueWordOffset);
            Assert.True(view.TryAbandonMesh(in second));
            Assert.True(view.TryRecyclePacketStorage());
            Assert.Equal(0, view.State.PacketWordCursor);
            Assert.Equal(0, view.State.ClaimedMeshCount);
            Assert.Equal(0, view.State.PacketConsumerCount);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void ReadyPacketCanRetireBeforeUpload()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            packetWordCapacity: 64);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            CompleteGeneration(ref view);
            Assert.True(view.TryClaimMesh(out NativeWorkItem work));
            Assert.True(view.TryBeginPacket(
                in work,
                opaqueWordCount: 2,
                opaqueFaceCount: 1,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out _));
            Assert.True(view.TryCompleteMesh(in work));

            Assert.True(view.TryRetirePacket(work.RecordIndex));
            Assert.Equal(
                NativeChunkState.Retired,
                view.Chunks[work.RecordIndex].State);
            Assert.Equal(
                NativeRenderPacketState.Retired,
                view.Packets[work.RecordIndex].State);
            Assert.Equal(0, view.State.ReadyPacketCount);
            Assert.True(view.TryRecyclePacketStorage());
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void CancellationAbandonsUnpublishedPacketAndReclaimsStorage()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            packetWordCapacity: 64);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            CompleteGeneration(ref view);
            Assert.True(view.TryClaimMesh(out NativeWorkItem work));
            Assert.True(view.TryBeginPacket(
                in work,
                opaqueWordCount: 4,
                opaqueFaceCount: 2,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out NativePacketWriteView packet));
            packet.OpaqueWords.Fill(211);

            view.RequestCancellation();
            Assert.True(view.TryAbandonMesh(in work));
            Assert.Equal(
                NativeWorkState.Canceled,
                view.MeshJobs[work.RecordIndex].State);
            Assert.Equal(
                NativeChunkState.Retired,
                view.Chunks[work.RecordIndex].State);
            Assert.Equal(
                NativeRenderPacketState.Retired,
                view.Packets[work.RecordIndex].State);
            Assert.Equal(0, view.State.ClaimedMeshCount);
            Assert.True(view.TryRecyclePacketStorage());
            Assert.Equal(0, view.State.PacketWordCursor);
            Assert.False(view.TryClaimMesh(out _));
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void ExhaustedPacketReservationRetiresBeforeCleanup()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            packetWordCapacity: 2);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            CompleteGeneration(ref view);
            Assert.True(view.TryClaimMesh(out NativeWorkItem work));
            Assert.False(view.TryBeginPacket(
                in work,
                opaqueWordCount: 4,
                opaqueFaceCount: 2,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out _));
            Assert.Equal(
                NativeRenderPacketState.Retired,
                view.Packets[work.RecordIndex].State);
            Assert.Equal(0, view.State.PacketWordCursor);
            Assert.True(view.TryAbandonMesh(in work));
            Assert.True(view.TryRecyclePacketStorage());
            Assert.Equal(
                (int)NativeGtrtFailureCode.PacketStorageExhausted,
                view.State.FailureCode);
        });
    }

    [Fact]
    public void PacketLifecycleKernelAllocatesNoManagedBytesAfterWarmup()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            packetWordCapacity: 64);
        using (NativeGtrtSession warmup = NativeGtrtSession.Create(layout))
        {
            warmup.PublishSeed(123456);
            warmup.Access(ExecutePacketLifecycleAction);
        }

        using NativeGtrtSession measured = NativeGtrtSession.Create(layout);
        measured.PublishSeed(123456);
        long before = GC.GetAllocatedBytesForCurrentThread();
        measured.Access(ExecutePacketLifecycleAction);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        measured.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.FailureCode);
            Assert.Equal(0, view.State.PacketWordCursor);
            Assert.Equal(0, view.State.ClaimedMeshCount);
            Assert.Equal(0, view.State.PacketConsumerCount);
        });
    }

    [Fact]
    public void DisposalRetiresReadyPacketsAfterActiveReaderReleases()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            packetWordCapacity: 64);
        NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);
        NativeWorkItem active = default;

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            CompleteGeneration(ref view);

            Assert.True(view.TryClaimMesh(out active));
            Assert.True(view.TryBeginPacket(
                in active,
                opaqueWordCount: 2,
                opaqueFaceCount: 1,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out _));
            Assert.True(view.TryCompleteMesh(in active));
            Assert.True(view.TryActivatePacket(active.RecordIndex, out _));

            Assert.True(view.TryClaimMesh(out NativeWorkItem ready));
            Assert.True(view.TryBeginPacket(
                in ready,
                opaqueWordCount: 2,
                opaqueFaceCount: 1,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out _));
            Assert.True(view.TryCompleteMesh(in ready));
            Assert.Equal(1, view.State.ReadyPacketCount);
        });

        Assert.Throws<InvalidOperationException>(() => session.Dispose());
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.CancellationRequested);
            Assert.True(view.TryRetirePacket(active.RecordIndex));
        });
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            session.Access(static _ => { }));
    }

    [Fact]
    public void DisposalRejectsWriterUntilClaimedWorkIsAbandoned()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            packetWordCapacity: 64);
        NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);
        NativeWorkItem claimed = default;

        try
        {
            session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                CompleteGeneration(ref view);
                Assert.True(view.TryClaimMesh(out claimed));
                Assert.True(view.TryBeginPacket(
                    in claimed,
                    opaqueWordCount: 2,
                    opaqueFaceCount: 1,
                    transparentWordCount: 0,
                    transparentFaceCount: 0,
                    out _));
            });

            Assert.Throws<InvalidOperationException>(() => session.Dispose());
            session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                Assert.True(view.CancellationRequested);
                Assert.True(view.TryAbandonMesh(in claimed));
                Assert.Equal(0, view.State.ClaimedMeshCount);
            });
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                session.Access(static _ => { }));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void DisposalRejectsClaimedGenerationUntilWorkerRetires()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
        NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);
        NativeWorkItem claimed = default;

        try
        {
            session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                Assert.True(view.TryClaimGeneration(out claimed));
            });

            Assert.Throws<InvalidOperationException>(() => session.Dispose());
            session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                Assert.True(view.CancellationRequested);
                Assert.True(view.TryAbandonGeneration(in claimed));
                Assert.Equal(0, view.State.ClaimedGenerationCount);
            });
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                session.Access(static _ => { }));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void SeedPublicationInitializesNativeNoiseWithoutManagedAllocation()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
        using (NativeGtrtSession warmup = NativeGtrtSession.Create(layout))
            warmup.PublishSeed(123456);

        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        long before = GC.GetAllocatedBytesForCurrentThread();
        session.PublishSeed(123456);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            NativeOpenSimplexNoiseState noise = view.NoiseState;
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(noise.EvaluationTables, digest);

            Assert.Equal(
                "AE99990D9640F33AF465A7DE4CE9B205748A4F860DB591538AD2A51A191D7C34",
                Convert.ToHexString(digest));
            Assert.Equal(
                0x3FCA7AC069022666UL,
                BitConverter.DoubleToUInt64Bits(
                    noise.Evaluate2D(-0.125, -17.5)));
        });
    }

    [Fact]
    public void ConcurrentWorkersClaimEveryNativeJobExactlyOnce()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
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
                    Assert.True(view.TryBeginPacket(
                        in work,
                        opaqueWordCount: 0,
                        opaqueFaceCount: 0,
                        transparentWordCount: 0,
                        transparentFaceCount: 0,
                        out _));
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
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
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
    public void CancellationAbandonsClaimedGenerationWork()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.TryClaimGeneration(out NativeWorkItem work));
            view.RequestCancellation();
            Assert.True(view.TryAbandonGeneration(in work));
            Assert.Equal(
                NativeWorkState.Canceled,
                view.GenerationJobs[work.RecordIndex].State);
            Assert.Equal(
                NativeColumnState.Retired,
                view.Columns[work.RecordIndex].State);
            Assert.Equal(0, view.State.ClaimedGenerationCount);
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
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
        using var session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.TryClaimGeneration(out NativeWorkItem work));
            NativeWorkItem stale = work;
            stale.Epoch++;
            Assert.False(view.TryCompleteGeneration(in stale));
            Assert.Equal(
                (int)NativeGtrtFailureCode.InvalidGenerationCompletion,
                view.State.FailureCode);
            Assert.True(view.TryAbandonGeneration(in work));
            Assert.Equal(0, view.State.ClaimedGenerationCount);
        });
    }

    [Fact]
    public void NativeSchedulerKernelAllocatesNoManagedBytesAfterWarmup()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
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
        {
            view.TryBeginPacket(
                in mesh,
                opaqueWordCount: 0,
                opaqueFaceCount: 0,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out _);
            view.TryCompleteMesh(in mesh);
        }
    }

    private static void ExecutePacketLifecycle(
        scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        CompleteGeneration(ref view);
        if (!view.TryClaimMesh(out NativeWorkItem work) ||
            !view.TryBeginPacket(
                in work,
                opaqueWordCount: 2,
                opaqueFaceCount: 1,
                transparentWordCount: 0,
                transparentFaceCount: 0,
                out NativePacketWriteView packet))
        {
            return;
        }

        packet.OpaqueWords[0] = 1;
        packet.OpaqueWords[1] = 2;
        if (!view.TryCompleteMesh(in work) ||
            !view.TryActivatePacket(work.RecordIndex, out _) ||
            !view.TryRetirePacket(work.RecordIndex))
        {
            return;
        }

        view.TryRecyclePacketStorage();
    }

    private static void CompleteGeneration(
        scoped ref NativeGtrtSessionView view)
    {
        while (view.TryClaimGeneration(out NativeWorkItem work))
            view.TryCompleteGeneration(in work);
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

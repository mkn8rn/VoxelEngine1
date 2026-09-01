using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeGeneratedMeshTests
{
    [Fact]
    public void ResidentNeighborsSuppressOpaqueAndWaterSideFaces()
    {
        var harness = new NativeGeneratedMeshHarness();
        using NativeGtrtSession session = CreateSession();
        session.PublishSeed(123456);
        session.Access(harness.PrepareAction);
        session.Access(harness.BuildAction);

        Assert.True(harness.Succeeded);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.TryReadPacket(
                harness.ClaimedMesh.RecordIndex,
                out NativePacketReadView packet));
            Assert.False(packet.OpaqueWords.IsEmpty);
            Assert.False(packet.TransparentWords.IsEmpty);
            AssertHasOnlyHorizontalFaces(packet.OpaqueWords);
            AssertHasOnlyHorizontalFaces(packet.TransparentWords);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void MeshKernelAllocatesNoManagedBytesAfterWarmup()
    {
        var harness = new NativeGeneratedMeshHarness();
        using (NativeGtrtSession warmup = CreateSession())
        {
            warmup.PublishSeed(123456);
            warmup.Access(harness.PrepareAction);
            warmup.Access(harness.BuildAction);
            Assert.True(harness.Succeeded);
        }

        using NativeGtrtSession measured = CreateSession();
        measured.PublishSeed(123456);
        measured.Access(harness.PrepareAction);
        harness.Succeeded = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        measured.Access(harness.BuildAction);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(harness.Succeeded);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void CanceledMeshBuildAbandonsItsNativePacketAuthority()
    {
        var harness = new NativeGeneratedMeshHarness();
        using NativeGtrtSession session = CreateSession();
        session.PublishSeed(123456);
        session.Access(harness.PrepareAction);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            view.RequestCancellation();
        });

        session.Access(harness.BuildAction);

        Assert.False(harness.Succeeded);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int chunkIndex = harness.ClaimedMesh.RecordIndex;
            Assert.Equal(
                NativeWorkState.Canceled,
                view.MeshJobs[chunkIndex].State);
            Assert.Equal(
                NativeChunkState.Retired,
                view.Chunks[chunkIndex].State);
            Assert.Equal(
                NativeRenderPacketState.Empty,
                view.Packets[chunkIndex].State);
            Assert.Equal(0, view.State.ClaimedMeshCount);
            Assert.True(view.TryRecyclePacketStorage());
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    private static NativeGtrtSession CreateSession()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 16,
            chunkSizeY: 16,
            chunkSizeZ: 16,
            lod1Radius: 0,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            generationWorkerCount: 1,
            meshWorkerCount: 1,
            packetWordCapacity: 32_768);
        return NativeGtrtSession.Create(layout);
    }

    private static void AssertHasOnlyHorizontalFaces(
        ReadOnlySpan<uint> words)
    {
        var reader = new PackedFaceRectangleReader(words);
        int rectangleCount = 0;
        while (reader.MoveNext())
        {
            Assert.True(reader.Direction is 2 or 3);
            rectangleCount++;
        }

        Assert.True(rectangleCount > 0);
    }

    private sealed class NativeGeneratedMeshHarness
    {
        private static readonly BlockColumnProfile Profile = new()
        {
            StoneStart = 0,
            StoneEnd = 4,
            SoilStart = 5,
            SoilEnd = 7,
            WaterStart = 8,
            WaterEnd = 9
        };

        internal NativeGeneratedMeshHarness()
        {
            PrepareAction = Prepare;
            BuildAction = Build;
        }

        internal NativeLeaseAction<byte> PrepareAction { get; }

        internal NativeLeaseAction<byte> BuildAction { get; }

        internal NativeWorkItem ClaimedMesh { get; private set; }

        internal bool Succeeded { get; set; }

        private void Prepare(scoped NativeLeaseView<byte> owner)
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            while (view.TryClaimGeneration(out NativeWorkItem generation))
            {
                view.GetColumnProfiles(generation.RecordIndex).Fill(Profile);
                view.Columns[generation.RecordIndex].GenerationEpoch =
                    generation.Epoch;
                if (!view.TryCompleteGeneration(in generation))
                {
                    Succeeded = false;
                    return;
                }
            }

            Succeeded = view.TryClaimMesh(out NativeWorkItem mesh);
            ClaimedMesh = mesh;
        }

        private void Build(scoped NativeLeaseView<byte> owner)
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            NativeWorkItem claimedMesh = ClaimedMesh;
            Succeeded = NativeGeneratedMesh.TryBuild(
                ref view,
                in claimedMesh,
                workerIndex: 0);
        }
    }
}

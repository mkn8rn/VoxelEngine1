using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeGeneratedMeshTests
{
    private const ushort OtherTransparentBlockId = 256;
    private const ushort CustomTransparentBlockId = 257;

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

    [Theory]
    [InlineData(CustomTransparentBlockId, 5)]
    [InlineData(OtherTransparentBlockId, 6)]
    public void MaterializedTransparentBorderMeshMatchesNaiveFaces(
        int neighborBlockId,
        int expectedTransparentFaceCount)
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        var harness = new NativeMaterializedMeshHarness(
            NativeMaterializedMeshMode.FullBorder,
            checked((ushort)neighborBlockId));
        using NativeGtrtSession session = CreateMaterializedSession(game);
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
            AssertPacketMatchesNaiveFaces(
                ref view,
                harness.ClaimedMesh.RecordIndex,
                in packet);
            Assert.Equal(0, packet.Record.OpaqueFaceCount);
            Assert.Equal(
                expectedTransparentFaceCount,
                packet.Record.TransparentFaceCount);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void HybridRuntimeEditMeshMatchesNaiveFaces()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        var harness = new NativeMaterializedMeshHarness(
            NativeMaterializedMeshMode.HybridEdit,
            neighborBlockId: 0);
        using NativeGtrtSession session = CreateMaterializedSession(game);
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
            AssertPacketMatchesNaiveFaces(
                ref view,
                harness.ClaimedMesh.RecordIndex,
                in packet);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void GeneratedChunkMeshObservesMaterializedNeighbor()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        var harness = new NativeMaterializedMeshHarness(
            NativeMaterializedMeshMode.GeneratedBesideMaterializedAir,
            neighborBlockId: 0);
        using NativeGtrtSession session = CreateMaterializedSession(game);
        session.PublishSeed(123456);
        session.Access(harness.PrepareAction);
        session.Access(harness.BuildAction);

        Assert.True(harness.Succeeded);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int center = harness.ClaimedMesh.RecordIndex;
            int right = view.GetChunkIndex(1, 0, 0);
            Assert.Equal(
                NativeChunkStorageKind.GeneratedProfile,
                view.Chunks[center].StorageKind);
            Assert.Equal(
                NativeChunkStorageKind.MaterializedSections,
                view.Chunks[right].StorageKind);
            Assert.True(view.TryReadPacket(
                center,
                out NativePacketReadView packet));
            AssertPacketMatchesNaiveFaces(
                ref view,
                center,
                in packet);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void MaterializedMeshAllocatesNoManagedBytesAfterWarmup()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        var harness = new NativeMaterializedMeshHarness(
            NativeMaterializedMeshMode.HybridEdit,
            neighborBlockId: 0);
        using (NativeGtrtSession warmup = CreateMaterializedSession(game))
        {
            warmup.PublishSeed(123456);
            warmup.Access(harness.PrepareAction);
            warmup.Access(harness.BuildAction);
            Assert.True(harness.Succeeded);
        }

        using NativeGtrtSession measured = CreateMaterializedSession(game);
        measured.PublishSeed(123456);
        measured.Access(harness.PrepareAction);
        harness.Succeeded = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        measured.Access(harness.BuildAction);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(harness.Succeeded);
        Assert.Equal(0, allocated);
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

    private static NativeGtrtSession CreateMaterializedSession(
        NativeGameSnapshot game)
    {
        byte[] snapshotBytes = game.CopyBytes();
        var gameView = new NativeGameSnapshotView(snapshotBytes);
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 16,
            chunkSizeY: 16,
            chunkSizeZ: 16,
            lod1Radius: 0,
            materials: gameView.GetGeneratedMaterials(),
            generationWorkerCount: 1,
            meshWorkerCount: 1,
            packetWordCapacity: 65_536,
            gameSnapshotByteCount: snapshotBytes.Length,
            materializedChunkCapacity: 2,
            materializedSectionCapacity: 2);
        return NativeGtrtSession.Create(layout, game);
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

    private static void AssertPacketMatchesNaiveFaces(
        scoped ref NativeGtrtSessionView view,
        int chunkIndex,
        scoped in NativePacketReadView packet)
    {
        var expectedOpaque = new List<ulong>();
        var expectedTransparent = new List<ulong>();
        for (int x = 0; x < view.ChunkSizeX; x++)
        {
            for (int y = 0; y < view.ChunkSizeY; y++)
            {
                for (int z = 0; z < view.ChunkSizeZ; z++)
                {
                    Assert.True(NativeGeneratedTerrain.TryGetBlock(
                        ref view,
                        chunkIndex,
                        x,
                        y,
                        z,
                        out ushort blockId));
                    if (blockId == 0)
                        continue;
                    Assert.True(view.TryGetBlockDescriptor(
                        blockId,
                        out NativeBlockDescriptor descriptor));
                    List<ulong> destination =
                        (descriptor.Flags & NativeBlockFlags.Opaque) != 0
                            ? expectedOpaque
                            : expectedTransparent;
                    for (byte direction = 0;
                         direction < 6;
                         direction++)
                    {
                        Assert.True(NativeGeneratedTerrain.TryIsFaceVisible(
                            ref view,
                            chunkIndex,
                            x,
                            y,
                            z,
                            direction,
                            out bool visible));
                        if (visible)
                        {
                            destination.Add(PackFaceIdentity(
                                x,
                                y,
                                z,
                                direction,
                                descriptor.GetTile(direction)));
                        }
                    }
                }
            }
        }

        var actualOpaque = new List<ulong>();
        var actualTransparent = new List<ulong>();
        AddPacketFaces(packet.OpaqueWords, actualOpaque);
        AddPacketFaces(packet.TransparentWords, actualTransparent);
        expectedOpaque.Sort();
        expectedTransparent.Sort();
        actualOpaque.Sort();
        actualTransparent.Sort();
        Assert.Equal(expectedOpaque, actualOpaque);
        Assert.Equal(expectedTransparent, actualTransparent);
        Assert.Equal(expectedOpaque.Count, packet.Record.OpaqueFaceCount);
        Assert.Equal(
            expectedTransparent.Count,
            packet.Record.TransparentFaceCount);
    }

    private static void AddPacketFaces(
        ReadOnlySpan<uint> words,
        List<ulong> destination)
    {
        var reader = new PackedFaceRectangleReader(words);
        while (reader.MoveNext())
        {
            destination.Add(PackFaceIdentity(
                reader.X,
                reader.Y,
                reader.Z,
                reader.Direction,
                reader.TileIndex));
        }
    }

    private static ulong PackFaceIdentity(
        int x,
        int y,
        int z,
        byte direction,
        uint tileIndex) =>
        (uint)x |
        ((ulong)(uint)y << 8) |
        ((ulong)(uint)z << 16) |
        ((ulong)direction << 24) |
        ((ulong)tileIndex << 27);

    private static void LoadDefaultGame()
    {
        GameManager.Initialize(TestPaths.GameDataRoot);
        string game = GameManager.SelectGameFolder("Default");
        GameManager.LoadGameDefaultSettings(game);
        TerrainLoader.allBlockTypes.Clear();
        TerrainLoader.allBlockTypesByBaseType.Clear();
        TerrainLoader.allBlockTypesByIds.Clear();
        TerrainLoader.allBlockTypeObjects.Clear();
        _ = new TerrainLoader();
        BiomeManager.LoadAllBiomes();
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

    private enum NativeMaterializedMeshMode
    {
        FullBorder,
        HybridEdit,
        GeneratedBesideMaterializedAir
    }

    private sealed class NativeMaterializedMeshHarness
    {
        private static readonly BlockColumnProfile GeneratedProfile = new()
        {
            StoneStart = 0,
            StoneEnd = 4,
            SoilStart = 5,
            SoilEnd = 7,
            WaterStart = 8,
            WaterEnd = 9
        };

        private static readonly BlockColumnProfile EmptyProfile = new()
        {
            StoneStart = -1,
            StoneEnd = -1,
            SoilStart = -1,
            SoilEnd = -1,
            WaterStart = -1,
            WaterEnd = -1
        };

        private readonly NativeMaterializedMeshMode mode;
        private readonly ushort neighborBlockId;

        internal NativeMaterializedMeshHarness(
            NativeMaterializedMeshMode mode,
            ushort neighborBlockId)
        {
            this.mode = mode;
            this.neighborBlockId = neighborBlockId;
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
            BlockColumnProfile profile = mode ==
                NativeMaterializedMeshMode.FullBorder
                    ? EmptyProfile
                    : GeneratedProfile;
            while (view.TryClaimGeneration(out NativeWorkItem generation))
            {
                view.GetColumnProfiles(generation.RecordIndex).Fill(profile);
                view.Columns[generation.RecordIndex].GenerationEpoch =
                    generation.Epoch;
                if (!view.TryCompleteGeneration(in generation))
                {
                    Succeeded = false;
                    return;
                }
            }

            int center = view.GetChunkIndex(0, 0, 0);
            if (mode == NativeMaterializedMeshMode.FullBorder)
            {
                int right = view.GetChunkIndex(1, 0, 0);
                Succeeded =
                    NativeMaterializedTerrain.TryMakeChunkMaterialized(
                        ref view,
                        center) &&
                    NativeMaterializedTerrain.TryMakeChunkMaterialized(
                        ref view,
                        right) &&
                    NativeMaterializedTerrain.TrySetBlock(
                        ref view,
                        center,
                        15,
                        8,
                        1,
                        CustomTransparentBlockId) &&
                    NativeMaterializedTerrain.TrySetBlock(
                        ref view,
                        right,
                        0,
                        8,
                        1,
                        neighborBlockId);
            }
            else if (mode == NativeMaterializedMeshMode.HybridEdit)
            {
                Succeeded = NativeMaterializedTerrain.TrySetBlock(
                    ref view,
                    center,
                    2,
                    5,
                    3,
                    CustomTransparentBlockId);
            }
            else
            {
                int right = view.GetChunkIndex(1, 0, 0);
                Succeeded =
                    NativeMaterializedTerrain.TryMakeChunkMaterialized(
                        ref view,
                        right);
            }

            if (!Succeeded)
                return;
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

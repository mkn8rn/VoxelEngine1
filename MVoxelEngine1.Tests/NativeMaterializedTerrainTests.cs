using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeMaterializedTerrainTests
{
    private const ushort OtherTransparentBlockId = 256;
    private const ushort CustomTransparentBlockId = 257;
    private const ushort StoneId = (byte)BaseBlockType.Stone;

    [Fact]
    public void CompleteMaterializationStoresUniformSectionsWithoutRawVoxelReservation()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = CreateSession(game, 32, 1, 8, materializedRawSectionCapacity: 1);
        session.PublishSeed(123456);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            view.Profiles.Fill(new BlockColumnProfile
            {
                StoneStart = 0, StoneEnd = 31, SoilStart = -1, SoilEnd = -1,
                WaterStart = -1, WaterEnd = -1
            });
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            Assert.True(NativeMaterializedTerrain.TryGetEditStorageRequirements(
                ref view, chunkIndex, 2, 5, 3, out NativeMaterializedStorageRequirements requirements));
            Assert.Equal(new NativeMaterializedStorageRequirements(1, 8, 1), requirements);
            Assert.True(NativeMaterializedTerrain.TrySetBlock(ref view, chunkIndex, 2, 5, 3, CustomTransparentBlockId));
            Assert.True(NativeMaterializedTerrain.TryMaterializeCompleteChunk(ref view, chunkIndex));
            Assert.Equal(8, view.State.MaterializedSectionCount);
            Assert.Equal(1, view.State.MaterializedRawSectionCount);
            for (int index = 1; index < 8; index++)
            {
                Assert.Equal(NativeSectionStorageKind.Uniform, view.MaterializedSections[index].StorageKind);
                Assert.Equal(StoneId, view.MaterializedSections[index].UniformBlockId);
                Assert.Equal(-1, view.MaterializedSections[index].RawVoxelOffset);
            }
            for (int x = 0; x < 32; x++)
                for (int y = 0; y < 32; y++)
                    for (int z = 0; z < 32; z++)
                        AssertBlock(ref view, chunkIndex, x, y, z,
                            x == 2 && y == 5 && z == 3 ? CustomTransparentBlockId : StoneId);
        });
    }

    [Fact]
    public void HybridEditCopiesGeneratedSectionAndPreservesCustomBlock()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = CreateSession(
            game,
            chunkSize: 32,
            materializedChunkCapacity: 2,
            materializedSectionCapacity: 4);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            AssertBlock(ref view, chunkIndex, 2, 5, 3, SoilId);
            AssertBlock(ref view, chunkIndex, 2, 20, 3, WaterId);

            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                2,
                5,
                3,
                CustomTransparentBlockId));
            AssertBlock(
                ref view,
                chunkIndex,
                2,
                5,
                3,
                CustomTransparentBlockId);
            AssertBlock(ref view, chunkIndex, 2, 4, 3, SoilId);
            AssertBlock(ref view, chunkIndex, 2, 20, 3, WaterId);
            Assert.Equal(
                NativeChunkStorageKind.HybridSections,
                view.Chunks[chunkIndex].StorageKind);
            Assert.Equal(1, view.State.MaterializedChunkCount);
            Assert.Equal(1, view.State.MaterializedSectionCount);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void MaterializedChunkTreatsMissingSectionsAsAir()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = CreateSession(
            game,
            chunkSize: 32,
            materializedChunkCapacity: 2,
            materializedSectionCapacity: 4);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            Assert.True(NativeMaterializedTerrain.TrySetUniformSection(
                ref view,
                chunkIndex,
                sectionX: 0,
                sectionY: 0,
                sectionZ: 0,
                CustomTransparentBlockId,
                NativeChunkStorageKind.MaterializedSections));

            AssertBlock(
                ref view,
                chunkIndex,
                10,
                10,
                10,
                CustomTransparentBlockId);
            AssertBlock(ref view, chunkIndex, 10, 20, 10, 0);
            Assert.Equal(
                NativeChunkStorageKind.MaterializedSections,
                view.Chunks[chunkIndex].StorageKind);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void TransparentFacesUseExactMaterializedNeighborsAcrossChunkBorder()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = CreateSession(
            game,
            chunkSize: 16,
            materializedChunkCapacity: 2,
            materializedSectionCapacity: 2);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int left = view.GetChunkIndex(0, 0, 0);
            int right = view.GetChunkIndex(1, 0, 0);
            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                left,
                15,
                8,
                1,
                CustomTransparentBlockId));
            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                right,
                0,
                8,
                1,
                CustomTransparentBlockId));

            AssertFace(ref view, left, 15, 8, 1, direction: 1, false);
            AssertFace(ref view, right, 0, 8, 1, direction: 0, false);

            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                right,
                0,
                8,
                1,
                OtherTransparentBlockId));
            AssertFace(ref view, left, 15, 8, 1, direction: 1, true);
            AssertFace(ref view, right, 0, 8, 1, direction: 0, true);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void MaterializedOverrideSurvivesCameraMovement()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = CreateSession(
            game,
            chunkSize: 16,
            materializedChunkCapacity: 2,
            materializedSectionCapacity: 2);
        session.PublishSeed(123456);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                1,
                5,
                1,
                CustomTransparentBlockId));
        });

        session.PrepareRun(123456, 2, 0, 3);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            Assert.Equal(-1, view.GetChunkIndex(0, 0, 0));
            Assert.Equal(1, view.State.MaterializedChunkCount);
            view.CommitStreamingRun();
        });

        session.PrepareRun(123456, 0, 0, 0);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            AssertBlock(
                ref view,
                chunkIndex,
                1,
                5,
                1,
                CustomTransparentBlockId);
            Assert.Equal(0, view.Chunks[chunkIndex].MaterializedChunkIndex);
            Assert.Equal(
                NativeChunkStorageKind.HybridSections,
                view.Chunks[chunkIndex].StorageKind);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void InactiveVerticalOverrideParticipatesInBoundaryQuery()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = CreateSession(
            game,
            chunkSize: 16,
            materializedChunkCapacity: 2,
            materializedSectionCapacity: 2);
        session.PublishSeed(123456);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int upper = view.GetChunkIndex(0, 1, 0);
            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                upper,
                2,
                0,
                3,
                CustomTransparentBlockId));
        });

        session.PrepareRun(123456, 0, -1, 0);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            Assert.Equal(-1, view.GetChunkIndex(0, 1, 0));
            int source = view.GetChunkIndex(0, 0, 0);
            AssertBlock(
                ref view,
                source,
                2,
                16,
                3,
                CustomTransparentBlockId);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void MaterializedSectionCapacityFailsBeforePartialPublication()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = CreateSession(
            game,
            chunkSize: 32,
            materializedChunkCapacity: 1,
            materializedSectionCapacity: 1);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                1,
                5,
                1,
                CustomTransparentBlockId));
            Assert.False(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                1,
                20,
                1,
                CustomTransparentBlockId));
            Assert.Equal(
                (int)NativeGtrtFailureCode.MaterializedSectionStorageExhausted,
                view.State.FailureCode);
            Assert.Equal(1, view.State.MaterializedChunkCount);
            Assert.Equal(1, view.State.MaterializedSectionCount);
            Assert.Equal(-1, view.MaterializedSectionMaps[1]);
        });
    }

    [Fact]
    public void MaterializedEditAndQueryAllocateNoManagedBytesAfterWarmup()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        var harness = new MaterializedTerrainHarness();

        using (NativeGtrtSession warmup = CreateSession(
            game,
            chunkSize: 16,
            materializedChunkCapacity: 1,
            materializedSectionCapacity: 1))
        {
            warmup.PublishSeed(123456);
            warmup.Access(harness.PrepareAction);
            warmup.Access(harness.EditAction);
            warmup.Access(harness.QueryAction);
            Assert.True(harness.Succeeded);
        }

        using NativeGtrtSession measured = CreateSession(
            game,
            chunkSize: 16,
            materializedChunkCapacity: 1,
            materializedSectionCapacity: 1);
        measured.PublishSeed(123456);
        measured.Access(harness.PrepareAction);
        harness.Succeeded = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        measured.Access(harness.EditAction);
        measured.Access(harness.QueryAction);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(harness.Succeeded);
        Assert.Equal(CustomTransparentBlockId, harness.BlockId);
        Assert.Equal(0, allocated);
    }

    private static ushort SoilId => (byte)BaseBlockType.Soil;

    private static ushort WaterId => (byte)BaseBlockType.Water;

    private static NativeGtrtSession CreateSession(
        NativeGameSnapshot game,
        int chunkSize,
        int materializedChunkCapacity,
        int materializedSectionCapacity,
        int materializedRawSectionCapacity = -1)
    {
        byte[] snapshotBytes = game.CopyBytes();
        var gameView = new NativeGameSnapshotView(snapshotBytes);
        var layout = new NativeGtrtSessionLayout(
            chunkSize,
            chunkSize,
            chunkSize,
            lod1Radius: 1,
            gameView.GetGeneratedMaterials(),
            gameSnapshotByteCount: snapshotBytes.Length,
            materializedChunkCapacity: materializedChunkCapacity,
            materializedSectionCapacity: materializedSectionCapacity,
            materializedRawSectionCapacity: materializedRawSectionCapacity);
        return NativeGtrtSession.Create(layout, game);
    }

    private static void PrepareTerrain(
        scoped ref NativeGtrtSessionView view)
    {
        var profile = new BlockColumnProfile
        {
            StoneStart = 0,
            StoneEnd = 3,
            SoilStart = 4,
            SoilEnd = 7,
            WaterStart = 8,
            WaterEnd = 31
        };
        Span<NativeColumnRecord> columns = view.Columns;
        for (int columnIndex = 0;
             columnIndex < columns.Length;
             columnIndex++)
        {
            view.GetColumnProfiles(columnIndex).Fill(profile);
            columns[columnIndex].GenerationEpoch = view.State.SessionEpoch;
            columns[columnIndex].State = NativeColumnState.Generated;
        }
    }

    private static void AssertBlock(
        scoped ref NativeGtrtSessionView view,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        ushort expected)
    {
        Assert.True(NativeGeneratedTerrain.TryGetBlock(
            ref view,
            chunkIndex,
            localX,
            localY,
            localZ,
            out ushort actual));
        Assert.Equal(expected, actual);
    }

    private static void AssertFace(
        scoped ref NativeGtrtSessionView view,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        byte direction,
        bool expected)
    {
        Assert.True(NativeGeneratedTerrain.TryIsFaceVisible(
            ref view,
            chunkIndex,
            localX,
            localY,
            localZ,
            direction,
            out bool visible));
        Assert.Equal(expected, visible);
    }

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

    private sealed class MaterializedTerrainHarness
    {
        internal MaterializedTerrainHarness()
        {
            PrepareAction = Prepare;
            EditAction = Edit;
            QueryAction = Query;
        }

        internal NativeLeaseAction<byte> PrepareAction { get; }

        internal NativeLeaseAction<byte> EditAction { get; }

        internal NativeLeaseAction<byte> QueryAction { get; }

        internal bool Succeeded { get; set; }

        internal ushort BlockId { get; private set; }

        private static void Prepare(scoped NativeLeaseView<byte> owner)
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
        }

        private void Edit(scoped NativeLeaseView<byte> owner)
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            Succeeded = NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                2,
                5,
                3,
                CustomTransparentBlockId);
        }

        private void Query(scoped NativeLeaseView<byte> owner)
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            ushort blockId = 0;
            Succeeded = Succeeded && NativeGeneratedTerrain.TryGetBlock(
                ref view,
                chunkIndex,
                2,
                5,
                3,
                out blockId);
            BlockId = blockId;
        }
    }
}

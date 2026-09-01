using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Tests;

public sealed class NativeGtrtWorkerPoolTests
{
    [Fact]
    public void StagedWorkersDisposeExactlyOnceBeforeSeedPublication()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        using NativeGtrtSession session = NativeGtrtSession.Create(
            CreateSmallSettings(),
            game,
            generationWorkerCount: 2,
            meshWorkerCount: 2);
        var workers = new NativeGtrtWorkerPool(
            session,
            generationWorkerCount: 2,
            meshWorkerCount: 2,
            streamGeneration: false);

        workers.Dispose();
        workers.Dispose();
    }

    [Fact]
    public void HeadlessPipelineStopsAtNativePreUploadPacket()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        GameSettings settings = CreateSmallSettings();
        using NativeGtrtPipeline pipeline =
            NativeGtrtPipeline.Create(
                atlas,
                settings,
                generationWorkerCount: 3,
                meshWorkerCount: 3);

        NativePreUploadPacket packet = pipeline.Run(123456);

        Assert.True(packet.RenderDataId > 0);
        Assert.True(
            packet.OpaqueFaceCount +
            packet.TransparentFaceCount > 0);
        Assert.Equal(
            packet.OpaqueRectangleCount * 2,
            packet.OpaqueWordCount);
        Assert.Equal(
            packet.TransparentRectangleCount * 2,
            packet.TransparentWordCount);
        Assert.Equal(0, pipeline.MaximumWorkerManagedAllocationBytes);
        Assert.Equal(0, pipeline.CoordinatorManagedAllocationBytes);
    }

    [Fact]
    public void ReadyPacketsUseBoundedNativeViewsAndRetireExactlyOnce()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        GameSettings settings = CreateSmallSettings();
        using NativeGtrtPipeline pipeline =
            NativeGtrtPipeline.Create(
                atlas,
                settings,
                generationWorkerCount: 3,
                meshWorkerCount: 3);
        NativePreUploadPacket firstPacket = pipeline.Run(123456);
        int matchingPacketCount = 0;
        long observedWordCount = 0;

        int consumed = pipeline.ConsumeReadyPackets(
            (in NativeChunkRenderPacketDescriptor descriptor,
             ReadOnlySpan<uint> opaqueWords,
             ReadOnlySpan<uint> transparentWords) =>
            {
                Assert.Equal(0, descriptor.ChunkWorldX % settings.chunkMaxX);
                Assert.Equal(0, descriptor.ChunkWorldY % settings.chunkMaxY);
                Assert.Equal(0, descriptor.ChunkWorldZ % settings.chunkMaxZ);
                Assert.Equal(1, descriptor.RegistryEpoch);
                Assert.Equal(1, descriptor.PublicationEpoch);
                Assert.Equal(descriptor.OpaqueWordCount, opaqueWords.Length);
                Assert.Equal(
                    descriptor.TransparentWordCount,
                    transparentWords.Length);
                observedWordCount +=
                    opaqueWords.Length + transparentWords.Length;
                if (descriptor.RenderDataId == firstPacket.RenderDataId)
                    matchingPacketCount++;
            });

        Assert.Equal(27, consumed);
        Assert.Equal(1, matchingPacketCount);
        Assert.True(observedWordCount > 0);
        Assert.Throws<InvalidOperationException>(() =>
            pipeline.ConsumeReadyPackets(
                static (in NativeChunkRenderPacketDescriptor _,
                        ReadOnlySpan<uint> _,
                        ReadOnlySpan<uint> _) => { }));
    }

    [Fact]
    public void ConsumerFailureRetiresTheActivePacketBeforeDisposal()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(
            atlas,
            CreateSmallSettings(),
            generationWorkerCount: 2,
            meshWorkerCount: 2);
        pipeline.Run(123456);

        Assert.Throws<PacketConsumerFailure>(() =>
            pipeline.ConsumeReadyPackets(
                static (in NativeChunkRenderPacketDescriptor _,
                        ReadOnlySpan<uint> _,
                        ReadOnlySpan<uint> _) =>
                    throw new PacketConsumerFailure()));

        pipeline.Dispose();
        pipeline.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentWorkersMatchSequentialNativePackets(
        bool streamGeneration)
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot game = NativeGameSnapshot.Create(atlas);
        byte[] gameBytes = game.CopyBytes();
        var gameView = new NativeGameSnapshotView(gameBytes);

        var concurrentLayout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: gameView.GetGeneratedMaterials(),
            generationWorkerCount: 3,
            meshWorkerCount: 3,
            packetWordCapacity: 1_000_000,
            gameSnapshotByteCount: gameBytes.Length);
        using NativeGtrtSession concurrent =
            NativeGtrtSession.Create(concurrentLayout, game);
        using (var workers = new NativeGtrtWorkerPool(
                   concurrent,
                   generationWorkerCount: 3,
                   meshWorkerCount: 3,
                   streamGeneration: streamGeneration))
        {
            workers.Run(123456);
            Assert.Equal(0, workers.MaximumManagedAllocationBytes);
        }

        var sequentialLayout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: gameView.GetGeneratedMaterials(),
            generationWorkerCount: 1,
            meshWorkerCount: 1,
            packetWordCapacity: 1_000_000,
            gameSnapshotByteCount: gameBytes.Length);
        using NativeGtrtSession sequential =
            NativeGtrtSession.Create(sequentialLayout, game);
        RunSequential(sequential);

        Dictionary<int, PacketCopy> expected = CopyPackets(sequential);
        Dictionary<int, PacketCopy> actual = CopyPackets(concurrent);
        Assert.NotEmpty(actual);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((int chunkIndex, PacketCopy expectedPacket) in expected)
        {
            PacketCopy actualPacket = actual[chunkIndex];
            Assert.Equal(expectedPacket.RenderDataId, actualPacket.RenderDataId);
            Assert.Equal(expectedPacket.OpaqueFaceCount, actualPacket.OpaqueFaceCount);
            Assert.Equal(
                expectedPacket.TransparentFaceCount,
                actualPacket.TransparentFaceCount);
            Assert.Equal(expectedPacket.OpaqueWords, actualPacket.OpaqueWords);
            Assert.Equal(
                expectedPacket.TransparentWords,
                actualPacket.TransparentWords);
        }
    }

    private static void RunSequential(NativeGtrtSession session)
    {
        session.PublishSeed(123456);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            NativeGameSnapshotView game = view.GameSnapshot;
            while (view.TryClaimGeneration(out NativeWorkItem generation))
            {
                NativeColumnRecord column =
                    view.Columns[generation.RecordIndex];
                int biomeIndex = game.SelectBiomeIndex(
                    view.State.Seed,
                    column.ChunkX,
                    column.ChunkZ);
                NativeBiomeDescriptor biome = game.Biomes[biomeIndex];
                Assert.True(NativeColumnProfileGenerator.TryGenerate(
                    ref view,
                    workerIndex: 0,
                    in generation,
                    biomeIndex,
                    in biome));
                Assert.True(view.TryCompleteGeneration(in generation));
            }

            while (view.TryClaimMesh(out NativeWorkItem mesh))
            {
                Assert.True(NativeGeneratedMesh.TryBuild(
                    ref view,
                    in mesh,
                    workerIndex: 0));
            }

            Assert.Equal(0, view.State.FailureCode);
            Assert.Equal(0, view.State.RemainingColumns);
            Assert.Equal(0, view.State.RemainingChunks);
        });
    }

    private static Dictionary<int, PacketCopy> CopyPackets(
        NativeGtrtSession session)
    {
        var packets = new Dictionary<int, PacketCopy>();
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            for (int chunkIndex = 0;
                 chunkIndex < view.Chunks.Length;
                 chunkIndex++)
            {
                if (view.Chunks[chunkIndex].State !=
                        NativeChunkState.PacketReady)
                {
                    continue;
                }

                Assert.True(view.TryReadPacket(
                    chunkIndex,
                    out NativePacketReadView packet));
                packets.Add(
                    chunkIndex,
                    new PacketCopy(
                        packet.Record.RenderDataId,
                        packet.Record.OpaqueFaceCount,
                        packet.Record.TransparentFaceCount,
                        packet.OpaqueWords.ToArray(),
                        packet.TransparentWords.ToArray()));
            }
        });
        return packets;
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

    private static GameSettings CreateSmallSettings()
    {
        GameSettings source = GameManager.settings;
        return new GameSettings
        {
            chunkMaxX = 4,
            chunkMaxY = 8,
            chunkMaxZ = 4,
            blockTileWidth = source.blockTileWidth,
            blockTileHeight = source.blockTileHeight,
            textureFileExtension = source.textureFileExtension,
            renderStreamingAllowed = source.renderStreamingAllowed,
            lod1RenderDistance = 1,
            lod2RenderDistance = source.lod2RenderDistance,
            lod3RenderDistance = source.lod3RenderDistance,
            lod4RenderDistance = source.lod4RenderDistance,
            lod5RenderDistance = source.lod5RenderDistance,
            entityLoadRange = source.entityLoadRange,
            entitySpawnMaxRange = source.entitySpawnMaxRange,
            entityDespawnMaxRange = source.entityDespawnMaxRange,
            regionWidthInChunks = source.regionWidthInChunks,
            oneRegionWorld = source.oneRegionWorld,
            chunkGenerationBufferInitial =
                source.chunkGenerationBufferInitial,
            chunkGenerationBufferRuntime =
                source.chunkGenerationBufferRuntime,
            gameDataDirectory = source.gameDataDirectory,
            loadedGameDirectory = source.loadedGameDirectory,
            loadedGameSettingsDirectory =
                source.loadedGameSettingsDirectory,
            assetsBaseBlockTexturesDirectory =
                source.assetsBaseBlockTexturesDirectory,
            assetsBlockTexturesDirectory =
                source.assetsBlockTexturesDirectory,
            dataBlockTypesDirectory = source.dataBlockTypesDirectory,
            dataBiomeTypesDirectory = source.dataBiomeTypesDirectory,
            savesWorldDirectory = source.savesWorldDirectory,
            savesCharactersDirectory = source.savesCharactersDirectory
        };
    }

    private sealed record PacketCopy(
        long RenderDataId,
        int OpaqueFaceCount,
        int TransparentFaceCount,
        uint[] OpaqueWords,
        uint[] TransparentWords);

    private sealed class PacketConsumerFailure : Exception;
}

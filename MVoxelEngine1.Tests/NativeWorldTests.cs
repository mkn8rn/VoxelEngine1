using MVoxelEngine1.Graphics;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Tests;

public sealed class NativeWorldTests
{
    [Fact]
    public void InitialPacketsReplaceExactlyOnceAfterCameraMovement()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(
            atlas,
            CreateSmallSettings(),
            generationWorkerCount: 3,
            meshWorkerCount: 3,
            streamGeneration: true);
        var factory = new TrackingRendererFactory();
        using NativeWorld world = NativeWorld.CreateForTesting(
            pipeline,
            seed: 123456,
            factory.Create);

        Assert.Equal(27, world.RendererSlotCount);
        Assert.Equal(27, factory.CreatedCount);
        Assert.Equal(0, factory.DisposedCount);
        Assert.Equal((0, 0, 0), world.PlayerChunkPosition);

        world.PlayerChunkPosition = (2, -1, 3);

        Assert.Equal((2, -1, 3), world.PlayerChunkPosition);
        Assert.Equal(54, factory.CreatedCount);
        Assert.Equal(27, factory.DisposedCount);
        Assert.Equal(4, factory.MinimumCurrentWorldX);
        Assert.Equal(12, factory.MaximumCurrentWorldX);
        Assert.Equal(-16, factory.MinimumCurrentWorldY);
        Assert.Equal(0, factory.MaximumCurrentWorldY);
        Assert.Equal(8, factory.MinimumCurrentWorldZ);
        Assert.Equal(16, factory.MaximumCurrentWorldZ);
    }

    [Fact]
    public void FailedMovementDisposesOnlyTheIncompleteReplacement()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(
            atlas,
            CreateSmallSettings(),
            generationWorkerCount: 2,
            meshWorkerCount: 2,
            streamGeneration: true);
        var factory = new TrackingRendererFactory();
        NativeWorld world = NativeWorld.CreateForTesting(
            pipeline,
            seed: 123456,
            factory.Create);
        factory.FailAtAttempt = 32;

        Assert.Throws<RendererFactoryFailure>(() =>
            world.PlayerChunkPosition = (1, 0, 0));

        Assert.Equal((0, 0, 0), world.PlayerChunkPosition);
        Assert.Equal(31, factory.CreatedCount);
        Assert.Equal(4, factory.DisposedCount);

        world.Dispose();
        Assert.Equal(31, factory.DisposedCount);
        world.Dispose();
        Assert.Equal(31, factory.DisposedCount);
    }

    [Fact]
    public void InitialRendererFailureReleasesEveryPublishedRenderer()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(
            atlas,
            CreateSmallSettings(),
            generationWorkerCount: 2,
            meshWorkerCount: 2);
        var factory = new TrackingRendererFactory
        {
            FailAtAttempt = 6
        };

        Assert.Throws<RendererFactoryFailure>(() =>
            NativeWorld.CreateForTesting(
                pipeline,
                seed: 123456,
                factory.Create));

        Assert.Equal(5, factory.CreatedCount);
        Assert.Equal(5, factory.DisposedCount);
        pipeline.Dispose();
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

    private sealed class TrackingRendererFactory
    {
        internal int CreatedCount { get; private set; }

        internal int DisposedCount { get; private set; }

        internal int FailAtAttempt { get; set; } = int.MaxValue;

        internal int MinimumCurrentWorldX { get; private set; } = int.MaxValue;

        internal int MaximumCurrentWorldX { get; private set; } = int.MinValue;

        internal int MinimumCurrentWorldY { get; private set; } = int.MaxValue;

        internal int MaximumCurrentWorldY { get; private set; } = int.MinValue;

        internal int MinimumCurrentWorldZ { get; private set; } = int.MaxValue;

        internal int MaximumCurrentWorldZ { get; private set; } = int.MinValue;

        internal INativeChunkRenderer Create(
            in NativeChunkRenderPacketDescriptor descriptor,
            ReadOnlySpan<uint> opaqueWords,
            ReadOnlySpan<uint> transparentWords)
        {
            int attempt = CreatedCount + 1;
            if (attempt == FailAtAttempt)
                throw new RendererFactoryFailure();

            Assert.Equal(descriptor.OpaqueWordCount, opaqueWords.Length);
            Assert.Equal(
                descriptor.TransparentWordCount,
                transparentWords.Length);
            CreatedCount = attempt;
            if (attempt % 27 == 1)
            {
                MinimumCurrentWorldX = int.MaxValue;
                MaximumCurrentWorldX = int.MinValue;
                MinimumCurrentWorldY = int.MaxValue;
                MaximumCurrentWorldY = int.MinValue;
                MinimumCurrentWorldZ = int.MaxValue;
                MaximumCurrentWorldZ = int.MinValue;
            }

            MinimumCurrentWorldX = Math.Min(
                MinimumCurrentWorldX,
                descriptor.ChunkWorldX);
            MaximumCurrentWorldX = Math.Max(
                MaximumCurrentWorldX,
                descriptor.ChunkWorldX);
            MinimumCurrentWorldY = Math.Min(
                MinimumCurrentWorldY,
                descriptor.ChunkWorldY);
            MaximumCurrentWorldY = Math.Max(
                MaximumCurrentWorldY,
                descriptor.ChunkWorldY);
            MinimumCurrentWorldZ = Math.Min(
                MinimumCurrentWorldZ,
                descriptor.ChunkWorldZ);
            MaximumCurrentWorldZ = Math.Max(
                MaximumCurrentWorldZ,
                descriptor.ChunkWorldZ);

            return new TrackingRenderer(this);
        }

        private sealed class TrackingRenderer : INativeChunkRenderer
        {
            private readonly TrackingRendererFactory owner;
            private int disposed;

            internal TrackingRenderer(TrackingRendererFactory owner)
            {
                this.owner = owner;
            }

            public void RenderOpaque(ShaderProgram program)
            {
            }

            public void RenderTransparent(ShaderProgram program)
            {
            }

            public void Dispose()
            {
                Assert.Equal(0, Interlocked.Exchange(ref disposed, 1));
                owner.DisposedCount++;
            }
        }
    }

    private sealed class RendererFactoryFailure : Exception;
}

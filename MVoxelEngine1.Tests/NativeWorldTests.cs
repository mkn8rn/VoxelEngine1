using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MVoxelEngine1.Graphics;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Terrain;
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

    [Fact]
    public void NegativeBlockEditSurvivesSaveReloadAndMovement()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSmallSettings();
        using var workspace = new SaveWorkspace();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        var firstFactory = new TrackingRendererFactory();
        string editedIdentity;
        using (NativeWorld firstWorld = NativeWorld.CreateForTesting(
                   NativeGtrtPipeline.Create(
                       atlas,
                       settings,
                       generationWorkerCount: 2,
                       meshWorkerCount: 2,
                       streamGeneration: true),
                   seed: 123456,
                   firstFactory.Create,
                   workspace.QuadsDirectory))
        {
            string initialIdentity = firstFactory.LiveBankIdentity;
            ushort previous = firstWorld.GetBlock(-1, 0, -1);
            Assert.NotEqual(CustomTransparentBlockId, previous);

            Assert.True(firstWorld.SetBlock(
                -1,
                0,
                -1,
                CustomTransparentBlockId));
            Assert.Equal(
                CustomTransparentBlockId,
                firstWorld.GetBlock(-1, 0, -1));
            editedIdentity = firstFactory.LiveBankIdentity;
            Assert.NotEqual(initialIdentity, editedIdentity);
            int createdAfterEdit = firstFactory.CreatedCount;
            Assert.False(firstWorld.SetBlock(
                -1,
                0,
                -1,
                CustomTransparentBlockId));
            Assert.Equal(createdAfterEdit, firstFactory.CreatedCount);
            Assert.Equal(1, firstWorld.Save());
            Assert.Equal(0, firstWorld.Save());
        }

        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        Assert.Equal(1, plan.ChunkCount);
        var secondAtlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        var secondFactory = new TrackingRendererFactory();
        using NativeWorld secondWorld = NativeWorld.CreateForTesting(
            NativeGtrtPipeline.Create(
                secondAtlas,
                settings,
                generationWorkerCount: 2,
                meshWorkerCount: 2,
                streamGeneration: true,
                savePlan: plan),
            seed: 123456,
            secondFactory.Create,
            workspace.QuadsDirectory);

        Assert.Equal(
            CustomTransparentBlockId,
            secondWorld.GetBlock(-1, 0, -1));
        Assert.Equal(editedIdentity, secondFactory.LiveBankIdentity);
        Assert.Equal(0, secondWorld.Save());

        secondWorld.PlayerChunkPosition = (1, 0, 0);
        secondWorld.PlayerChunkPosition = (0, 0, 0);

        Assert.Equal(editedIdentity, secondFactory.LiveBankIdentity);
        Assert.Equal(
            CustomTransparentBlockId,
            secondWorld.GetBlock(-1, 0, -1));
    }

    [Fact]
    public void RendererFailureRollsBackEditAndKeepsWorldUsable()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        var factory = new TrackingRendererFactory();
        using NativeWorld world = NativeWorld.CreateForTesting(
            NativeGtrtPipeline.Create(
                atlas,
                CreateSmallSettings(),
                generationWorkerCount: 2,
                meshWorkerCount: 2,
                streamGeneration: true),
            seed: 123456,
            factory.Create);
        ushort previous = world.GetBlock(-1, 0, -1);
        string initialIdentity = factory.LiveBankIdentity;
        factory.FailAtAttempt = checked(factory.CreatedCount + 5);

        Assert.Throws<RendererFactoryFailure>(() => world.SetBlock(
            -1,
            0,
            -1,
            CustomTransparentBlockId));

        Assert.Equal(previous, world.GetBlock(-1, 0, -1));
        Assert.Equal(27, factory.LiveRendererCount);
        Assert.Equal(initialIdentity, factory.LiveBankIdentity);

        factory.FailAtAttempt = int.MaxValue;
        world.PlayerChunkPosition = (1, 0, 0);
        world.PlayerChunkPosition = (0, 0, 0);

        Assert.Equal(previous, world.GetBlock(-1, 0, -1));
        Assert.Equal(initialIdentity, factory.LiveBankIdentity);
    }

    [Fact]
    public void RendererCleanupFailureKeepsPublishedEditConsistent()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        var factory = new TrackingRendererFactory();
        using NativeWorld world = NativeWorld.CreateForTesting(
            NativeGtrtPipeline.Create(
                atlas,
                CreateSmallSettings(),
                generationWorkerCount: 2,
                meshWorkerCount: 2),
            seed: 123456,
            factory.Create);
        string initialIdentity = factory.LiveBankIdentity;
        factory.FailDisposeAtCreation = 1;

        Assert.Throws<RendererDisposeFailure>(() => world.SetBlock(
            -1,
            0,
            -1,
            CustomTransparentBlockId));

        Assert.Equal(
            CustomTransparentBlockId,
            world.GetBlock(-1, 0, -1));
        Assert.Equal(27, factory.LiveRendererCount);
        string editedIdentity = factory.LiveBankIdentity;
        Assert.NotEqual(initialIdentity, editedIdentity);

        factory.FailDisposeAtCreation = int.MaxValue;
        world.PlayerChunkPosition = (1, 0, 0);
        world.PlayerChunkPosition = (0, 0, 0);

        Assert.Equal(
            CustomTransparentBlockId,
            world.GetBlock(-1, 0, -1));
        Assert.Equal(editedIdentity, factory.LiveBankIdentity);
    }

    [Fact]
    public void FailedAtomicSaveKeepsThePriorQuadAndDirtyEdit()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSmallSettings();
        using var workspace = new SaveWorkspace();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        var factory = new TrackingRendererFactory();
        using NativeWorld world = NativeWorld.CreateForTesting(
            NativeGtrtPipeline.Create(
                atlas,
                settings,
                generationWorkerCount: 2,
                meshWorkerCount: 2),
            seed: 123456,
            factory.Create,
            workspace.QuadsDirectory);

        Assert.True(world.SetBlock(
            0,
            0,
            0,
            CustomTransparentBlockId));
        Assert.True(world.SetBlock(
            4,
            0,
            0,
            CustomTransparentBlockId));
        Assert.Equal(1, world.Save());
        string quadPath = Directory.GetFiles(
            workspace.QuadsDirectory,
            "quad*x*.bin").Single();
        byte[] firstHash = SHA256.HashData(File.ReadAllBytes(quadPath));
        ushort secondPrevious = world.GetBlock(1, 0, 0);
        ushort secondBlock = secondPrevious == 0
            ? SoilId
            : (ushort)0;
        Assert.True(world.SetBlock(1, 0, 0, secondBlock));

        using (var locked = new FileStream(
                   quadPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => world.Save());
        }

        Assert.True(firstHash.AsSpan().SequenceEqual(
            SHA256.HashData(File.ReadAllBytes(quadPath))));
        Assert.Empty(Directory.GetFiles(
            workspace.QuadsDirectory,
            ".*.tmp"));
        Assert.Equal(1, world.Save());
        Assert.False(firstHash.AsSpan().SequenceEqual(
            SHA256.HashData(File.ReadAllBytes(quadPath))));
        Assert.Equal(0, world.Save());
        world.Dispose();

        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        Assert.Equal(2, plan.ChunkCount);
        var reloadAtlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeWorld reloaded = NativeWorld.CreateForTesting(
            NativeGtrtPipeline.Create(
                reloadAtlas,
                settings,
                generationWorkerCount: 2,
                meshWorkerCount: 2,
                savePlan: plan),
            seed: 123456,
            new TrackingRendererFactory().Create);
        Assert.Equal(
            CustomTransparentBlockId,
            reloaded.GetBlock(0, 0, 0));
        Assert.Equal(
            CustomTransparentBlockId,
            reloaded.GetBlock(4, 0, 0));
        Assert.Equal(secondBlock, reloaded.GetBlock(1, 0, 0));
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

    private static ushort SoilId => (byte)BaseBlockType.Soil;

    private const ushort CustomTransparentBlockId = 257;

    private sealed class TrackingRendererFactory
    {
        private readonly List<TrackingRenderer> renderers = [];

        internal int CreatedCount { get; private set; }

        internal int DisposedCount { get; private set; }

        internal int FailAtAttempt { get; set; } = int.MaxValue;

        internal int FailDisposeAtCreation { get; set; } = int.MaxValue;

        internal int MinimumCurrentWorldX { get; private set; } = int.MaxValue;

        internal int MaximumCurrentWorldX { get; private set; } = int.MinValue;

        internal int MinimumCurrentWorldY { get; private set; } = int.MaxValue;

        internal int MaximumCurrentWorldY { get; private set; } = int.MinValue;

        internal int MinimumCurrentWorldZ { get; private set; } = int.MaxValue;

        internal int MaximumCurrentWorldZ { get; private set; } = int.MinValue;

        internal int LiveRendererCount =>
            renderers.Count(renderer => !renderer.IsDisposed);

        internal string LiveBankIdentity => string.Join(
            "|",
            renderers
                .Where(renderer => !renderer.IsDisposed)
                .Select(renderer => renderer.Identity)
                .Order(StringComparer.Ordinal));

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

            string identity = CreateIdentity(
                in descriptor,
                opaqueWords,
                transparentWords);
            var renderer = new TrackingRenderer(this, attempt, identity);
            renderers.Add(renderer);
            return renderer;
        }

        private static string CreateIdentity(
            in NativeChunkRenderPacketDescriptor descriptor,
            ReadOnlySpan<uint> opaqueWords,
            ReadOnlySpan<uint> transparentWords)
        {
            string opaqueHash = Convert.ToHexString(SHA256.HashData(
                MemoryMarshal.AsBytes(opaqueWords)));
            string transparentHash = Convert.ToHexString(SHA256.HashData(
                MemoryMarshal.AsBytes(transparentWords)));
            return $"{descriptor.ChunkWorldX},{descriptor.ChunkWorldY}," +
                $"{descriptor.ChunkWorldZ}:" +
                $"{descriptor.OpaqueFaceCount},{opaqueWords.Length}," +
                $"{descriptor.TransparentFaceCount}," +
                $"{transparentWords.Length}:{opaqueHash}:{transparentHash}";
        }

        private sealed class TrackingRenderer : INativeChunkRenderer
        {
            private readonly TrackingRendererFactory owner;
            private int disposed;

            internal TrackingRenderer(
                TrackingRendererFactory owner,
                int creationNumber,
                string identity)
            {
                this.owner = owner;
                CreationNumber = creationNumber;
                Identity = identity;
            }

            private int CreationNumber { get; }

            internal string Identity { get; }

            internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

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
                if (CreationNumber == owner.FailDisposeAtCreation)
                    throw new RendererDisposeFailure();
            }
        }
    }

    private sealed class SaveWorkspace : IDisposable
    {
        internal SaveWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "VoxelEngine1",
                "native-world-save",
                Guid.NewGuid().ToString("N"));
            QuadsDirectory = Path.Combine(Root, "quads");
        }

        internal string Root { get; }

        internal string QuadsDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RendererFactoryFailure : Exception;

    private sealed class RendererDisposeFailure : Exception;
}

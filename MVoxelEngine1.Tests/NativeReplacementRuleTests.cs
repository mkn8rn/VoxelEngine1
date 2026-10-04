using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Tests;

public sealed class NativeReplacementRuleTests
{
    [Fact]
    public void DefaultInlineRulesUseTheRuntimeBlocksAndTheirActualTransparency()
    {
        using TestWorkspace workspace = TestPaths.CreateWorkspace();
        Configure(workspace);
        using NativeWorld world = CreateWorld(workspace);
        Assert.Equal((ushort)257, world.GetBlock(0, 0, 0));
        Assert.Equal((ushort)401, world.GetBlock(0, 400, 0));
        Assert.Equal((ushort)0, world.GetBlock(0, -1, 0));
        world.InspectState(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            NativeColumnRecord column = view.Columns[view.GetColumnIndex(0, 0)];
            Assert.Equal(1, column.ReplacementMode);
            Assert.Equal((ushort)257, column.ResolvedMaterials.Stone.Id);
            Assert.Equal((ushort)401, column.ResolvedMaterials.Soil.Id);
            Assert.Equal(NativeBlockFlags.Transparent,
                column.ResolvedMaterials.Stone.Flags & NativeBlockFlags.Transparent);
        });
        WorldFaceManifest manifest = AssertReference(world);
        Assert.Equal(0, manifest.Faces.OpaqueFaceCount);
        Assert.True(manifest.Faces.TransparentFaceCount > 0);
    }

    [Fact]
    public void InlinePriorityIsStableAndSimpleRulesThenMatchTheReplacedIds()
    {
        using TestWorkspace workspace = TestPaths.CreateWorkspace();
        Configure(workspace);
        File.WriteAllText(RulesPath(workspace), "[\n" +
            "  { \"generation_type\": \"SimpleReplacement\", \"base_blocks_to_replace\": [\"Gas\"],\n" +
            "    \"block_type_id\": \"Water\", \"priority\": 1, \"absolute_min_ylevel\": 3, \"absolute_max_ylevel\": 3 },\n" +
            "  { \"generation_type\": \"InlineReplacement\", \"base_blocks_to_replace\": [\"Stone\"],\n" +
            "    \"block_type_id\": \"LimeWhole\", \"priority\": 2 },\n" +
            "  { \"generation_type\": \"InlineReplacement\", \"base_blocks_to_replace\": [\"Stone\"],\n" +
            "    \"block_type_id\": \"RockWhole\", \"priority\": 2 },\n" +
            "  { \"generation_type\": \"SimpleReplacement\", \"base_blocks_to_replace\": [], \"blocks_to_replace\": [256],\n" +
            "    \"block_type_id\": \"Gas\", \"priority\": -100, \"absolute_min_ylevel\": 2, \"absolute_max_ylevel\": 3 }\n" +
            "]");
        using NativeWorld world = CreateWorld(workspace);
        Assert.Equal((ushort)256, world.GetBlock(0, 0, 0));
        Assert.Equal((ushort)256, world.GetBlock(0, 1, 0));
        Assert.Equal((ushort)1, world.GetBlock(0, 2, 0));
        Assert.Equal((ushort)11, world.GetBlock(0, 3, 0));
        Assert.Equal((ushort)256, world.GetBlock(0, 4, 0));
        AssertReference(world);
    }

    [Fact]
    public void RelativeDepthBoundsFollowEachColumnsSolidSurface()
    {
        using TestWorkspace workspace = TestPaths.CreateWorkspace();
        Configure(workspace);
        File.WriteAllText(RulesPath(workspace), "[\n" +
            "  { \"generation_type\": \"InlineReplacement\", \"base_blocks_to_replace\": [\"Soil\"],\n" +
            "    \"block_type_id\": \"Rendzina\", \"priority\": 1 },\n" +
            "  { \"generation_type\": \"SimpleReplacement\", \"base_blocks_to_replace\": [\"Soil\"],\n" +
            "    \"block_type_id\": \"Gas\", \"priority\": 1, \"relative_min_depth\": 2, \"relative_max_depth\": 4 }\n" +
            "]");
        using NativeWorld world = CreateWorld(workspace);
        for (int x = -4; x < 8; x++)
        {
            int surface = 0;
            world.InspectState(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                int column = view.GetColumnIndex(Math.DivRem(x, 4, out int localX), 0);
                if (localX < 0)
                {
                    column = view.GetColumnIndex(x / 4 - 1, 0);
                    localX += 4;
                }
                surface = view.GetColumnProfiles(column)[localX * 4].SoilEnd;
            });
            Assert.Equal((ushort)401, world.GetBlock(x, surface - 1, 0));
            for (int depth = 2; depth <= 4; depth++)
                Assert.Equal((ushort)1, world.GetBlock(x, surface - depth, 0));
            Assert.Equal((ushort)401, world.GetBlock(x, surface - 5, 0));
        }
        AssertReference(world);
    }

    [Theory]
    [InlineData("\"absolute_min_ylevel\": 8, \"absolute_max_ylevel\": 2", "bounds")]
    [InlineData("\"relative_min_depth\": -1", "bounds")]
    [InlineData("\"fill_proportion\": 0.5", "not implemented")]
    [InlineData("\"microbiome_id\": 1", "not implemented")]
    public void InvalidOrUnsupportedFiltersFailBeforeNativeSeedPublication(string filter, string message)
    {
        using TestWorkspace workspace = TestPaths.CreateWorkspace();
        Configure(workspace);
        File.WriteAllText(RulesPath(workspace), "[{ \"generation_type\": \"InlineReplacement\", \"base_blocks_to_replace\": [\"Stone\"],\n" +
            "   \"block_type_id\": \"LimeWhole\", \"priority\": 1, $filter }]".Replace("$filter", filter, StringComparison.Ordinal));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LoadGame(workspace));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorldFaceManifest AssertReference(NativeWorld world)
    {
        WorldFaceManifest reference = WorldFaceManifestBuilder.Capture(world, "Default", 123456, FaceGenerationMode.Reference);
        WorldFaceManifest optimized = WorldFaceManifestBuilder.Capture(world, "Default", 123456, FaceGenerationMode.Optimized);
        Assert.Equal(reference.Faces.Sha256, optimized.Faces.Sha256);
        return optimized;
    }

    private static void Configure(TestWorkspace workspace) =>
        SimulatedGpuUploadTestSupport.ConfigureSmallWorld(workspace.GameDataRoot,
            chunkSizeX: 4, chunkSizeY: 8, chunkSizeZ: 4);

    private static string RulesPath(TestWorkspace workspace) => Path.Combine(workspace.GameDataRoot,
        "Default", "Data", "Biomes", "Fallowlands", "GenerationRules.txt");

    private static NativeWorld CreateWorld(TestWorkspace workspace)
    {
        LoadGame(workspace);
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        ChunkRender.terrainTextureAtlas = atlas;
        return NativeWorld.CreateForTesting(NativeGtrtPipeline.Create(atlas, GameManager.settings, 2, 2),
            123456, static (in NativeChunkRenderPacketDescriptor descriptor,
                ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent) => null);
    }

    private static void LoadGame(TestWorkspace workspace)
    {
        GameManager.Initialize(workspace.GameDataRoot);
        GameManager.LoadGameDefaultSettings(GameManager.SelectGameFolder("Default"));
        TerrainLoader.allBlockTypes.Clear();
        TerrainLoader.allBlockTypesByBaseType.Clear();
        TerrainLoader.allBlockTypesByIds.Clear();
        TerrainLoader.allBlockTypeObjects.Clear();
        _ = new TerrainLoader();
        BiomeManager.LoadAllBiomes();
    }
}

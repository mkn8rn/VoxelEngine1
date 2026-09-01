using System.Reflection;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeGameSnapshotTests
{
    private const string NamRepositoryCommit =
        "b4c62b7dc7500a06412d44935978d978f97cf642";

    [Fact]
    public void PublishedNamPackageIdentityIsLoaded()
    {
        Assembly assembly = typeof(NativeBuilder<>).Assembly;
        AssemblyName name = assembly.GetName();
        AssemblyInformationalVersionAttribute? information =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.Equal("Supprocom.NativeAllocationManagement", name.Name);
        Assert.Equal(new Version(0, 2, 1, 0), name.Version);
        Assert.Equal(
            $"0.2.1+{NamRepositoryCommit}",
            information?.InformationalVersion);
    }

    [Fact]
    public void NativeSnapshotPreservesRuntimeBlocksBiomesAndTiles()
    {
        LoadDefaultGame();
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGameSnapshot snapshot = NativeGameSnapshot.Create(atlas);

        snapshot.Access(view =>
        {
            var native = new NativeGameSnapshotView(view.AsSpan());
            Assert.Equal(ushort.MaxValue + 1, native.Blocks.Length);

            foreach (BlockType block in TerrainLoader.allBlockTypeObjects)
            {
                NativeBlockDescriptor descriptor = native.Blocks[block.ID];
                Assert.Equal(block.ID, descriptor.Id);
                Assert.Equal((ushort)block.BaseType, descriptor.BaseType);
                Assert.Equal((byte)block.StateOfMatter, descriptor.StateOfMatter);
                Assert.True((descriptor.Flags & NativeBlockFlags.Defined) != 0);
                Assert.Equal(
                    TerrainLoader.IsOpaque(block.ID),
                    (descriptor.Flags & NativeBlockFlags.Opaque) != 0);
                Assert.Equal(
                    block.IsTransparent,
                    (descriptor.Flags & NativeBlockFlags.Transparent) != 0);
                Assert.Equal(
                    TerrainLoader.IsLiquid(block.ID),
                    (descriptor.Flags & NativeBlockFlags.Liquid) != 0);

                IReadOnlyDictionary<Faces, ByteVector2> faces =
                    BlockTextureAtlas.blockTypeUVCoordinates[block.ID];
                for (byte direction = 0; direction < 6; direction++)
                {
                    ByteVector2 coordinates = faces[(Faces)direction];
                    ushort expected = checked((ushort)(
                        coordinates.y * atlas.tilesX + coordinates.x));
                    Assert.Equal(expected, descriptor.GetTile(direction));
                }
            }

            NativeTerrainMaterialSet materials =
                native.GetGeneratedMaterials();
            AssertDescriptorEqual(
                native.Blocks[(byte)BaseBlockType.Stone],
                materials.Stone);
            AssertDescriptorEqual(
                native.Blocks[(byte)BaseBlockType.Soil],
                materials.Soil);
            AssertDescriptorEqual(
                native.Blocks[(byte)BaseBlockType.Water],
                materials.Water);

            KeyValuePair<string, Biome>[] orderedBiomes = BiomeManager.Biomes
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(orderedBiomes.Length, native.Biomes.Length);
            for (int index = 0; index < orderedBiomes.Length; index++)
            {
                Biome source = orderedBiomes[index].Value;
                NativeBiomeDescriptor descriptor = native.Biomes[index];
                Assert.Equal(source.id, descriptor.Id);
                Assert.Equal(source.stoneMinYLevel, descriptor.StoneMinY);
                Assert.Equal(source.stoneMaxYLevel, descriptor.StoneMaxY);
                Assert.Equal(source.stoneMinDepth, descriptor.StoneMinDepth);
                Assert.Equal(source.stoneMaxDepth, descriptor.StoneMaxDepth);
                Assert.Equal(source.soilMinYLevel, descriptor.SoilMinY);
                Assert.Equal(source.soilMaxYLevel, descriptor.SoilMaxY);
                Assert.Equal(source.soilMinDepth, descriptor.SoilMinDepth);
                Assert.Equal(source.soilMaxDepth, descriptor.SoilMaxDepth);
                Assert.Equal(source.waterLevel, descriptor.WaterLevel);
                Assert.Equal(
                    source.compiledSimpleReplacementRules.Length,
                    descriptor.ReplacementRuleCount);
            }

            Assert.Equal(
                BiomeManager.SelectBiomeForChunk(123456, -160, 320).id,
                native.Biomes[
                    native.SelectBiomeIndex(123456, -160, 320)].Id);
        });
    }

    private static void AssertDescriptorEqual(
        NativeBlockDescriptor expected,
        NativeBlockDescriptor actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.BaseType, actual.BaseType);
        Assert.Equal(expected.StateOfMatter, actual.StateOfMatter);
        Assert.Equal(expected.Flags, actual.Flags);
        for (byte direction = 0; direction < 6; direction++)
            Assert.Equal(expected.GetTile(direction), actual.GetTile(direction));
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
}

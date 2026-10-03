using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Tests;

public sealed class NativeReferenceFaceGeneratorTests
{
    [Fact]
    public void ReferenceFacesUseRawProfilesAndRulesDespiteCorruptedProductionSummariesAndMaterials()
    {
        using TestWorkspace workspace = TestPaths.CreateWorkspace();
        SimulatedGpuUploadTestSupport.ConfigureSmallWorld(workspace.GameDataRoot,
            chunkSizeX: 4, chunkSizeY: 8, chunkSizeZ: 4);
        GameManager.Initialize(workspace.GameDataRoot);
        GameManager.LoadGameDefaultSettings(GameManager.SelectGameFolder("Default"));
        TerrainLoader.allBlockTypes.Clear();
        TerrainLoader.allBlockTypesByBaseType.Clear();
        TerrainLoader.allBlockTypesByIds.Clear();
        TerrainLoader.allBlockTypeObjects.Clear();
        _ = new TerrainLoader();
        BiomeManager.LoadAllBiomes();
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(atlas, GameManager.settings, 2, 2);
        pipeline.Run(123456);
        pipeline.ConsumeReadyPackets(static (in NativeChunkRenderPacketDescriptor descriptor,
            ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent) => { });
        pipeline.InspectState(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            foreach (NativeColumnRecord column in view.Columns)
            {
                Span<BlockColumnProfile> profiles = view.GetColumnProfiles(view.GetColumnIndex(column.ChunkX, column.ChunkZ));
                for (int x = 0; x < view.ChunkSizeX; x++)
                for (int z = 0; z < view.ChunkSizeZ; z++)
                {
                    int worldX = column.ChunkX * view.ChunkSizeX + x;
                    int worldZ = column.ChunkZ * view.ChunkSizeZ + z;
                    int stoneEnd = Math.Abs(worldX % 3) * 2 + 1;
                    profiles[x * view.ChunkSizeZ + z] = new BlockColumnProfile
                    {
                        StoneStart = 0, StoneEnd = stoneEnd,
                        SoilStart = (worldZ & 1) == 0 ? 1 : stoneEnd + 1, SoilEnd = stoneEnd + 3,
                        WaterStart = stoneEnd + 4, WaterEnd = 14
                    };
                }
            }
            var reference = new NativeReferenceFaceGenerator(ref view);
            var actual = new List<CanonicalRenderFace>();
            var expected = new List<CanonicalRenderFace>();
            (int X, int Y, int Z)[] normals = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];
            for (int index = 0; index < view.Chunks.Length; index++)
            {
                if (!view.TryInspectRetiredPacket(index, out _))
                    continue;
                actual.AddRange(reference.Generate(ref view, index));
                NativeChunkRecord chunk = view.Chunks[index];
                for (int x = 0; x < view.ChunkSizeX; x++)
                for (int y = 0; y < view.ChunkSizeY; y++)
                for (int z = 0; z < view.ChunkSizeZ; z++)
                {
                    Assert.True(NativeGeneratedTerrain.TryGetBlock(ref view, index, x, y, z, out ushort source));
                    Assert.Equal(source, reference.GetBlock(ref view, index, x, y, z));
                    if (source == 0)
                        continue;
                    for (byte direction = 0; direction < normals.Length; direction++)
                    {
                        (int dx, int dy, int dz) = normals[direction];
                        Assert.True(NativeGeneratedTerrain.TryGetBlock(ref view, index, x + dx, y + dy, z + dz, out ushort neighbor));
                        bool sourceOpaque = TerrainLoader.IsOpaque(source);
                        if (TerrainLoader.IsOpaque(neighbor) || (!sourceOpaque && source == neighbor))
                            continue;
                        expected.Add(new CanonicalRenderFace(chunk.ChunkX * view.ChunkSizeX + x,
                            chunk.ChunkY * view.ChunkSizeY + y, chunk.ChunkZ * view.ChunkSizeZ + z,
                            direction, sourceOpaque ? CanonicalRenderPass.Opaque : CanonicalRenderPass.Transparent, source, neighbor));
                    }
                }
            }
            string expectedHash = CanonicalRenderFaceHasher.Hash(expected).Sha256;
            Assert.Equal(expectedHash, CanonicalRenderFaceHasher.Hash(actual).Sha256);
            Assert.NotEmpty(actual);
            view.ColumnSummaries.Clear();
            foreach (ref NativeColumnRecord column in view.Columns)
            {
                column.SummaryComputed = 0;
                column.ReplacementMode = 1;
                column.ResolvedMaterials = view.Materials;
            }
            var afterCorruption = new NativeReferenceFaceGenerator(ref view);
            actual.Clear();
            for (int index = 0; index < view.Chunks.Length; index++)
                if (view.TryInspectRetiredPacket(index, out _))
                    actual.AddRange(afterCorruption.Generate(ref view, index));
            Assert.Equal(expectedHash, CanonicalRenderFaceHasher.Hash(actual).Sha256);
            int origin = view.GetChunkIndex(0, 0, 0);
            Assert.Equal((ushort)257, afterCorruption.GetBlock(ref view, origin, 0, 0, 0));
            Assert.True(NativeGeneratedTerrain.TryGetBlock(ref view, origin, 0, 0, 0, out ushort corrupted));
            Assert.Equal((ushort)6, corrupted);
        });
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Tests;

public sealed class NativeStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RingMovementRetainsProfilesPacketsAndFacesAcrossWrappingAndTeleports(bool stream)
    {
        using TestWorkspace workspace = Configure();
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        ChunkRender.terrainTextureAtlas = atlas;
        using NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(atlas, GameManager.settings, 2, 3, stream);
        using var allocationScope = new NoGcAllocationScope();
        using NativeWorld world = NativeWorld.CreateForTesting(pipeline, 123456, HeadlessRenderer);
        Assert.Equal(new NativeStreamingStatistics(25, 0, 27, 0), world.StreamingStatistics);
        int surfaceY = 0;
        world.InspectState(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            surfaceY = view.GetColumnProfiles(view.GetColumnIndex(0, 0))[0].SoilEnd / 8;
        });
        (int x, int y, int z)[] centers =
        [
            (0, surfaceY, 0), (1, surfaceY, 0), (1, surfaceY + 1, 0),
            (0, surfaceY, -1), (-1, surfaceY, -2), (-2, surfaceY, -3),
            (-3, surfaceY, -4), (-4, surfaceY, -5), (-5, surfaceY, -6),
            (-6, surfaceY, -7), (-12, surfaceY + 4, 14), (0, surfaceY, 0)
        ];
        foreach ((int x, int y, int z) center in centers)
        {
            (int oldX, int oldY, int oldZ) = world.PlayerChunkPosition;
            var previousPackets = CapturePackets(world);
            var previousColumns = CaptureColumns(world);
            world.PlayerChunkPosition = center;
            int retainedColumns = Overlap(5, oldX, center.x) * Overlap(5, oldZ, center.z);
            int retainedPackets = Overlap(3, oldX, center.x) * Overlap(3, oldY, center.y) * Overlap(3, oldZ, center.z);
            Assert.Equal(new NativeStreamingStatistics(25 - retainedColumns, retainedColumns,
                27 - retainedPackets, retainedPackets), world.StreamingStatistics);
            var packets = CapturePackets(world);
            int observedRetained = 0;
            foreach (var pair in packets)
            {
                if (previousPackets.TryGetValue(pair.Key, out var previous))
                {
                    Assert.Equal(previous, pair.Value);
                    observedRetained++;
                }
            }
            Assert.Equal(retainedPackets, observedRetained);
            var columns = CaptureColumns(world);
            int observedColumns = 0;
            foreach (var pair in columns)
            {
                if (previousColumns.TryGetValue(pair.Key, out var previous))
                {
                    Assert.Equal(previous, pair.Value);
                    observedColumns++;
                }
            }
            Assert.Equal(retainedColumns, observedColumns);
            AssertReference(world);
        }
        Assert.Equal(0, pipeline.MaximumWorkerManagedAllocationBytes);
        Assert.Equal(0, pipeline.CoordinatorManagedAllocationBytes);
    }

    [Fact]
    public void EditsOnlyRemeshTheEditedChunkAndItsAffectedBoundaryNeighbors()
    {
        using TestWorkspace workspace = Configure();
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        ChunkRender.terrainTextureAtlas = atlas;
        using NativeWorld world = NativeWorld.CreateForTesting(
            NativeGtrtPipeline.Create(atlas, GameManager.settings, 2, 2), 123456, HeadlessRenderer);
        var original = CapturePackets(world);
        Assert.True(world.SetBlock(1, 1, 1, 256));
        Assert.Equal(new NativeStreamingStatistics(0, 25, 1, 26), world.StreamingStatistics);
        var edited = CapturePackets(world);
        Assert.Equal(26, edited.Count(pair => original[pair.Key] == pair.Value));
        AssertReference(world);
        Assert.True(world.SetBlock(3, 7, 3, 11));
        Assert.Equal(new NativeStreamingStatistics(0, 25, 4, 23), world.StreamingStatistics);
        AssertReference(world);
        world.PlayerChunkPosition = (5, 0, -5);
        world.PlayerChunkPosition = (0, 0, 0);
        Assert.Equal((ushort)256, world.GetBlock(1, 1, 1));
        Assert.Equal((ushort)11, world.GetBlock(3, 7, 3));
        AssertReference(world);
    }

    private static Dictionary<(int, int, int), (long, string, string)> CapturePackets(NativeWorld world)
    {
        var result = new Dictionary<(int, int, int), (long, string, string)>();
        world.InspectRenderPackets((in NativeChunkRenderPacketDescriptor descriptor,
            ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent) =>
            result.Add((descriptor.ChunkWorldX, descriptor.ChunkWorldY, descriptor.ChunkWorldZ),
                (descriptor.RenderDataId, Hash(opaque), Hash(transparent))));
        Assert.Equal(27, result.Count);
        return result;
    }

    private static Dictionary<(int, int), (int, string)> CaptureColumns(NativeWorld world)
    {
        var result = new Dictionary<(int, int), (int, string)>();
        world.InspectState(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            for (int index = 0; index < view.Columns.Length; index++)
            {
                NativeColumnRecord column = view.Columns[index];
                Assert.Equal(index, view.GetColumnIndex(column.ChunkX, column.ChunkZ));
                result.Add((column.ChunkX, column.ChunkZ), (index,
                    Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(view.GetColumnProfiles(index))))));
            }
        });
        return result;
    }

    private static string Hash(ReadOnlySpan<uint> words) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(words)));

    private static int Overlap(int width, int a, int b) => Math.Max(0, width - Math.Abs(a - b));

    private static void AssertReference(NativeWorld world)
    {
        WorldFaceManifest reference = WorldFaceManifestBuilder.Capture(world, "Default", 123456, FaceGenerationMode.Reference);
        WorldFaceManifest optimized = WorldFaceManifestBuilder.Capture(world, "Default", 123456, FaceGenerationMode.Optimized);
        Assert.Equal(reference.Faces.Sha256, optimized.Faces.Sha256);
    }

    private static INativeChunkRenderer? HeadlessRenderer(in NativeChunkRenderPacketDescriptor descriptor,
        ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent) => null;

    private static TestWorkspace Configure()
    {
        TestWorkspace workspace = TestPaths.CreateWorkspace();
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
        return workspace;
    }
}

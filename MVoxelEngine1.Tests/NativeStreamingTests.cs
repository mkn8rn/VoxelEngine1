using System.Buffers.Binary;
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
    [Fact(Explicit = true, Timeout = 300_000)]
    [Trait("Category", "Oracle")]
    [Trait("Resource", "CPU")]
    public void FullProductionTeleportKeepsPacketsReusable()
    {
        using TestWorkspace workspace = TestPaths.CreateWorkspace();
        GameManager.Initialize(workspace.GameDataRoot);
        GameManager.LoadGameDefaultSettings(GameManager.SelectGameFolder("Default"));
        TerrainLoader.allBlockTypes.Clear();
        TerrainLoader.allBlockTypesByBaseType.Clear();
        TerrainLoader.allBlockTypesByIds.Clear();
        TerrainLoader.allBlockTypeObjects.Clear();
        _ = new TerrainLoader();
        BiomeManager.LoadAllBiomes();
        Assert.Equal(160, GameManager.settings.chunkMaxX);
        Assert.Equal(160, GameManager.settings.chunkMaxY);
        Assert.Equal(160, GameManager.settings.chunkMaxZ);
        Assert.Equal(12, GameManager.settings.lod1RenderDistance);
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(atlas, GameManager.settings,
            Environment.ProcessorCount * 2, Environment.ProcessorCount * 2,
            runtimeGenerationWorkerCount: NativeGtrtPipeline.GetWorkerCount(0.5f),
            runtimeMeshWorkerCount: Environment.ProcessorCount);
        using NativeWorld world = NativeWorld.CreateForTesting(pipeline, 123456, HeadlessRenderer);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        string original = CaptureProductionPacketHash(world, (0, 0, 0));
        Exception? failure = Record.Exception(() => world.PlayerChunkPosition = (40, 0, -40));
        if (failure is not null)
        {
            Assert.Equal((0, 0, 0), world.PlayerChunkPosition);
            Assert.Equal(original, CaptureProductionPacketHash(world, (0, 0, 0)));
        }
        Assert.Null(failure);
        Assert.Equal(new NativeStreamingStatistics(729, 0, 15_625, 0), world.StreamingStatistics);
        _ = CaptureProductionPacketHash(world, (40, 0, -40));
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        world.PlayerChunkPosition = (0, 0, 0);
        Assert.Equal(original, CaptureProductionPacketHash(world, (0, 0, 0)));
        world.InspectState(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.TransactionOpen);
            Assert.InRange(view.SessionHeader.TotalByteCount, 1, NativeGtrtSession.MaximumSessionByteCount);
            Console.WriteLine($"Full-production teleport native bytes: {view.SessionHeader.TotalByteCount}; " +
                $"packet words: {view.PacketWordCapacity}; high water: {view.State.PacketWordCursor}.");
        });
    }

    private static string CaptureProductionPacketHash(NativeWorld world, (int x, int y, int z) center)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var coordinates = new HashSet<(int, int, int)>();
        long faces = 0;
        world.InspectRenderPackets((in NativeChunkRenderPacketDescriptor descriptor,
            ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent) =>
        {
            Assert.True(coordinates.Add((descriptor.ChunkWorldX, descriptor.ChunkWorldY, descriptor.ChunkWorldZ)));
            Assert.InRange(descriptor.ChunkWorldX, (center.x - 12) * 160, (center.x + 12) * 160);
            Assert.InRange(descriptor.ChunkWorldY, (center.y - 12) * 160, (center.y + 12) * 160);
            Assert.InRange(descriptor.ChunkWorldZ, (center.z - 12) * 160, (center.z + 12) * 160);
            Assert.True(opaque.IsEmpty);
            Span<byte> coordinate = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(coordinate, descriptor.ChunkWorldX);
            BinaryPrimitives.WriteInt32LittleEndian(coordinate[4..], descriptor.ChunkWorldY);
            BinaryPrimitives.WriteInt32LittleEndian(coordinate[8..], descriptor.ChunkWorldZ);
            hash.AppendData(coordinate);
            hash.AppendData(MemoryMarshal.AsBytes(transparent));
            faces += descriptor.TransparentFaceCount;
        });
        Assert.Equal(15_625, coordinates.Count);
        Assert.True(faces > 0);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RingMovementRetainsProfilesPacketsAndFacesAcrossWrappingAndTeleports(bool stream)
    {
        using TestWorkspace workspace = Configure();
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        ValidateRingMovementRetainsProfilesPacketsAndFacesAcrossWrappingAndTeleportsEvidence(stream, atlas);
    }

    [Fact]
    public void EditsOnlyRemeshTheEditedChunkAndItsAffectedBoundaryNeighbors()
    {
        using TestWorkspace workspace = Configure();
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        ChunkRender.terrainTextureAtlas = atlas;
        using NativeGtrtPipeline ownedPipeline0 = NativeGtrtPipeline.Create(atlas, GameManager.settings, 2, 2);
        using NativeWorld world = NativeWorld.CreateForTesting(
            ownedPipeline0, 123456, HeadlessRenderer);
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

    private static void ValidateRingMovementRetainsProfilesPacketsAndFacesAcrossWrappingAndTeleportsEvidence(bool stream, global::MVoxelEngine1.Graphics.Textures.BlockTextureAtlas atlas)
    {
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
}

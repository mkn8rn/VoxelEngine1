using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.WorldGeneration.Native;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;
using Supprocom.OpenSimplexNoise;

namespace MVoxelEngine1.Tests;

public sealed class NativeColumnProfileGeneratorTests
{

    private static readonly System.Text.Json.JsonSerializerOptions EvidenceJsonOptions0 = new JsonSerializerOptions { WriteIndented = true };
    [Fact(Explicit = true, Timeout = 300_000)]
    [Trait("Category", "Oracle")]
    [Trait("Resource", "CPU")]
    public void EveryProductionAndHaloProfileMatchesTheManagedHeightAuthority()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        const long seed = 123456;
        GameManager.Initialize(TestPaths.GameDataRoot);
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
        Biome[] biomes = BiomeManager.Biomes.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => pair.Value).ToArray();
        Assert.Single(biomes);
        var noise = new OpenSimplexNoise(seed);
        var atlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(atlas, GameManager.settings,
            Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);
        CompleteEveryProductionAndHaloProfileMatchesTheManagedHeightAuthorityPhase(seed, biomes, noise, pipeline);
    }

    [Theory]
    [InlineData(-1, -1, 16)]
    [InlineData(0, 0, 16)]
    [InlineData(1, 1, 16)]
    [InlineData(0, 0, 160)]
    public void NativeProfilesAndSummaryMatchManagedReference(
        int chunkX,
        int chunkZ,
        int size)
    {
        const long seed = 123456;
        Biome biome = CreateBiome();
        NativeBiomeDescriptor nativeBiome = new(biome, 0, 0);
        var noise = new OpenSimplexNoise(seed);
        BlockColumnProfile[] expected = BuildReferenceProfiles(
            chunkX,
            chunkZ,
            size,
            seed,
            biome,
            noise,
            out ColumnUniformRanges expectedSummary);
        ValidateNativeProfilesAndSummaryMatchManagedReferenceEvidence(chunkX, chunkZ, size, seed, nativeBiome, expected, expectedSummary);
    }

    [Fact]
    public void GenerationWorkspaceRejectsConcurrentReuse()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 0,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            generationWorkerCount: 1);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.TryAcquireGenerationWorkspace(0));
            Assert.False(view.TryAcquireGenerationWorkspace(0));
            Assert.Equal(
                (int)NativeGtrtFailureCode.InvalidGenerationWorkspace,
                view.State.FailureCode);
            view.ReleaseGenerationWorkspace(0);
            Assert.Equal(0, view.GenerationWorkspaces[0].State);
        });
    }

    [Fact]
    public void NativeProfileKernelAllocatesNoManagedBytesAfterWarmup()
    {
        const long seed = 123456;
        Biome biome = CreateBiome();
        NativeBiomeDescriptor nativeBiome = new(biome, 0, 0);
        var harness = new ProfileGenerationHarness(nativeBiome);
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 16,
            chunkSizeY: 16,
            chunkSizeZ: 16,
            lod1Radius: 0,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            generationWorkerCount: 1);

        using (NativeGtrtSession warmup = NativeGtrtSession.Create(layout))
        {
            warmup.PublishSeed(seed);
            warmup.Access(harness.Action);
            Assert.True(harness.Result);
        }

        using NativeGtrtSession measured = NativeGtrtSession.Create(layout);
        measured.PublishSeed(seed);
        harness.Result = false;
        using var allocationScope = new NoGcAllocationScope();
        long before = GC.GetAllocatedBytesForCurrentThread();
        measured.Access(harness.Action);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(harness.Result);
        Assert.Equal(0, allocated);
    }

    private static BlockColumnProfile[] BuildReferenceProfiles(
        int chunkX,
        int chunkZ,
        int size,
        long seed,
        Biome biome,
        OpenSimplexNoise noise,
        out ColumnUniformRanges summary)
    {
        int profileCount = checked(size * size);
        int baseX = unchecked(chunkX * size);
        int baseZ = unchecked(chunkZ * size);
        var heights = new float[profileCount];
        var noiseValues = new float[profileCount];
        var profiles = new BlockColumnProfile[profileCount];
        Quadrant.FillHeightMap(
            noise,
            baseX,
            baseZ,
            size,
            size,
            heights);
        return FinishBuildReferenceProfilesPhase(size, seed, biome, out summary, baseX, baseZ, heights, noiseValues, profiles);
    }

    private static ColumnUniformRanges CreateEmptySummary() => new()
    {
        MinimumMaterialStart = int.MaxValue,
        MaximumMaterialEnd = int.MinValue,
        AllColumnsHaveStone = true,
        StoneStartMinimum = int.MaxValue,
        StoneStartMaximum = int.MinValue,
        StoneEndMinimum = int.MaxValue,
        StoneEndMaximum = int.MinValue,
        AllColumnsHaveSoil = true,
        SoilStartMinimum = int.MaxValue,
        SoilStartMaximum = int.MinValue,
        SoilEndMinimum = int.MaxValue,
        SoilEndMaximum = int.MinValue,
        AllColumnsHaveWater = true,
        WaterStartMinimum = int.MaxValue,
        WaterStartMaximum = int.MinValue,
        WaterEndMinimum = int.MaxValue,
        WaterEndMaximum = int.MinValue
    };

    private static void AddToSummary(
        ref ColumnUniformRanges summary,
        scoped ref readonly BlockColumnProfile profile)
    {
        bool hasStone =
            profile.StoneStart >= 0 &&
            profile.StoneEnd >= profile.StoneStart;
        bool hasSoil =
            profile.SoilStart >= 0 &&
            profile.SoilEnd >= profile.SoilStart;
        bool hasWater =
            profile.WaterStart >= 0 &&
            profile.WaterEnd >= profile.WaterStart;
        if (hasStone)
        {
            summary.HasMaterial = true;
            summary.MinimumMaterialStart = Math.Min(
                summary.MinimumMaterialStart,
                profile.StoneStart);
            summary.MaximumMaterialEnd = Math.Max(
                summary.MaximumMaterialEnd,
                profile.StoneEnd);
            summary.StoneStartMinimum = Math.Min(
                summary.StoneStartMinimum,
                profile.StoneStart);
            summary.StoneStartMaximum = Math.Max(
                summary.StoneStartMaximum,
                profile.StoneStart);
            summary.StoneEndMinimum = Math.Min(
                summary.StoneEndMinimum,
                profile.StoneEnd);
            summary.StoneEndMaximum = Math.Max(
                summary.StoneEndMaximum,
                profile.StoneEnd);
        }
        else
        {
            summary.AllColumnsHaveStone = false;
        }
        FinishAddToSummaryPhase(ref summary, in profile, hasSoil, hasWater);
    }

    private static void AssertProfileEqual(
        BlockColumnProfile expected,
        BlockColumnProfile actual)
    {
        Assert.Equal(expected.StoneStart, actual.StoneStart);
        Assert.Equal(expected.StoneEnd, actual.StoneEnd);
        Assert.Equal(expected.SoilStart, actual.SoilStart);
        Assert.Equal(expected.SoilEnd, actual.SoilEnd);
        Assert.Equal(expected.WaterStart, actual.WaterStart);
        Assert.Equal(expected.WaterEnd, actual.WaterEnd);
    }

    private static void AssertSummaryEqual(
        ColumnUniformRanges expected,
        NativeColumnSummary actual)
    {
        Assert.Equal(expected.HasMaterial, actual.HasMaterial != 0);
        Assert.Equal(expected.MinimumMaterialStart, actual.MinimumMaterialStart);
        Assert.Equal(expected.MaximumMaterialEnd, actual.MaximumMaterialEnd);
        Assert.Equal(
            expected.AllColumnsHaveStone,
            actual.AllColumnsHaveStone != 0);
        Assert.Equal(expected.StoneStartMinimum, actual.StoneStartMinimum);
        Assert.Equal(expected.StoneStartMaximum, actual.StoneStartMaximum);
        Assert.Equal(expected.StoneEndMinimum, actual.StoneEndMinimum);
        Assert.Equal(expected.StoneEndMaximum, actual.StoneEndMaximum);
        Assert.Equal(
            expected.AllColumnsHaveSoil,
            actual.AllColumnsHaveSoil != 0);
        Assert.Equal(expected.SoilStartMinimum, actual.SoilStartMinimum);
        Assert.Equal(expected.SoilStartMaximum, actual.SoilStartMaximum);
        Assert.Equal(expected.SoilEndMinimum, actual.SoilEndMinimum);
        Assert.Equal(expected.SoilEndMaximum, actual.SoilEndMaximum);
        Assert.Equal(
            expected.AllColumnsHaveWater,
            actual.AllColumnsHaveWater != 0);
        Assert.Equal(expected.WaterStartMinimum, actual.WaterStartMinimum);
        Assert.Equal(expected.WaterStartMaximum, actual.WaterStartMaximum);
        Assert.Equal(expected.WaterEndMinimum, actual.WaterEndMinimum);
        Assert.Equal(expected.WaterEndMaximum, actual.WaterEndMaximum);
    }

    private static Biome CreateBiome() => new()
    {
        id = 7,
        name = "native-profile-test",
        stoneMinYLevel = 0,
        stoneMaxYLevel = 1000,
        stoneMinDepth = 1,
        stoneMaxDepth = 1000,
        soilMinYLevel = 0,
        soilMaxYLevel = 1000,
        soilMinDepth = 4,
        soilMaxDepth = 10,
        waterLevel = 500,
        microbiomes = [],
        simpleReplacements = []
    };

    private sealed class ProfileGenerationHarness
    {
        private readonly NativeBiomeDescriptor biome;

        internal ProfileGenerationHarness(NativeBiomeDescriptor biome)
        {
            this.biome = biome;
            Action = Execute;
        }

        internal NativeLeaseAction<byte> Action { get; }

        internal bool Result { get; set; }

        private void Execute(scoped NativeLeaseView<byte> owner)
        {
            var session = new NativeGtrtSessionView(owner.AsSpan());
            Result = session.TryClaimGeneration(out NativeWorkItem work);
            if (!Result)
                return;

            Result = NativeColumnProfileGenerator.TryGenerate(
                    ref session,
                    workerIndex: 0,
                    in work,
                    biomeIndex: 0,
                    in biome);
            if (Result)
                Result = session.TryAbandonGeneration(in work);
        }
    }

    private static void ValidateEveryProductionAndHaloProfileMatchesTheManagedHeightAuthorityEvidence(long seed, long profileCount, int columnCount, string? nativeHash, string? referenceHash)
    {
        Assert.Equal(729, columnCount);
        Assert.Equal(18_662_400, profileCount);
        Assert.Equal(referenceHash, nativeHash);
        Assert.Equal("6CE51114DDC88E9ED52501806A5A6F7BE3174C4B197ACF9CE352D71713A86286", nativeHash);
        string output = Path.Combine(TestPaths.ResultsRoot, "default-full-profiles.json");
        Directory.CreateDirectory(TestPaths.ResultsRoot);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            game = "Default", seed, chunkSize = 160, lod1Radius = 12,
            columnCount, profileCount, nativeHash, referenceHash, allProfileBytesEqual = true
        }, EvidenceJsonOptions0));
        Console.WriteLine($"Full-production profile authority evidence: {output}");

    }

    private static void ValidateNativeProfilesAndSummaryMatchManagedReferenceEvidence(int chunkX, int chunkZ, int size, long seed, global::MVoxelEngine1.WorldGeneration.Native.NativeBiomeDescriptor nativeBiome, global::MVoxelEngine1.Infrastructure.Models.Generation.BlockColumnProfile[] expected, global::MVoxelEngine1.WorldGeneration.Terrain.ColumnUniformRanges expectedSummary)
    {
        var layout = new NativeGtrtSessionLayout(
            size,
            chunkSizeY: 16,
            size,
            lod1Radius: 0,
            materials: NativeTerrainMaterialSet.CreateConventional(),
            generationWorkerCount: 2);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);
        session.PublishSeed(seed);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int targetIndex = view.GetColumnIndex(chunkX, chunkZ);
            NativeWorkItem target = default;
            bool found = false;
            while (view.TryClaimGeneration(out NativeWorkItem claimed))
            {
                if (claimed.RecordIndex != targetIndex)
                {
                    Assert.True(view.TryAbandonGeneration(in claimed));
                    continue;
                }

                target = claimed;
                found = true;
                break;
            }

            Assert.True(found);
            Assert.True(NativeColumnProfileGenerator.TryGenerate(
                ref view,
                workerIndex: 1,
                in target,
                biomeIndex: 0,
                in nativeBiome));
            Assert.Equal(0, view.State.FailureCode);
            Assert.Equal(0, view.GenerationWorkspaces[1].State);
            Assert.Equal(0, view.Columns[targetIndex].BiomeIndex);
            Assert.Equal(target.Epoch, view.Columns[targetIndex].GenerationEpoch);

            Span<BlockColumnProfile> actual =
                view.GetColumnProfiles(targetIndex);
            Assert.Equal(expected.Length, actual.Length);
            for (int index = 0; index < expected.Length; index++)
                AssertProfileEqual(expected[index], actual[index]);

            AssertSummaryEqual(
                expectedSummary,
                view.ColumnSummaries[targetIndex]);
            Assert.True(view.TryAbandonGeneration(in target));
            Assert.Equal(0, view.State.ClaimedGenerationCount);
        });

    }

    private static void CompleteEveryProductionAndHaloProfileMatchesTheManagedHeightAuthorityPhase(long seed, global::MVoxelEngine1.Infrastructure.Models.Generation.Biomes.Biome[] biomes, global::Supprocom.OpenSimplexNoise.OpenSimplexNoise noise, global::MVoxelEngine1.WorldGeneration.Native.NativeGtrtPipeline pipeline)
    {
        pipeline.Run(seed);
        pipeline.ConsumeReadyPackets(static (in NativeChunkRenderPacketDescriptor descriptor,
            ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent) => { });
        long profileCount = 0;
        int columnCount = 0;
        string? nativeHash = null, referenceHash = null;
        pipeline.InspectState(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            NativeColumnRecord[] columns = view.Columns.ToArray();
            Array.Sort(columns, static (left, right) =>
            {
                int comparison = left.ChunkX.CompareTo(right.ChunkX);
                return comparison == 0 ? left.ChunkZ.CompareTo(right.ChunkZ) : comparison;
            });
            using IncrementalHash native = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using IncrementalHash reference = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            native.AppendData("MVoxelEngine1.DefaultColumnProfiles.v1"u8);
            reference.AppendData("MVoxelEngine1.DefaultColumnProfiles.v1"u8);
            Span<byte> coordinate = stackalloc byte[8];
            foreach (NativeColumnRecord column in columns)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(NativeColumnState.Generated, column.State);
                Assert.Equal(view.State.SessionEpoch, column.GenerationEpoch);
                BlockColumnProfile[] expected = BuildReferenceProfiles(column.ChunkX, column.ChunkZ,
                    view.ChunkSizeX, seed, biomes[column.BiomeIndex], noise, out _);
                Span<BlockColumnProfile> actual = view.GetColumnProfiles(view.GetColumnIndex(column.ChunkX, column.ChunkZ));
                ReadOnlySpan<byte> expectedBytes = MemoryMarshal.AsBytes(expected.AsSpan());
                ReadOnlySpan<byte> actualBytes = MemoryMarshal.AsBytes(actual);
                Assert.True(actualBytes.SequenceEqual(expectedBytes),
                    $"Production profile bytes differ at column ({column.ChunkX},{column.ChunkZ}).");
                BinaryPrimitives.WriteInt32LittleEndian(coordinate, column.ChunkX);
                BinaryPrimitives.WriteInt32LittleEndian(coordinate[4..], column.ChunkZ);
                native.AppendData(coordinate);
                reference.AppendData(coordinate);
                native.AppendData(actualBytes);
                reference.AppendData(expectedBytes);
                profileCount += actual.Length;
                columnCount++;
            }
            nativeHash = Convert.ToHexString(native.GetHashAndReset());
            referenceHash = Convert.ToHexString(reference.GetHashAndReset());
        });
        ValidateEveryProductionAndHaloProfileMatchesTheManagedHeightAuthorityEvidence(seed, profileCount, columnCount, nativeHash, referenceHash);

    }

    private static BlockColumnProfile[] FinishBuildReferenceProfilesPhase(int size, long seed, global::MVoxelEngine1.Infrastructure.Models.Generation.Biomes.Biome biome, scoped out global::MVoxelEngine1.WorldGeneration.Terrain.ColumnUniformRanges summary, int baseX, int baseZ, float[] heights, float[] noiseValues, global::MVoxelEngine1.Infrastructure.Models.Generation.BlockColumnProfile[] profiles)
    {
        TerrainGenerationUtils.FillSmoothValueNoise01(
            baseX,
            baseZ,
            size,
            size,
            seed,
            noiseValues);
        summary = CreateEmptySummary();

        for (int x = 0; x < size; x++)
        {
            int rowOffset = x * size;
            int previousXOffset = x == 0
                ? rowOffset
                : rowOffset - size;
            int nextXOffset = x == size - 1
                ? rowOffset
                : rowOffset + size;
            for (int z = 0; z < size; z++)
            {
                int profileIndex = rowOffset + z;
                int previousZ = z == 0 ? 0 : z - 1;
                int nextZ = z == size - 1 ? z : z + 1;
                int surface = (int)heights[profileIndex];
                float dx = heights[nextXOffset + z] -
                    heights[previousXOffset + z];
                float dz = heights[rowOffset + nextZ] -
                    heights[rowOffset + previousZ];
                float gradient = MathF.Sqrt(dx * dx + dz * dz);
                float slope = MathF.Min(1f, gradient / 6f);
                var spans =
                    TerrainGenerationUtils.DeriveWorldStoneSoilSpansFromNoise(
                        surface,
                        biome,
                        slope,
                        noiseValues[profileIndex]);
                BlockColumnProfile profile = new()
                {
                    StoneStart = spans.stoneStart,
                    StoneEnd = spans.stoneEnd,
                    SoilStart = spans.soilStart,
                    SoilEnd = spans.soilEnd,
                    WaterStart = spans.waterStart,
                    WaterEnd = spans.waterEnd
                };
                profiles[profileIndex] = profile;
                AddToSummary(ref summary, in profile);
            }
        }

        return profiles;

    }

    private static void FinishAddToSummaryPhase(ref global::MVoxelEngine1.WorldGeneration.Terrain.ColumnUniformRanges summary, scoped in global::MVoxelEngine1.Infrastructure.Models.Generation.BlockColumnProfile profile, bool hasSoil, bool hasWater)
    {

        if (hasSoil)
        {
            summary.HasMaterial = true;
            summary.MinimumMaterialStart = Math.Min(
                summary.MinimumMaterialStart,
                profile.SoilStart);
            summary.MaximumMaterialEnd = Math.Max(
                summary.MaximumMaterialEnd,
                profile.SoilEnd);
            summary.SoilStartMinimum = Math.Min(
                summary.SoilStartMinimum,
                profile.SoilStart);
            summary.SoilStartMaximum = Math.Max(
                summary.SoilStartMaximum,
                profile.SoilStart);
            summary.SoilEndMinimum = Math.Min(
                summary.SoilEndMinimum,
                profile.SoilEnd);
            summary.SoilEndMaximum = Math.Max(
                summary.SoilEndMaximum,
                profile.SoilEnd);
        }
        else
        {
            summary.AllColumnsHaveSoil = false;
        }

        if (hasWater)
        {
            summary.HasMaterial = true;
            summary.MinimumMaterialStart = Math.Min(
                summary.MinimumMaterialStart,
                profile.WaterStart);
            summary.MaximumMaterialEnd = Math.Max(
                summary.MaximumMaterialEnd,
                profile.WaterEnd);
            summary.WaterStartMinimum = Math.Min(
                summary.WaterStartMinimum,
                profile.WaterStart);
            summary.WaterStartMaximum = Math.Max(
                summary.WaterStartMaximum,
                profile.WaterStart);
            summary.WaterEndMinimum = Math.Min(
                summary.WaterEndMinimum,
                profile.WaterEnd);
            summary.WaterEndMaximum = Math.Max(
                summary.WaterEndMaximum,
                profile.WaterEnd);
        }
        else
        {
            summary.AllColumnsHaveWater = false;
        }

    }
}

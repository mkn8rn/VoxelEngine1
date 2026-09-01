using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.WorldGeneration.Native;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;
using Supprocom.OpenSimplexNoise;

namespace MVoxelEngine1.Tests;

public sealed class NativeColumnProfileGeneratorTests
{
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
        var layout = new NativeGtrtSessionLayout(
            size,
            chunkSizeY: 16,
            size,
            lod1Radius: 0,
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
                    continue;

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
                in nativeBiome,
                noise));
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
        });
    }

    [Fact]
    public void GenerationWorkspaceRejectsConcurrentReuse()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 0,
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
        var harness = new ProfileGenerationHarness(
            nativeBiome,
            new OpenSimplexNoise(seed));
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 16,
            chunkSizeY: 16,
            chunkSizeZ: 16,
            lod1Radius: 0,
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
        scoped in BlockColumnProfile profile)
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
        private readonly OpenSimplexNoise noise;

        internal ProfileGenerationHarness(
            NativeBiomeDescriptor biome,
            OpenSimplexNoise noise)
        {
            this.biome = biome;
            this.noise = noise;
            Action = Execute;
        }

        internal NativeLeaseAction<byte> Action { get; }

        internal bool Result { get; set; }

        private void Execute(scoped NativeLeaseView<byte> owner)
        {
            var session = new NativeGtrtSessionView(owner.AsSpan());
            Result =
                session.TryClaimGeneration(out NativeWorkItem work) &&
                NativeColumnProfileGenerator.TryGenerate(
                    ref session,
                    workerIndex: 0,
                    in work,
                    biomeIndex: 0,
                    in biome,
                    noise);
        }
    }
}

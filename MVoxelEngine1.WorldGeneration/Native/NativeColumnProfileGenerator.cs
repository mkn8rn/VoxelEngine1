using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeColumnSummary
{
    internal int HasMaterial;
    internal int MinimumMaterialStart;
    internal int MaximumMaterialEnd;
    internal int AllColumnsHaveStone;
    internal int StoneStartMinimum;
    internal int StoneStartMaximum;
    internal int StoneEndMinimum;
    internal int StoneEndMaximum;
    internal int AllColumnsHaveSoil;
    internal int SoilStartMinimum;
    internal int SoilStartMaximum;
    internal int SoilEndMinimum;
    internal int SoilEndMaximum;
    internal int AllColumnsHaveWater;
    internal int WaterStartMinimum;
    internal int WaterStartMaximum;
    internal int WaterEndMinimum;
    internal int WaterEndMaximum;

    internal static NativeColumnSummary CreateEmpty() => new()
    {
        MinimumMaterialStart = int.MaxValue,
        MaximumMaterialEnd = int.MinValue,
        AllColumnsHaveStone = 1,
        StoneStartMinimum = int.MaxValue,
        StoneStartMaximum = int.MinValue,
        StoneEndMinimum = int.MaxValue,
        StoneEndMaximum = int.MinValue,
        AllColumnsHaveSoil = 1,
        SoilStartMinimum = int.MaxValue,
        SoilStartMaximum = int.MinValue,
        SoilEndMinimum = int.MaxValue,
        SoilEndMaximum = int.MinValue,
        AllColumnsHaveWater = 1,
        WaterStartMinimum = int.MaxValue,
        WaterStartMaximum = int.MinValue,
        WaterEndMinimum = int.MaxValue,
        WaterEndMaximum = int.MinValue
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(scoped in BlockColumnProfile profile)
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
            HasMaterial = 1;
            if (profile.StoneStart < MinimumMaterialStart)
                MinimumMaterialStart = profile.StoneStart;
            if (profile.StoneEnd > MaximumMaterialEnd)
                MaximumMaterialEnd = profile.StoneEnd;
            if (profile.StoneStart < StoneStartMinimum)
                StoneStartMinimum = profile.StoneStart;
            if (profile.StoneStart > StoneStartMaximum)
                StoneStartMaximum = profile.StoneStart;
            if (profile.StoneEnd < StoneEndMinimum)
                StoneEndMinimum = profile.StoneEnd;
            if (profile.StoneEnd > StoneEndMaximum)
                StoneEndMaximum = profile.StoneEnd;
        }
        else
        {
            AllColumnsHaveStone = 0;
        }

        if (hasSoil)
        {
            HasMaterial = 1;
            if (profile.SoilStart < MinimumMaterialStart)
                MinimumMaterialStart = profile.SoilStart;
            if (profile.SoilEnd > MaximumMaterialEnd)
                MaximumMaterialEnd = profile.SoilEnd;
            if (profile.SoilStart < SoilStartMinimum)
                SoilStartMinimum = profile.SoilStart;
            if (profile.SoilStart > SoilStartMaximum)
                SoilStartMaximum = profile.SoilStart;
            if (profile.SoilEnd < SoilEndMinimum)
                SoilEndMinimum = profile.SoilEnd;
            if (profile.SoilEnd > SoilEndMaximum)
                SoilEndMaximum = profile.SoilEnd;
        }
        else
        {
            AllColumnsHaveSoil = 0;
        }

        if (hasWater)
        {
            HasMaterial = 1;
            if (profile.WaterStart < MinimumMaterialStart)
                MinimumMaterialStart = profile.WaterStart;
            if (profile.WaterEnd > MaximumMaterialEnd)
                MaximumMaterialEnd = profile.WaterEnd;
            if (profile.WaterStart < WaterStartMinimum)
                WaterStartMinimum = profile.WaterStart;
            if (profile.WaterStart > WaterStartMaximum)
                WaterStartMaximum = profile.WaterStart;
            if (profile.WaterEnd < WaterEndMinimum)
                WaterEndMinimum = profile.WaterEnd;
            if (profile.WaterEnd > WaterEndMaximum)
                WaterEndMaximum = profile.WaterEnd;
        }
        else
        {
            AllColumnsHaveWater = 0;
        }
    }
}

internal static class NativeColumnProfileGenerator
{
    internal static bool TryGenerate(
        scoped ref NativeGtrtSessionView session,
        int workerIndex,
        scoped in NativeWorkItem work,
        int biomeIndex,
        scoped in NativeBiomeDescriptor biome)
    {
        if (work.Kind != NativeWorkKind.GenerateColumn ||
            work.State != NativeWorkState.Claimed ||
            work.Epoch != session.State.SessionEpoch ||
            (uint)work.RecordIndex >= (uint)session.ColumnCount ||
            biomeIndex < 0)
        {
            session.Fail(NativeGtrtFailureCode.InvalidProfileGeneration);
            return false;
        }

        if (!session.TryAcquireGenerationWorkspace(workerIndex))
            return false;

        try
        {
            return GenerateCore(
                ref session,
                workerIndex,
                in work,
                biomeIndex,
                in biome);
        }
        finally
        {
            session.ReleaseGenerationWorkspace(workerIndex);
        }
    }

    private static bool GenerateCore(
        scoped ref NativeGtrtSessionView session,
        int workerIndex,
        scoped in NativeWorkItem work,
        int biomeIndex,
        scoped in NativeBiomeDescriptor biome)
    {
        int profileCount = session.ProfilesPerColumn;
        int sizeX = session.ChunkSizeX;
        int sizeZ = session.ChunkSizeZ;
        Span<float> values = session.GetGenerationFloatScratch(workerIndex);
        Span<float> heights = values.Slice(0, profileCount);
        Span<float> noiseValues = values.Slice(profileCount, profileCount);
        ref NativeColumnRecord column =
            ref session.Columns[work.RecordIndex];
        Span<BlockColumnProfile> profiles =
            session.GetColumnProfiles(work.RecordIndex);

        int baseWorldX = unchecked(column.ChunkX * sizeX);
        int baseWorldZ = unchecked(column.ChunkZ * sizeZ);
        NativeOpenSimplexNoiseState noise = session.NoiseState;
        Quadrant.FillHeightMap(
            noise.Permutation,
            noise.Permutation2D,
            baseWorldX,
            baseWorldZ,
            sizeX,
            sizeZ,
            heights);
        TerrainGenerationUtils.FillSmoothValueNoise01(
            baseWorldX,
            baseWorldZ,
            sizeX,
            sizeZ,
            session.State.Seed,
            noiseValues,
            session.GetGenerationXScratch(workerIndex),
            session.GetGenerationZScratch(workerIndex),
            session.GetGenerationLatticeScratch(workerIndex));

        TerrainMaterialSpanParameters parameters = new(
            biome.StoneMinY,
            biome.StoneMaxY,
            biome.StoneMinDepth,
            biome.StoneMaxDepth,
            biome.SoilMinY,
            biome.SoilMaxY,
            biome.SoilMinDepth,
            biome.SoilMaxDepth,
            biome.WaterLevel);
        NativeColumnSummary summary = NativeColumnSummary.CreateEmpty();
        for (int x = 0; x < sizeX; x++)
        {
            int rowOffset = x * sizeZ;
            int previousXOffset = x == 0
                ? rowOffset
                : rowOffset - sizeZ;
            int nextXOffset = x == sizeX - 1
                ? rowOffset
                : rowOffset + sizeZ;
            for (int z = 0; z < sizeZ; z++)
            {
                int profileIndex = rowOffset + z;
                int previousZ = z == 0 ? 0 : z - 1;
                int nextZ = z == sizeZ - 1 ? z : z + 1;
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
                        in parameters,
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
                summary.Add(in profile);
            }
        }

        column.BiomeIndex = biomeIndex;
        column.GenerationEpoch = work.Epoch;
        session.ColumnSummaries[work.RecordIndex] = summary;
        return true;
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;
internal static class NativeColumnProfileGenerator
{
    internal static bool TryGenerate(scoped ref NativeGtrtSessionView session, int workerIndex, scoped ref readonly NativeWorkItem work, int biomeIndex, scoped in NativeBiomeDescriptor biome)
    {
        if (work.Kind != NativeWorkKind.GenerateColumn || work.State != NativeWorkState.Claimed || work.Epoch != session.State.SessionEpoch || (uint)work.RecordIndex >= (uint)session.ColumnCount || biomeIndex < 0)
        {
            session.Fail(NativeGtrtFailureCode.InvalidProfileGeneration);
            session.TryAbandonGeneration(in work);
            return false;
        }

        if (!session.TryAcquireGenerationWorkspace(workerIndex))
        {
            session.TryAbandonGeneration(in work);
            return false;
        }

        bool keepClaim = false;
        try
        {
            if (session.CancellationRequested)
                return false;
            bool generated = GenerateCore(ref session, workerIndex, in work, biomeIndex, in biome);
            keepClaim = generated && !session.CancellationRequested;
            return keepClaim;
        }
        finally
        {
            if (!keepClaim)
                session.TryAbandonGeneration(in work);
            session.ReleaseGenerationWorkspace(workerIndex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool GenerateCore(scoped ref NativeGtrtSessionView session, int workerIndex, scoped ref readonly NativeWorkItem work, int biomeIndex, scoped in NativeBiomeDescriptor biome)
    {
        int profileCount = session.ProfilesPerColumn;
        int sizeX = session.ChunkSizeX;
        int sizeZ = session.ChunkSizeZ;
        Span<float> values = session.GetGenerationFloatScratch(workerIndex);
        Span<float> heights = values.Slice(0, profileCount);
        Span<float> noiseValues = values.Slice(profileCount, profileCount);
        ref NativeColumnRecord column = ref session.Columns[work.RecordIndex];
        Span<BlockColumnProfile> profiles = session.GetColumnProfiles(work.RecordIndex);
        int baseWorldX = unchecked(column.ChunkX * sizeX);
        int baseWorldZ = unchecked(column.ChunkZ * sizeZ);
        NativeOpenSimplexNoiseState noise = session.NoiseState;
        NativeHeightMap.FillHeightMap(noise.Permutation, noise.Permutation2D, baseWorldX, baseWorldZ, sizeX, sizeZ, heights);
        TerrainGenerationUtils.FillSmoothValueNoise01(baseWorldX, baseWorldZ, sizeX, sizeZ, session.State.Seed, noiseValues, session.GetGenerationXScratch(workerIndex), session.GetGenerationZScratch(workerIndex), session.GetGenerationLatticeScratch(workerIndex));
        TerrainMaterialSpanParameters parameters = new(biome.StoneMinY, biome.StoneMaxY, biome.StoneMinDepth, biome.StoneMaxDepth, biome.SoilMinY, biome.SoilMaxY, biome.SoilMinDepth, biome.SoilMaxDepth, biome.WaterLevel);
        NativeColumnSummary summary = NativeColumnSummary.CreateEmpty();
        for (int x = 0; x < sizeX; x++)
        {
            int rowOffset = x * sizeZ;
            int previousXOffset = x == 0 ? rowOffset : rowOffset - sizeZ;
            int nextXOffset = x == sizeX - 1 ? rowOffset : rowOffset + sizeZ;
            for (int z = 0; z < sizeZ; z++)
            {
                int profileIndex = rowOffset + z;
                int previousZ = z == 0 ? 0 : z - 1;
                int nextZ = z == sizeZ - 1 ? z : z + 1;
                int surface = (int)heights[profileIndex];
                float dx = heights[nextXOffset + z] - heights[previousXOffset + z];
                float dz = heights[rowOffset + nextZ] - heights[rowOffset + previousZ];
                float gradient = MathF.Sqrt(dx * dx + dz * dz);
                float slope = MathF.Min(1f, gradient / 6f);
                var spans = TerrainGenerationUtils.DeriveWorldStoneSoilSpansFromNoise(surface, in parameters, slope, noiseValues[profileIndex]);
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
        if (session.HasGameSnapshot)
        {
            var game = session.GameSnapshot;
            column.ReplacementMode = NativeReplacementRules.ResolveMaterials(ref game, in biome, session.Materials, out column.ResolvedMaterials);
        }

        column.GenerationEpoch = work.Epoch;
        session.ColumnSummaries[work.RecordIndex] = summary;
        column.SummaryComputed = 1;
        return true;
    }
}

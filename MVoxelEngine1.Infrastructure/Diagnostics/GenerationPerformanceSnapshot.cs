using System.Diagnostics;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
    public sealed record GenerationPerformanceSnapshot
    {
        public required long Columns { get; init; }
        public required long Chunks { get; init; }
        public required long AllAirChunks { get; init; }
        public required long AllStoneChunks { get; init; }
        public required long AllSoilChunks { get; init; }
        public required long AllWaterChunks { get; init; }
        public required long NonUniformChunks { get; init; }
        public required double AggregatedProfileMilliseconds { get; init; }
        public required double HeightMapMilliseconds { get; init; }
        public required double SmoothValueNoiseMilliseconds { get; init; }
        public required double ProfileDerivationMilliseconds { get; init; }
        public required double VerticalClassificationMilliseconds { get; init; }
        public required double SpanMapMilliseconds { get; init; }
        public required double ChunkConstructionMilliseconds { get; init; }
        public required double UniformSectionMilliseconds { get; init; }
        public required double NonUniformGenerationMilliseconds { get; init; }
        public required double NonUniformColumnScanMilliseconds { get; init; }
        public required double NonUniformUniformSectionMilliseconds { get; init; }
        public required double NonUniformTerrainEmissionMilliseconds { get; init; }
        public required double NonUniformWaterEmissionMilliseconds { get; init; }
        public required double NonUniformCollapseMilliseconds { get; init; }
        public required double NonUniformFinalizeMilliseconds { get; init; }
        public required long FinalizedSections { get; init; }
        public required long ScratchSections { get; init; }
        public required long EscalatedScratchSections { get; init; }
        public required long EmptySections { get; init; }
        public required long UniformSections { get; init; }
        public required long PackedSections { get; init; }
        public required long MultiPackedSections { get; init; }
        public required long ExpandedSections { get; init; }
        public required double BoundaryPlaneMilliseconds { get; init; }
        public required double RegistrarMilliseconds { get; init; }
    }
}

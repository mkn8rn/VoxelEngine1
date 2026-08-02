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

    public static class GenerationPerformanceRecorder
    {
        private static long columns;
        private static long chunks;
        private static long allAirChunks;
        private static long allStoneChunks;
        private static long allSoilChunks;
        private static long allWaterChunks;
        private static long nonUniformChunks;
        private static long aggregatedProfileTicks;
        private static long verticalClassificationTicks;
        private static long spanMapTicks;
        private static long chunkConstructionTicks;
        private static long uniformSectionTicks;
        private static long nonUniformGenerationTicks;
        private static long nonUniformColumnScanTicks;
        private static long nonUniformUniformSectionTicks;
        private static long nonUniformTerrainEmissionTicks;
        private static long nonUniformWaterEmissionTicks;
        private static long nonUniformCollapseTicks;
        private static long nonUniformFinalizeTicks;
        private static long finalizedSections;
        private static long scratchSections;
        private static long escalatedScratchSections;
        private static long emptySections;
        private static long uniformSections;
        private static long packedSections;
        private static long multiPackedSections;
        private static long expandedSections;
        private static long boundaryPlaneTicks;
        private static long registrarTicks;

        public static void Reset()
        {
            Volatile.Write(ref columns, 0);
            Volatile.Write(ref chunks, 0);
            Volatile.Write(ref allAirChunks, 0);
            Volatile.Write(ref allStoneChunks, 0);
            Volatile.Write(ref allSoilChunks, 0);
            Volatile.Write(ref allWaterChunks, 0);
            Volatile.Write(ref nonUniformChunks, 0);
            Volatile.Write(ref aggregatedProfileTicks, 0);
            Volatile.Write(ref verticalClassificationTicks, 0);
            Volatile.Write(ref spanMapTicks, 0);
            Volatile.Write(ref chunkConstructionTicks, 0);
            Volatile.Write(ref uniformSectionTicks, 0);
            Volatile.Write(ref nonUniformGenerationTicks, 0);
            Volatile.Write(ref nonUniformColumnScanTicks, 0);
            Volatile.Write(ref nonUniformUniformSectionTicks, 0);
            Volatile.Write(ref nonUniformTerrainEmissionTicks, 0);
            Volatile.Write(ref nonUniformWaterEmissionTicks, 0);
            Volatile.Write(ref nonUniformCollapseTicks, 0);
            Volatile.Write(ref nonUniformFinalizeTicks, 0);
            Volatile.Write(ref finalizedSections, 0);
            Volatile.Write(ref scratchSections, 0);
            Volatile.Write(ref escalatedScratchSections, 0);
            Volatile.Write(ref emptySections, 0);
            Volatile.Write(ref uniformSections, 0);
            Volatile.Write(ref packedSections, 0);
            Volatile.Write(ref multiPackedSections, 0);
            Volatile.Write(ref expandedSections, 0);
            Volatile.Write(ref boundaryPlaneTicks, 0);
            Volatile.Write(ref registrarTicks, 0);
        }

        public static void RecordColumn(
            long profileTicks,
            long classificationTicks,
            long mapTicks,
            long constructionTicks,
            long registrationTicks)
        {
            Interlocked.Increment(ref columns);
            Interlocked.Add(ref aggregatedProfileTicks, profileTicks);
            Interlocked.Add(ref verticalClassificationTicks, classificationTicks);
            Interlocked.Add(ref spanMapTicks, mapTicks);
            Interlocked.Add(ref chunkConstructionTicks, constructionTicks);
            Interlocked.Add(ref registrarTicks, registrationTicks);
        }

        public static void RecordChunkKind(int uniformOverride)
        {
            Interlocked.Increment(ref chunks);
            switch (uniformOverride)
            {
                case 1:
                    Interlocked.Increment(ref allAirChunks);
                    break;
                case 2:
                    Interlocked.Increment(ref allStoneChunks);
                    break;
                case 3:
                    Interlocked.Increment(ref allSoilChunks);
                    break;
                case 4:
                    Interlocked.Increment(ref allWaterChunks);
                    break;
                default:
                    Interlocked.Increment(ref nonUniformChunks);
                    break;
            }
        }

        public static void RecordUniformSections(long elapsedTicks) =>
            Interlocked.Add(ref uniformSectionTicks, elapsedTicks);

        public static void RecordNonUniformGeneration(long elapsedTicks) =>
            Interlocked.Add(ref nonUniformGenerationTicks, elapsedTicks);

        public static void RecordNonUniformPhases(
            long columnScanTicks,
            long uniformSectionTicks,
            long terrainEmissionTicks,
            long waterEmissionTicks,
            long collapseTicks,
            long finalizeTicks)
        {
            Interlocked.Add(ref nonUniformColumnScanTicks, columnScanTicks);
            Interlocked.Add(ref nonUniformUniformSectionTicks, uniformSectionTicks);
            Interlocked.Add(ref nonUniformTerrainEmissionTicks, terrainEmissionTicks);
            Interlocked.Add(ref nonUniformWaterEmissionTicks, waterEmissionTicks);
            Interlocked.Add(ref nonUniformCollapseTicks, collapseTicks);
            Interlocked.Add(ref nonUniformFinalizeTicks, finalizeTicks);
        }

        public static void RecordFinalizedSections(
            long finalized,
            long scratch,
            long escalatedScratch,
            long empty,
            long uniform,
            long packed,
            long multiPacked,
            long expanded)
        {
            Interlocked.Add(ref finalizedSections, finalized);
            Interlocked.Add(ref scratchSections, scratch);
            Interlocked.Add(ref escalatedScratchSections, escalatedScratch);
            Interlocked.Add(ref emptySections, empty);
            Interlocked.Add(ref uniformSections, uniform);
            Interlocked.Add(ref packedSections, packed);
            Interlocked.Add(ref multiPackedSections, multiPacked);
            Interlocked.Add(ref expandedSections, expanded);
        }

        public static void RecordBoundaryPlanes(long elapsedTicks) =>
            Interlocked.Add(ref boundaryPlaneTicks, elapsedTicks);

        public static GenerationPerformanceSnapshot CreateSnapshot() => new()
        {
            Columns = Volatile.Read(ref columns),
            Chunks = Volatile.Read(ref chunks),
            AllAirChunks = Volatile.Read(ref allAirChunks),
            AllStoneChunks = Volatile.Read(ref allStoneChunks),
            AllSoilChunks = Volatile.Read(ref allSoilChunks),
            AllWaterChunks = Volatile.Read(ref allWaterChunks),
            NonUniformChunks = Volatile.Read(ref nonUniformChunks),
            AggregatedProfileMilliseconds = ToMilliseconds(Volatile.Read(ref aggregatedProfileTicks)),
            VerticalClassificationMilliseconds = ToMilliseconds(Volatile.Read(ref verticalClassificationTicks)),
            SpanMapMilliseconds = ToMilliseconds(Volatile.Read(ref spanMapTicks)),
            ChunkConstructionMilliseconds = ToMilliseconds(Volatile.Read(ref chunkConstructionTicks)),
            UniformSectionMilliseconds = ToMilliseconds(Volatile.Read(ref uniformSectionTicks)),
            NonUniformGenerationMilliseconds = ToMilliseconds(Volatile.Read(ref nonUniformGenerationTicks)),
            NonUniformColumnScanMilliseconds = ToMilliseconds(Volatile.Read(ref nonUniformColumnScanTicks)),
            NonUniformUniformSectionMilliseconds = ToMilliseconds(Volatile.Read(ref nonUniformUniformSectionTicks)),
            NonUniformTerrainEmissionMilliseconds = ToMilliseconds(Volatile.Read(ref nonUniformTerrainEmissionTicks)),
            NonUniformWaterEmissionMilliseconds = ToMilliseconds(Volatile.Read(ref nonUniformWaterEmissionTicks)),
            NonUniformCollapseMilliseconds = ToMilliseconds(Volatile.Read(ref nonUniformCollapseTicks)),
            NonUniformFinalizeMilliseconds = ToMilliseconds(Volatile.Read(ref nonUniformFinalizeTicks)),
            FinalizedSections = Volatile.Read(ref finalizedSections),
            ScratchSections = Volatile.Read(ref scratchSections),
            EscalatedScratchSections = Volatile.Read(ref escalatedScratchSections),
            EmptySections = Volatile.Read(ref emptySections),
            UniformSections = Volatile.Read(ref uniformSections),
            PackedSections = Volatile.Read(ref packedSections),
            MultiPackedSections = Volatile.Read(ref multiPackedSections),
            ExpandedSections = Volatile.Read(ref expandedSections),
            BoundaryPlaneMilliseconds = ToMilliseconds(Volatile.Read(ref boundaryPlaneTicks)),
            RegistrarMilliseconds = ToMilliseconds(Volatile.Read(ref registrarTicks))
        };

        public static long GetElapsedTicks(long startTimestamp) =>
            Stopwatch.GetElapsedTime(startTimestamp).Ticks;

        private static double ToMilliseconds(long ticks) =>
            TimeSpan.FromTicks(ticks).TotalMilliseconds;
    }
}

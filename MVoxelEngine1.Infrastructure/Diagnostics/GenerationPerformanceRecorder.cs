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
            BoundaryPlaneMilliseconds = ToMilliseconds(Volatile.Read(ref boundaryPlaneTicks)),
            RegistrarMilliseconds = ToMilliseconds(Volatile.Read(ref registrarTicks))
        };

        public static long GetElapsedTicks(long startTimestamp) =>
            Stopwatch.GetElapsedTime(startTimestamp).Ticks;

        private static double ToMilliseconds(long ticks) =>
            TimeSpan.FromTicks(ticks).TotalMilliseconds;
    }
}

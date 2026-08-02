using System.Diagnostics;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
    public sealed record MeshPerformanceSnapshot
    {
        public required long BuiltChunks { get; init; }
        public required long GeneratedSpanChunks { get; init; }
        public required long SectionChunks { get; init; }
        public required long GeneratedSpanOpaqueFaces { get; init; }
        public required long GeneratedSpanTransparentFaces { get; init; }
        public required long GeneratedSpanOpaqueRectangles { get; init; }
        public required long GeneratedSpanTransparentRectangles { get; init; }
        public required double AggregatedBuildMilliseconds { get; init; }
        public required double GeneratedSpanBuildMilliseconds { get; init; }
        public required double SectionBuildMilliseconds { get; init; }
        public required double GeneratedSpanCountPassMilliseconds { get; init; }
        public required double GeneratedSpanPreparationMilliseconds { get; init; }
        public required double GeneratedSpanWritePassMilliseconds { get; init; }
        public required long NeighborPlaneSnapshotChunks { get; init; }
        public required long NeighborOpaquePlaneSnapshotArrays { get; init; }
        public required long NeighborTransparentPlaneSnapshotArrays { get; init; }
        public required long NeighborOpaquePlaneSnapshotBytes { get; init; }
        public required long NeighborTransparentPlaneSnapshotBytes { get; init; }
        public required double NeighborPlaneSnapshotMilliseconds { get; init; }
    }

    public static class MeshPerformanceRecorder
    {
        private static long builtChunks;
        private static long generatedSpanChunks;
        private static long sectionChunks;
        private static long generatedSpanOpaqueFaces;
        private static long generatedSpanTransparentFaces;
        private static long generatedSpanOpaqueRectangles;
        private static long generatedSpanTransparentRectangles;
        private static long aggregatedBuildTicks;
        private static long generatedSpanBuildTicks;
        private static long sectionBuildTicks;
        private static long generatedSpanCountPassTicks;
        private static long generatedSpanPreparationTicks;
        private static long generatedSpanWritePassTicks;
        private static long neighborPlaneSnapshotChunks;
        private static long neighborOpaquePlaneSnapshotArrays;
        private static long neighborTransparentPlaneSnapshotArrays;
        private static long neighborOpaquePlaneSnapshotBytes;
        private static long neighborTransparentPlaneSnapshotBytes;
        private static long neighborPlaneSnapshotTicks;

        public static void Reset()
        {
            Volatile.Write(ref builtChunks, 0);
            Volatile.Write(ref generatedSpanChunks, 0);
            Volatile.Write(ref sectionChunks, 0);
            Volatile.Write(ref generatedSpanOpaqueFaces, 0);
            Volatile.Write(ref generatedSpanTransparentFaces, 0);
            Volatile.Write(ref generatedSpanOpaqueRectangles, 0);
            Volatile.Write(ref generatedSpanTransparentRectangles, 0);
            Volatile.Write(ref aggregatedBuildTicks, 0);
            Volatile.Write(ref generatedSpanBuildTicks, 0);
            Volatile.Write(ref sectionBuildTicks, 0);
            Volatile.Write(ref generatedSpanCountPassTicks, 0);
            Volatile.Write(ref generatedSpanPreparationTicks, 0);
            Volatile.Write(ref generatedSpanWritePassTicks, 0);
            Volatile.Write(ref neighborPlaneSnapshotChunks, 0);
            Volatile.Write(ref neighborOpaquePlaneSnapshotArrays, 0);
            Volatile.Write(ref neighborTransparentPlaneSnapshotArrays, 0);
            Volatile.Write(ref neighborOpaquePlaneSnapshotBytes, 0);
            Volatile.Write(ref neighborTransparentPlaneSnapshotBytes, 0);
            Volatile.Write(ref neighborPlaneSnapshotTicks, 0);
        }

        public static void RecordBuiltChunk(
            bool generatedSpans,
            long elapsedTicks)
        {
            Interlocked.Increment(ref builtChunks);
            Interlocked.Add(ref aggregatedBuildTicks, elapsedTicks);
            if (generatedSpans)
            {
                Interlocked.Add(ref generatedSpanBuildTicks, elapsedTicks);
                return;
            }

            Interlocked.Increment(ref sectionChunks);
            Interlocked.Add(ref sectionBuildTicks, elapsedTicks);
        }

        public static void RecordGeneratedSpanPhases(
            long countPassTicks,
            long preparationTicks,
            long writePassTicks,
            int opaqueFaces,
            int transparentFaces,
            int opaqueRectangles,
            int transparentRectangles)
        {
            Interlocked.Increment(ref generatedSpanChunks);
            Interlocked.Add(ref generatedSpanCountPassTicks, countPassTicks);
            Interlocked.Add(ref generatedSpanPreparationTicks, preparationTicks);
            Interlocked.Add(ref generatedSpanWritePassTicks, writePassTicks);
            Interlocked.Add(ref generatedSpanOpaqueFaces, opaqueFaces);
            Interlocked.Add(ref generatedSpanTransparentFaces, transparentFaces);
            Interlocked.Add(ref generatedSpanOpaqueRectangles, opaqueRectangles);
            Interlocked.Add(
                ref generatedSpanTransparentRectangles,
                transparentRectangles);
        }

        public static void RecordNeighborPlaneSnapshots(
            long elapsedTicks,
            long opaqueArrays,
            long transparentArrays,
            long opaqueBytes,
            long transparentBytes)
        {
            Interlocked.Increment(ref neighborPlaneSnapshotChunks);
            Interlocked.Add(ref neighborOpaquePlaneSnapshotArrays, opaqueArrays);
            Interlocked.Add(
                ref neighborTransparentPlaneSnapshotArrays,
                transparentArrays);
            Interlocked.Add(ref neighborOpaquePlaneSnapshotBytes, opaqueBytes);
            Interlocked.Add(
                ref neighborTransparentPlaneSnapshotBytes,
                transparentBytes);
            Interlocked.Add(ref neighborPlaneSnapshotTicks, elapsedTicks);
        }

        public static MeshPerformanceSnapshot CreateSnapshot() => new()
        {
            BuiltChunks = Volatile.Read(ref builtChunks),
            GeneratedSpanChunks = Volatile.Read(ref generatedSpanChunks),
            SectionChunks = Volatile.Read(ref sectionChunks),
            GeneratedSpanOpaqueFaces = Volatile.Read(ref generatedSpanOpaqueFaces),
            GeneratedSpanTransparentFaces = Volatile.Read(ref generatedSpanTransparentFaces),
            GeneratedSpanOpaqueRectangles = Volatile.Read(
                ref generatedSpanOpaqueRectangles),
            GeneratedSpanTransparentRectangles = Volatile.Read(
                ref generatedSpanTransparentRectangles),
            AggregatedBuildMilliseconds = ToMilliseconds(
                Volatile.Read(ref aggregatedBuildTicks)),
            GeneratedSpanBuildMilliseconds = ToMilliseconds(
                Volatile.Read(ref generatedSpanBuildTicks)),
            SectionBuildMilliseconds = ToMilliseconds(
                Volatile.Read(ref sectionBuildTicks)),
            GeneratedSpanCountPassMilliseconds = ToMilliseconds(
                Volatile.Read(ref generatedSpanCountPassTicks)),
            GeneratedSpanPreparationMilliseconds = ToMilliseconds(
                Volatile.Read(ref generatedSpanPreparationTicks)),
            GeneratedSpanWritePassMilliseconds = ToMilliseconds(
                Volatile.Read(ref generatedSpanWritePassTicks)),
            NeighborPlaneSnapshotChunks = Volatile.Read(
                ref neighborPlaneSnapshotChunks),
            NeighborOpaquePlaneSnapshotArrays = Volatile.Read(
                ref neighborOpaquePlaneSnapshotArrays),
            NeighborTransparentPlaneSnapshotArrays = Volatile.Read(
                ref neighborTransparentPlaneSnapshotArrays),
            NeighborOpaquePlaneSnapshotBytes = Volatile.Read(
                ref neighborOpaquePlaneSnapshotBytes),
            NeighborTransparentPlaneSnapshotBytes = Volatile.Read(
                ref neighborTransparentPlaneSnapshotBytes),
            NeighborPlaneSnapshotMilliseconds = ToMilliseconds(
                Volatile.Read(ref neighborPlaneSnapshotTicks))
        };

        public static long GetElapsedTicks(long startTimestamp) =>
            Stopwatch.GetElapsedTime(startTimestamp).Ticks;

        private static double ToMilliseconds(long ticks) =>
            TimeSpan.FromTicks(ticks).TotalMilliseconds;
    }
}

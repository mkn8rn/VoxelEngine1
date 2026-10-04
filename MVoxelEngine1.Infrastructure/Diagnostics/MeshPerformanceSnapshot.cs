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
}

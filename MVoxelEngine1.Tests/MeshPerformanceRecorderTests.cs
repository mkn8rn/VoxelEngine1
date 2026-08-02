using MVoxelEngine1.Infrastructure.Diagnostics;

namespace MVoxelEngine1.Tests
{
    public class MeshPerformanceRecorderTests
    {
        [Fact]
        public void SnapshotSeparatesGeneratedAndSectionWork()
        {
            MeshPerformanceRecorder.Reset();
            MeshPerformanceRecorder.RecordBuiltChunk(
                generatedSpans: true,
                elapsedTicks: TimeSpan.FromMilliseconds(7).Ticks);
            MeshPerformanceRecorder.RecordBuiltChunk(
                generatedSpans: false,
                elapsedTicks: TimeSpan.FromMilliseconds(3).Ticks);
            MeshPerformanceRecorder.RecordGeneratedSpanPhases(
                TimeSpan.FromMilliseconds(2).Ticks,
                TimeSpan.FromMilliseconds(1).Ticks,
                TimeSpan.FromMilliseconds(4).Ticks,
                opaqueFaces: 12,
                transparentFaces: 5);

            MeshPerformanceSnapshot snapshot =
                MeshPerformanceRecorder.CreateSnapshot();

            Assert.Equal(2, snapshot.BuiltChunks);
            Assert.Equal(1, snapshot.GeneratedSpanChunks);
            Assert.Equal(1, snapshot.SectionChunks);
            Assert.Equal(12, snapshot.GeneratedSpanOpaqueFaces);
            Assert.Equal(5, snapshot.GeneratedSpanTransparentFaces);
            Assert.Equal(10, snapshot.AggregatedBuildMilliseconds);
            Assert.Equal(7, snapshot.GeneratedSpanBuildMilliseconds);
            Assert.Equal(3, snapshot.SectionBuildMilliseconds);
            Assert.Equal(2, snapshot.GeneratedSpanCountPassMilliseconds);
            Assert.Equal(1, snapshot.GeneratedSpanPreparationMilliseconds);
            Assert.Equal(4, snapshot.GeneratedSpanWritePassMilliseconds);
        }
    }
}

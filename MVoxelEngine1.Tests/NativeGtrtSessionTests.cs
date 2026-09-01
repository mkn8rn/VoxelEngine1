using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Tests;

public sealed class NativeGtrtSessionTests
{
    [Fact]
    public void DenseLayoutMapsEveryResidentCoordinateExactlyOnce()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);

        Assert.Equal(2, layout.ResidentRadius);
        Assert.Equal(5, layout.ColumnWidth);
        Assert.Equal(3, layout.VerticalChunkCount);
        Assert.Equal(25, layout.ColumnCount);
        Assert.Equal(75, layout.ChunkCount);
        Assert.Equal(400, layout.ProfileCount);
        Assert.Equal(0, layout.GetColumnIndex(-2, -2));
        Assert.Equal(24, layout.GetColumnIndex(2, 2));
        Assert.Equal(0, layout.GetChunkIndex(-2, -1, -2));
        Assert.Equal(74, layout.GetChunkIndex(2, 1, 2));
        Assert.Equal(-1, layout.GetColumnIndex(-3, 0));
        Assert.Equal(-1, layout.GetChunkIndex(0, 2, 0));
    }

    [Fact]
    public void NativeSessionOwnsInitializedGridJobsProfilesAndSeed()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 8,
            chunkSizeZ: 4,
            lod1Radius: 1);
        var session = NativeGtrtSession.Create(layout);
        try
        {
            session.PublishSeed(123456);
            session.Access(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                Assert.Equal(123456, view.State.Seed);
                Assert.Equal(1, view.State.SessionEpoch);
                Assert.Equal(1, view.State.PublicationState);
                Assert.Equal(layout.ColumnCount, view.State.RemainingColumns);
                Assert.Equal(layout.ChunkCount, view.State.RemainingChunks);
                Assert.Equal(layout.ColumnCount, view.Columns.Length);
                Assert.Equal(layout.ProfileCount, view.Profiles.Length);
                Assert.Equal(layout.ChunkCount, view.Chunks.Length);
                Assert.Equal(layout.ColumnCount, view.GenerationJobs.Length);
                Assert.Equal(layout.ChunkCount, view.MeshJobs.Length);
                Assert.Equal(layout.ChunkCount, view.Packets.Length);

                for (int index = 0; index < view.Columns.Length; index++)
                {
                    NativeColumnRecord column = view.Columns[index];
                    Assert.Equal(index, view.GetColumnIndex(
                        column.ChunkX,
                        column.ChunkZ));
                    Assert.Equal(index * layout.ProfilesPerColumn,
                        column.ProfileOffset);
                    Assert.Equal(-1, column.BiomeIndex);
                    Assert.Equal(NativeColumnState.Empty, column.State);
                    Assert.Equal(index, view.GenerationJobs[index].RecordIndex);
                    Assert.Equal(
                        NativeWorkKind.GenerateColumn,
                        view.GenerationJobs[index].Kind);
                }

                for (int index = 0; index < view.Chunks.Length; index++)
                {
                    NativeChunkRecord chunk = view.Chunks[index];
                    Assert.Equal(index, view.GetChunkIndex(
                        chunk.ChunkX,
                        chunk.ChunkY,
                        chunk.ChunkZ));
                    Assert.Equal(chunk.ColumnIndex * layout.ProfilesPerColumn,
                        chunk.ProfileOffset);
                    Assert.Equal(index, chunk.PacketIndex);
                    Assert.Equal(NativeChunkState.Empty, chunk.State);
                    Assert.Equal(index, view.MeshJobs[index].RecordIndex);
                    Assert.Equal(
                        NativeWorkKind.BuildChunkMesh,
                        view.MeshJobs[index].Kind);
                }

                foreach (var profile in view.Profiles)
                {
                    Assert.Equal(-1, profile.StoneStart);
                    Assert.Equal(-1, profile.StoneEnd);
                    Assert.Equal(-1, profile.SoilStart);
                    Assert.Equal(-1, profile.SoilEnd);
                    Assert.Equal(-1, profile.WaterStart);
                    Assert.Equal(-1, profile.WaterEnd);
                }
            });

            Assert.Throws<InvalidOperationException>(() =>
                session.PublishSeed(123456));
        }
        finally
        {
            session.Dispose();
        }

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            session.Access(static _ => { }));
    }
}

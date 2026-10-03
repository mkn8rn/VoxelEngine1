using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeMaterializedIndexTests
{
    private const int Count = 1024;

    [Fact]
    public void NativeCoordinateIndexSurvivesCollisionsRemovalAndSignedCoordinateExtremes()
    {
        using NativeGtrtSession session = CreateSession();
        session.Access(Import);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            for (int index = 0; index < Count; index++)
            {
                (int x, int y, int z) = Coordinates(index);
                Assert.Equal(index, view.FindMaterializedChunkIndex(x, y, z));
                Assert.Equal(-1, view.FindMaterializedChunkIndex(x, y + 1, z));
            }
            for (int index = 0; index < Count; index += 3)
            {
                (int x, int y, int z) = Coordinates(index);
                view.RemoveMaterializedChunkIndex(index);
                view.MaterializedChunks[index].State = 0;
                Assert.Equal(-1, view.FindMaterializedChunkIndex(x, y, z));
            }
            for (int index = 0; index < Count; index++)
            {
                (int x, int y, int z) = Coordinates(index);
                Assert.Equal(index % 3 == 0 ? -1 : index, view.FindMaterializedChunkIndex(x, y, z));
            }
            for (int index = 0; index < Count; index += 3)
            {
                ref NativeMaterializedChunkRecord chunk = ref view.MaterializedChunks[index];
                chunk.ChunkY += 2;
                chunk.State = 1;
                view.IndexMaterializedChunk(index);
            }
            for (int index = 0; index < Count; index++)
            {
                (int x, int y, int z) = Coordinates(index);
                Assert.Equal(index, view.FindMaterializedChunkIndex(x, y + (index % 3 == 0 ? 2 : 0), z));
            }
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void NativeCoordinateLookupAllocatesNoManagedMemory()
    {
        using NativeGtrtSession session = CreateSession();
        session.Access(Import);
        session.PublishSeed(123456);
        int checksum = 0;
        NativeLeaseAction<byte> query = owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            checksum = 0;
            for (int index = 0; index < Count; index++)
            {
                (int x, int y, int z) = Coordinates(index);
                checksum += view.FindMaterializedChunkIndex(x, y, z);
            }
        };
        session.Access(query);
        using var allocationScope = new NoGcAllocationScope();
        long before = GC.GetAllocatedBytesForCurrentThread();
        session.Access(query);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Count * (Count - 1) / 2, checksum);
        Assert.Equal(0, allocated);
    }

    private static void Import(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        for (int index = 0; index < Count; index++)
        {
            (int x, int y, int z) = Coordinates(index);
            Assert.True(NativeMaterializedTerrain.TryImportChunk(ref view, x, y, z, true, 6, out int created));
            Assert.Equal(index, created);
        }
    }

    private static (int x, int y, int z) Coordinates(int index) =>
        (index % 2 == 0 ? int.MinValue + index : int.MaxValue - index, index - Count / 2, -index * 37);

    private static NativeGtrtSession CreateSession() => NativeGtrtSession.Create(new NativeGtrtSessionLayout(
        4, 8, 4, 1, NativeTerrainMaterialSet.CreateConventional(),
        materializedChunkCapacity: Count, materializedSectionCapacity: 1, materializedRawSectionCapacity: 0));
}

using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeSessionCapacityTests
{
    [Fact]
    public void GrowthUsesTheRequiredCapacityWhenDoublingWouldOverflowTheByteOwner()
    {
        NativeTerrainMaterialSet materials = NativeTerrainMaterialSet.CreateConventional();
        var original = new NativeGtrtSessionLayout(16, 16, 16, 0, materials,
            materializedChunkCapacity: 1, materializedSectionCapacity: 150_001,
            materializedRawSectionCapacity: 150_000);
        NativeGtrtSessionLayout expanded = NativeGtrtSession.SelectExpandedLayout(new NativeGtrtSessionHeader(original),
            materials, new NativeMaterializedStorageRequirements(1, 150_001, 150_001), NativeGtrtSession.MaximumSessionByteCount);
        Assert.Equal(150_001, expanded.MaterializedRawSectionCapacity);
        Assert.True(expanded.TotalByteCount > original.TotalByteCount);
        Assert.True(expanded.TotalByteCount <= NativeGtrtSession.MaximumSessionByteCount);
    }

    [Fact]
    public void GrowthCopiesRawPackedAndPacketPayloadsAndRebuildsTheCoordinateIndex()
    {
        using NativeGtrtSession session = CreateSession();
        session.Access(Import);
        int originalBytes = 0;
        session.Access(owner => originalBytes = new NativeGtrtSessionView(owner.AsSpan()).SessionHeader.TotalByteCount);
        session.EnsureMaterializedCapacity(new NativeMaterializedStorageRequirements(3, 3, 2));
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.True(view.SessionHeader.TotalByteCount > originalBytes);
            Assert.Equal(4, view.MaterializedChunkCapacity);
            Assert.Equal(4, view.MaterializedSectionCapacity);
            Assert.Equal(2, view.MaterializedRawSectionCapacity);
            Assert.Equal(2, view.State.MaterializedChunkCount);
            Assert.Equal(2, view.State.MaterializedSectionCount);
            Assert.Equal(1, view.State.MaterializedRawSectionCount);
            Assert.Equal(0, view.FindMaterializedChunkIndex(-16, 7, 24));
            Assert.Equal(1, view.FindMaterializedChunkIndex(80, -3, 99));
            Assert.True(NativeMaterializedTerrain.TryGetStoredBlock(ref view, 0, 0, 0, 0, out ushort first));
            Assert.Equal(11, first);
            Assert.True(NativeMaterializedTerrain.TryGetStoredBlock(ref view, 0, 1, 0, 0, out ushort second));
            Assert.Equal(6, second);
            Assert.True(NativeMaterializedTerrain.TryGetStoredBlock(ref view, 1, 0, 0, 0, out ushort packed));
            Assert.Equal(11, packed);
            Assert.Equal(128, view.State.MaterializedPackedWordCursor);
            Assert.Equal(2, view.State.MaterializedPaletteCursor);
            Assert.Equal(20, view.State.PacketWordCursor);
            Assert.Equal(777, view.Packets[0].RenderDataId);
            for (int index = 0; index < 20; index++)
                Assert.Equal((uint)(100 + index), view.PacketWords[index]);
            Assert.Equal(17, view.Profiles[0].StoneEnd);
            Assert.True(NativeMaterializedTerrain.TryImportChunk(ref view, -90, 2, 7, true, 6, out int created));
            Assert.Equal(2, created);
            Assert.Equal(2, view.FindMaterializedChunkIndex(-90, 2, 7));
            Assert.All(view.MaterializedSectionMaps.ToArray()[2..], entry => Assert.Equal(-1, entry));
        });
    }

    [Fact]
    public void RejectedGrowthKeepsTheOriginalOwnerAndPayloadUsable()
    {
        using NativeGtrtSession session = CreateSession();
        session.Access(Import);
        byte[] before = [];
        session.Access(owner => before = owner.AsSpan().ToArray());
        Assert.Throws<InvalidOperationException>(() => session.EnsureMaterializedCapacity(
            new NativeMaterializedStorageRequirements(3, 3, 2), maximumByteCount: before.Length));
        session.Access(owner => Assert.Equal(before, owner.AsSpan().ToArray()));
        session.EnsureMaterializedCapacity(new NativeMaterializedStorageRequirements(3, 3, 2));
        session.Access(owner => Assert.Equal(0,
            new NativeGtrtSessionView(owner.AsSpan()).FindMaterializedChunkIndex(-16, 7, 24)));
    }

    [Fact]
    public void GrowthRejectsAnActiveNativeTransaction()
    {
        using NativeGtrtSession session = CreateSession();
        session.Access(owner => new NativeGtrtSessionView(owner.AsSpan()).State.TransactionOpen = 1);
        Assert.Throws<InvalidOperationException>(() => session.EnsureMaterializedCapacity(
            new NativeMaterializedStorageRequirements(3, 3, 2)));
        session.Access(owner => new NativeGtrtSessionView(owner.AsSpan()).State.TransactionOpen = 0);
        session.EnsureMaterializedCapacity(new NativeMaterializedStorageRequirements(3, 3, 2));
    }

    private static NativeGtrtSession CreateSession() => NativeGtrtSession.Create(new NativeGtrtSessionLayout(
        16, 16, 16, 0, NativeTerrainMaterialSet.CreateConventional(),
        materializedChunkCapacity: 2, materializedSectionCapacity: 2, materializedRawSectionCapacity: 1,
        materializedPaletteCapacity: 2, materializedPackedWordCapacity: 128));

    private static void Import(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        Assert.True(NativeMaterializedTerrain.TryImportChunk(ref view, -16, 7, 24, false, 0, out int rawChunk));
        Span<ushort> raw = stackalloc ushort[VoxelSection.VoxelCount];
        raw.Fill(6);
        raw[0] = 11;
        Assert.True(NativeMaterializedTerrain.TryImportRawSection(ref view, rawChunk, 0, raw));
        Assert.True(NativeMaterializedTerrain.TryImportChunk(ref view, 80, -3, 99, false, 0, out int packedChunk));
        Span<ushort> palette = stackalloc ushort[] { 6, 11 };
        Span<uint> words = stackalloc uint[128];
        words.Fill(uint.MaxValue);
        Assert.True(NativeMaterializedTerrain.TryImportPackedSection(ref view, packedChunk, 0, 1, palette, words));
        view.State.PacketWordCursor = 20;
        view.Packets[0].RenderDataId = 777;
        for (int index = 0; index < 20; index++)
            view.PacketWords[index] = (uint)(100 + index);
        view.Profiles[0].StoneEnd = 17;
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal static class NativeGtrtSessionCloner
{
    internal static void Clone(scoped Span<byte> bytes, scoped ReadOnlySpan<byte> source, NativeGtrtSessionHeader old, NativeGtrtSessionLayout layout)
    {
        NativeGtrtSessionState state = MemoryMarshal.Read<NativeGtrtSessionState>(source.Slice(old.StateOffset));
        bytes.Clear();
        var header = new NativeGtrtSessionHeader(layout);
        MemoryMarshal.Write(bytes, in header);
        MemoryMarshal.Cast<byte, int>(bytes.Slice(layout.MaterializedSectionMapOffset, checked(layout.MaterializedSectionMapCount * sizeof(int)))).Fill(-1);
        CopyRange<NativeGtrtSessionState>(bytes, source, old.StateOffset, layout.StateOffset, 1);
        CopyRange<byte>(bytes, source, old.NoiseStateOffset, layout.NoiseStateOffset, NativeOpenSimplexNoiseState.StateByteCount);
        CopyRange<NativeTerrainMaterialSet>(bytes, source, old.MaterialOffset, layout.MaterialOffset, 1);
        CopyRange<NativeColumnRecord>(bytes, source, old.ColumnOffset, layout.ColumnOffset, old.ColumnCount);
        CopyRange<BlockColumnProfile>(bytes, source, old.ProfileOffset, layout.ProfileOffset, old.ProfileCount);
        CopyRange<NativeColumnSummary>(bytes, source, old.ColumnSummaryOffset, layout.ColumnSummaryOffset, old.ColumnCount);
        CopyRange<NativeChunkRecord>(bytes, source, old.ChunkOffset, layout.ChunkOffset, old.ChunkCount);
        CopyRange<NativeWorkItem>(bytes, source, old.GenerationJobOffset, layout.GenerationJobOffset, old.ColumnCount);
        CopyRange<NativeWorkItem>(bytes, source, old.MeshJobOffset, layout.MeshJobOffset, old.ChunkCount);
        CopyRange<NativeRenderPacketRecord>(bytes, source, old.PacketOffset, layout.PacketOffset, old.ChunkCount);
        CopyRange<NativeReadySlot>(bytes, source, old.MeshReadyOffset, layout.MeshReadyOffset, old.RequiredChunkCount);
        CopyRange<uint>(bytes, source, old.PacketWordOffset, layout.PacketWordOffset, state.PacketWordCursor);
        CopyRange<byte>(bytes, source, old.GameSnapshotOffset, layout.GameSnapshotOffset, old.GameSnapshotByteCount);
        CopyRange<NativeMaterializedChunkRecord>(bytes, source, old.MaterializedChunkOffset, layout.MaterializedChunkOffset, state.MaterializedChunkCount);
        CopyRange<int>(bytes, source, old.MaterializedSectionMapOffset, layout.MaterializedSectionMapOffset, checked(state.MaterializedChunkCount * old.SectionsPerChunk));
        CopyRange<NativeMaterializedSectionRecord>(bytes, source, old.MaterializedSectionOffset, layout.MaterializedSectionOffset, state.MaterializedSectionCount);
        CopyRange<ushort>(bytes, source, old.MaterializedRawVoxelOffset, layout.MaterializedRawVoxelOffset, checked(state.MaterializedRawSectionCount * VoxelSection.VoxelCount));
        CopyRange<ushort>(bytes, source, old.MaterializedPaletteOffset, layout.MaterializedPaletteOffset, state.MaterializedPaletteCursor);
        CopyRange<uint>(bytes, source, old.MaterializedPackedWordOffset, layout.MaterializedPackedWordOffset, state.MaterializedPackedWordCursor);
        CopyRange<NativePacketWordRange>(bytes, source, old.Streaming.FreeRanges, layout.Streaming.FreeRanges, state.FreeRangeCount);
        if (layout.PacketWordCapacity > old.PacketWordCapacity)
        {
            var expanded = new NativeGtrtSessionView(bytes);
            expanded.AddPacketStorageTail(old.PacketWordCapacity);
        }

        Span<int> index = MemoryMarshal.Cast<byte, int>(bytes.Slice(layout.MaterializedIndexOffset, checked(layout.MaterializedIndexCapacity * sizeof(int))));
        ReadOnlySpan<NativeMaterializedChunkRecord> chunks = MemoryMarshal.Cast<byte, NativeMaterializedChunkRecord>(bytes.Slice(layout.MaterializedChunkOffset, checked(state.MaterializedChunkCount * Unsafe.SizeOf<NativeMaterializedChunkRecord>())));
        for (int recordIndex = 0; recordIndex < chunks.Length; recordIndex++)
        {
            NativeMaterializedChunkRecord chunk = chunks[recordIndex];
            if (chunk.State == 0)
                continue;
            int slot = NativeGtrtSessionView.GetMaterializedSlot(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, index.Length);
            while (index[slot] != 0)
                slot = (slot + 1) & (index.Length - 1);
            index[slot] = recordIndex + 1;
        }
    }

    private static void CopyRange<T>(scoped Span<byte> bytes, scoped ReadOnlySpan<byte> source, int sourceOffset, int targetOffset, int count)
        where T : unmanaged
    {
        int byteCount = checked(count * Unsafe.SizeOf<T>());
        source.Slice(sourceOffset, byteCount).CopyTo(bytes.Slice(targetOffset, byteCount));
    }
}

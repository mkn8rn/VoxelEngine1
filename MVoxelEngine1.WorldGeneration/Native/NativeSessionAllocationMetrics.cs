using System.Runtime;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public readonly record struct NativeSessionAllocationMetrics(long OwnerLengthBytes, long OwnerCapacityBytes, int ResidentColumns, int RequiredChunks, int ProfileCount, int PacketWordCapacity, int PacketWordHighWaterCount, int MaterializedChunks, int MaterializedSections, int MaterializedRawSections, int MaterializedPaletteEntries, int MaterializedPackedWords)
{
    internal static NativeSessionAllocationMetrics Capture(NativeGtrtSession session)
    {
        NativeSessionAllocationMetrics metrics = default;
        session.Access((scoped NativeLeaseView<byte> owner) =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            metrics = new NativeSessionAllocationMetrics(session.OwnerLength, session.OwnerCapacity, view.Columns.Length, view.RequiredChunkCount, view.Profiles.Length, view.PacketWordCapacity, view.State.PacketWordCursor, view.State.MaterializedChunkCount, view.State.MaterializedSectionCount, view.State.MaterializedRawSectionCount, view.State.MaterializedPaletteCursor, view.State.MaterializedPackedWordCursor);
        });
        return metrics;
    }
}

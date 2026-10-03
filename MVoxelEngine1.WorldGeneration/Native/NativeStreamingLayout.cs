using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeStreamingLayout
{
    internal NativeStreamingLayout(int offset, int columns, int chunks, int profiles, int requiredChunks)
    {
        int cursor = offset;
        StateBackup = Reserve<NativeGtrtSessionState>(ref cursor, 1);
        ColumnsBackup = Reserve<NativeColumnRecord>(ref cursor, columns);
        SummariesBackup = Reserve<NativeColumnSummary>(ref cursor, columns);
        ChunksBackup = Reserve<NativeChunkRecord>(ref cursor, chunks);
        GenerationJobsBackup = Reserve<NativeWorkItem>(ref cursor, columns);
        MeshJobsBackup = Reserve<NativeWorkItem>(ref cursor, chunks);
        PacketsBackup = Reserve<NativeRenderPacketRecord>(ref cursor, chunks);
        ReadySlotsBackup = Reserve<NativeReadySlot>(ref cursor, requiredChunks);
        ChangedColumns = Reserve<byte>(ref cursor, columns);
        ProfilesBackup = Reserve<BlockColumnProfile>(ref cursor, profiles);
        FreeRangeCapacity = checked(chunks * 2 + 1);
        FreeRanges = Reserve<NativePacketWordRange>(ref cursor, FreeRangeCapacity);
        FreeRangesBackup = Reserve<NativePacketWordRange>(ref cursor, FreeRangeCapacity);
        EndOffset = cursor;
    }

    internal int StateBackup { get; }
    internal int ColumnsBackup { get; }
    internal int SummariesBackup { get; }
    internal int ChunksBackup { get; }
    internal int GenerationJobsBackup { get; }
    internal int MeshJobsBackup { get; }
    internal int PacketsBackup { get; }
    internal int ReadySlotsBackup { get; }
    internal int ChangedColumns { get; }
    internal int ProfilesBackup { get; }
    internal int FreeRanges { get; }
    internal int FreeRangesBackup { get; }
    internal int FreeRangeCapacity { get; }
    internal int EndOffset { get; }

    private static int Reserve<T>(ref int cursor, int count) where T : unmanaged
    {
        int offset = cursor;
        cursor = checked((checked(cursor + count * Unsafe.SizeOf<T>()) + 7) & ~7);
        return offset;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativePacketWordRange
{
    internal int Offset;
    internal int Count;
}

internal readonly record struct NativeStreamingStatistics(
    int GeneratedColumns,
    int RetainedColumns,
    int MeshedChunks,
    int RetainedPackets);

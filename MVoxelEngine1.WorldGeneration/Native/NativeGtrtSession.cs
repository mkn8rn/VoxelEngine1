using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

internal enum NativeColumnState : int
{
    Empty = 0,
    Reserved = 1,
    Generated = 2,
    Retired = 3
}

internal enum NativeChunkState : int
{
    Empty = 0,
    Reserved = 1,
    Generated = 2,
    MeshReady = 3,
    PacketReady = 4,
    Active = 5,
    Retired = 6
}

internal enum NativeWorkKind : int
{
    None = 0,
    GenerateColumn = 1,
    BuildChunkMesh = 2
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeGtrtSessionState
{
    internal long Seed;
    internal int SessionEpoch;
    internal int PublicationState;
    internal int GenerationCursor;
    internal int RemainingColumns;
    internal int MeshCursor;
    internal int RemainingChunks;
    internal int ReadyPacketCount;
    internal int FailureCode;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeColumnRecord
{
    internal int ChunkX;
    internal int ChunkZ;
    internal int ProfileOffset;
    internal int BiomeIndex;
    internal int GenerationEpoch;
    internal NativeColumnState State;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeChunkRecord
{
    internal int ChunkX;
    internal int ChunkY;
    internal int ChunkZ;
    internal int ColumnIndex;
    internal int ProfileOffset;
    internal int GenerationEpoch;
    internal int MeshEpoch;
    internal int PacketIndex;
    internal long DirtyRevision;
    internal NativeChunkState State;
    internal int Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeWorkItem
{
    internal int RecordIndex;
    internal int Epoch;
    internal NativeWorkKind Kind;
    internal int Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeRenderPacketRecord
{
    internal long RenderDataId;
    internal int ChunkIndex;
    internal int RegistryEpoch;
    internal int OpaqueWordOffset;
    internal int OpaqueWordCount;
    internal int OpaqueFaceCount;
    internal int TransparentWordOffset;
    internal int TransparentWordCount;
    internal int TransparentFaceCount;
    internal int PublicationEpoch;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeGtrtSessionLayout
{
    internal NativeGtrtSessionLayout(
        int chunkSizeX,
        int chunkSizeY,
        int chunkSizeZ,
        int lod1Radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeZ);
        ArgumentOutOfRangeException.ThrowIfNegative(lod1Radius);

        ChunkSizeX = chunkSizeX;
        ChunkSizeY = chunkSizeY;
        ChunkSizeZ = chunkSizeZ;
        Lod1Radius = lod1Radius;
        ResidentRadius = checked(lod1Radius + 1);
        MinimumChunkX = -ResidentRadius;
        MaximumChunkX = ResidentRadius;
        MinimumChunkZ = -ResidentRadius;
        MaximumChunkZ = ResidentRadius;
        MinimumChunkY = -lod1Radius;
        MaximumChunkY = lod1Radius;
        ColumnWidth = checked(ResidentRadius * 2 + 1);
        VerticalChunkCount = checked(lod1Radius * 2 + 1);
        ColumnCount = checked(ColumnWidth * ColumnWidth);
        ChunkCount = checked(ColumnCount * VerticalChunkCount);
        ProfilesPerColumn = checked(chunkSizeX * chunkSizeZ);
        ProfileCount = checked(ColumnCount * ProfilesPerColumn);

        int cursor = Align(
            Unsafe.SizeOf<NativeGtrtSessionHeader>(),
            8);
        StateOffset = cursor;
        cursor = AddRange<NativeGtrtSessionState>(cursor, 1, 8);
        ColumnOffset = cursor;
        cursor = AddRange<NativeColumnRecord>(cursor, ColumnCount, 8);
        ProfileOffset = cursor;
        cursor = AddRange<BlockColumnProfile>(cursor, ProfileCount, 8);
        ChunkOffset = cursor;
        cursor = AddRange<NativeChunkRecord>(cursor, ChunkCount, 8);
        GenerationJobOffset = cursor;
        cursor = AddRange<NativeWorkItem>(cursor, ColumnCount, 8);
        MeshJobOffset = cursor;
        cursor = AddRange<NativeWorkItem>(cursor, ChunkCount, 8);
        PacketOffset = cursor;
        cursor = AddRange<NativeRenderPacketRecord>(cursor, ChunkCount, 8);
        TotalByteCount = cursor;
    }

    internal int ChunkSizeX { get; }

    internal int ChunkSizeY { get; }

    internal int ChunkSizeZ { get; }

    internal int Lod1Radius { get; }

    internal int ResidentRadius { get; }

    internal int MinimumChunkX { get; }

    internal int MaximumChunkX { get; }

    internal int MinimumChunkY { get; }

    internal int MaximumChunkY { get; }

    internal int MinimumChunkZ { get; }

    internal int MaximumChunkZ { get; }

    internal int ColumnWidth { get; }

    internal int VerticalChunkCount { get; }

    internal int ColumnCount { get; }

    internal int ChunkCount { get; }

    internal int ProfilesPerColumn { get; }

    internal int ProfileCount { get; }

    internal int StateOffset { get; }

    internal int ColumnOffset { get; }

    internal int ProfileOffset { get; }

    internal int ChunkOffset { get; }

    internal int GenerationJobOffset { get; }

    internal int MeshJobOffset { get; }

    internal int PacketOffset { get; }

    internal int TotalByteCount { get; }

    internal static NativeGtrtSessionLayout Create(GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new NativeGtrtSessionLayout(
            settings.chunkMaxX,
            settings.chunkMaxY,
            settings.chunkMaxZ,
            settings.lod1RenderDistance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetColumnIndex(int chunkX, int chunkZ)
    {
        int localX = chunkX - MinimumChunkX;
        int localZ = chunkZ - MinimumChunkZ;
        if ((uint)localX >= (uint)ColumnWidth ||
            (uint)localZ >= (uint)ColumnWidth)
        {
            return -1;
        }

        return checked(localX * ColumnWidth + localZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetChunkIndex(int chunkX, int chunkY, int chunkZ)
    {
        int columnIndex = GetColumnIndex(chunkX, chunkZ);
        int localY = chunkY - MinimumChunkY;
        if (columnIndex < 0 || (uint)localY >= (uint)VerticalChunkCount)
            return -1;

        return checked(columnIndex * VerticalChunkCount + localY);
    }

    private static int AddRange<T>(int offset, int count, int alignment)
        where T : unmanaged
    {
        int end = checked(offset + checked(count * Unsafe.SizeOf<T>()));
        return Align(end, alignment);
    }

    private static int Align(int value, int alignment)
    {
        int mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeGtrtSessionHeader
{
    internal const uint ExpectedMagic = 0x54525447;
    internal const int ExpectedVersion = 1;

    internal NativeGtrtSessionHeader(NativeGtrtSessionLayout layout)
    {
        Magic = ExpectedMagic;
        Version = ExpectedVersion;
        TotalByteCount = layout.TotalByteCount;
        ChunkSizeX = layout.ChunkSizeX;
        ChunkSizeY = layout.ChunkSizeY;
        ChunkSizeZ = layout.ChunkSizeZ;
        Lod1Radius = layout.Lod1Radius;
        ResidentRadius = layout.ResidentRadius;
        MinimumChunkX = layout.MinimumChunkX;
        MaximumChunkX = layout.MaximumChunkX;
        MinimumChunkY = layout.MinimumChunkY;
        MaximumChunkY = layout.MaximumChunkY;
        MinimumChunkZ = layout.MinimumChunkZ;
        MaximumChunkZ = layout.MaximumChunkZ;
        ColumnWidth = layout.ColumnWidth;
        VerticalChunkCount = layout.VerticalChunkCount;
        ColumnCount = layout.ColumnCount;
        ChunkCount = layout.ChunkCount;
        ProfilesPerColumn = layout.ProfilesPerColumn;
        ProfileCount = layout.ProfileCount;
        StateOffset = layout.StateOffset;
        ColumnOffset = layout.ColumnOffset;
        ProfileOffset = layout.ProfileOffset;
        ChunkOffset = layout.ChunkOffset;
        GenerationJobOffset = layout.GenerationJobOffset;
        MeshJobOffset = layout.MeshJobOffset;
        PacketOffset = layout.PacketOffset;
    }

    internal uint Magic { get; }
    internal int Version { get; }
    internal int TotalByteCount { get; }
    internal int ChunkSizeX { get; }
    internal int ChunkSizeY { get; }
    internal int ChunkSizeZ { get; }
    internal int Lod1Radius { get; }
    internal int ResidentRadius { get; }
    internal int MinimumChunkX { get; }
    internal int MaximumChunkX { get; }
    internal int MinimumChunkY { get; }
    internal int MaximumChunkY { get; }
    internal int MinimumChunkZ { get; }
    internal int MaximumChunkZ { get; }
    internal int ColumnWidth { get; }
    internal int VerticalChunkCount { get; }
    internal int ColumnCount { get; }
    internal int ChunkCount { get; }
    internal int ProfilesPerColumn { get; }
    internal int ProfileCount { get; }
    internal int StateOffset { get; }
    internal int ColumnOffset { get; }
    internal int ProfileOffset { get; }
    internal int ChunkOffset { get; }
    internal int GenerationJobOffset { get; }
    internal int MeshJobOffset { get; }
    internal int PacketOffset { get; }
}

internal ref struct NativeGtrtSessionInitializer
{
    private Span<byte> bytes;

    internal NativeGtrtSessionInitializer(Span<byte> bytes)
    {
        this.bytes = bytes;
    }

    internal void Initialize(
        scoped in NativeGtrtSessionLayout layout)
    {
        bytes.Clear();
        WriteHeader(in layout);
        WriteInt32(
            layout.StateOffset + 20,
            layout.ColumnCount);
        WriteInt32(
            layout.StateOffset + 28,
            layout.ChunkCount);

        int profileByteCount = checked(
            layout.ProfileCount * Unsafe.SizeOf<BlockColumnProfile>());
        Span<byte> profileBytes = bytes.Slice(
            layout.ProfileOffset,
            profileByteCount);
        profileBytes.Fill(byte.MaxValue);

        int columnSize = Unsafe.SizeOf<NativeColumnRecord>();
        int chunkSize = Unsafe.SizeOf<NativeChunkRecord>();
        int workItemSize = Unsafe.SizeOf<NativeWorkItem>();
        for (int chunkX = layout.MinimumChunkX;
             chunkX <= layout.MaximumChunkX;
             chunkX++)
        {
            for (int chunkZ = layout.MinimumChunkZ;
                 chunkZ <= layout.MaximumChunkZ;
                 chunkZ++)
            {
                int columnIndex = layout.GetColumnIndex(chunkX, chunkZ);
                int profileOffset = checked(
                    columnIndex * layout.ProfilesPerColumn);
                int columnOffset = checked(
                    layout.ColumnOffset + columnIndex * columnSize);
                WriteInt32(columnOffset, chunkX);
                WriteInt32(columnOffset + 4, chunkZ);
                WriteInt32(columnOffset + 8, profileOffset);
                WriteInt32(columnOffset + 12, -1);

                int generationJobOffset = checked(
                    layout.GenerationJobOffset +
                    columnIndex * workItemSize);
                WriteInt32(generationJobOffset, columnIndex);
                WriteInt32(generationJobOffset + 4, 1);
                WriteInt32(
                    generationJobOffset + 8,
                    (int)NativeWorkKind.GenerateColumn);

                for (int chunkY = layout.MinimumChunkY;
                     chunkY <= layout.MaximumChunkY;
                     chunkY++)
                {
                    int chunkIndex = layout.GetChunkIndex(
                        chunkX,
                        chunkY,
                        chunkZ);
                    int chunkOffset = checked(
                        layout.ChunkOffset + chunkIndex * chunkSize);
                    WriteInt32(chunkOffset, chunkX);
                    WriteInt32(chunkOffset + 4, chunkY);
                    WriteInt32(chunkOffset + 8, chunkZ);
                    WriteInt32(chunkOffset + 12, columnIndex);
                    WriteInt32(chunkOffset + 16, profileOffset);
                    WriteInt32(chunkOffset + 28, chunkIndex);

                    int meshJobOffset = checked(
                        layout.MeshJobOffset +
                        chunkIndex * workItemSize);
                    WriteInt32(meshJobOffset, chunkIndex);
                    WriteInt32(meshJobOffset + 4, 1);
                    WriteInt32(
                        meshJobOffset + 8,
                        (int)NativeWorkKind.BuildChunkMesh);
                }
            }
        }
    }

    private void WriteHeader(
        scoped in NativeGtrtSessionLayout layout)
    {
        int offset = 0;
        WriteUInt32(offset, NativeGtrtSessionHeader.ExpectedMagic);
        offset += sizeof(uint);
        WriteInt32(offset, NativeGtrtSessionHeader.ExpectedVersion);
        offset += sizeof(int);
        WriteInt32(offset, layout.TotalByteCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.ChunkSizeX);
        offset += sizeof(int);
        WriteInt32(offset, layout.ChunkSizeY);
        offset += sizeof(int);
        WriteInt32(offset, layout.ChunkSizeZ);
        offset += sizeof(int);
        WriteInt32(offset, layout.Lod1Radius);
        offset += sizeof(int);
        WriteInt32(offset, layout.ResidentRadius);
        offset += sizeof(int);
        WriteInt32(offset, layout.MinimumChunkX);
        offset += sizeof(int);
        WriteInt32(offset, layout.MaximumChunkX);
        offset += sizeof(int);
        WriteInt32(offset, layout.MinimumChunkY);
        offset += sizeof(int);
        WriteInt32(offset, layout.MaximumChunkY);
        offset += sizeof(int);
        WriteInt32(offset, layout.MinimumChunkZ);
        offset += sizeof(int);
        WriteInt32(offset, layout.MaximumChunkZ);
        offset += sizeof(int);
        WriteInt32(offset, layout.ColumnWidth);
        offset += sizeof(int);
        WriteInt32(offset, layout.VerticalChunkCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.ColumnCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.ChunkCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.ProfilesPerColumn);
        offset += sizeof(int);
        WriteInt32(offset, layout.ProfileCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.StateOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.ColumnOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.ProfileOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.ChunkOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationJobOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.MeshJobOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.PacketOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt32(int offset, int value) =>
        WriteUInt32(offset, unchecked((uint)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUInt32(int offset, uint value)
    {
        if (BitConverter.IsLittleEndian)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
            return;
        }

        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }
}

internal ref struct NativeGtrtSessionView
{
    private Span<byte> bytes;
    private NativeGtrtSessionHeader header;

    internal NativeGtrtSessionView(Span<byte> bytes)
    {
        if (bytes.Length < Unsafe.SizeOf<NativeGtrtSessionHeader>())
        {
            throw new InvalidDataException(
                "The native GTRT session header is incomplete.");
        }

        header = MemoryMarshal.Read<NativeGtrtSessionHeader>(bytes);
        if (header.Magic != NativeGtrtSessionHeader.ExpectedMagic ||
            header.Version != NativeGtrtSessionHeader.ExpectedVersion ||
            header.TotalByteCount != bytes.Length)
        {
            throw new InvalidDataException(
                "The native GTRT session header is invalid.");
        }

        this.bytes = bytes;
        ValidateRange<NativeGtrtSessionState>(header.StateOffset, 1);
        ValidateRange<NativeColumnRecord>(
            header.ColumnOffset,
            header.ColumnCount);
        ValidateRange<BlockColumnProfile>(
            header.ProfileOffset,
            header.ProfileCount);
        ValidateRange<NativeChunkRecord>(
            header.ChunkOffset,
            header.ChunkCount);
        ValidateRange<NativeWorkItem>(
            header.GenerationJobOffset,
            header.ColumnCount);
        ValidateRange<NativeWorkItem>(
            header.MeshJobOffset,
            header.ChunkCount);
        ValidateRange<NativeRenderPacketRecord>(
            header.PacketOffset,
            header.ChunkCount);
    }

    internal ref NativeGtrtSessionState State =>
        ref ReadRange<NativeGtrtSessionState>(header.StateOffset, 1)[0];

    internal Span<NativeColumnRecord> Columns =>
        ReadRange<NativeColumnRecord>(header.ColumnOffset, header.ColumnCount);

    internal Span<BlockColumnProfile> Profiles =>
        ReadRange<BlockColumnProfile>(header.ProfileOffset, header.ProfileCount);

    internal Span<NativeChunkRecord> Chunks =>
        ReadRange<NativeChunkRecord>(header.ChunkOffset, header.ChunkCount);

    internal Span<NativeWorkItem> GenerationJobs =>
        ReadRange<NativeWorkItem>(
            header.GenerationJobOffset,
            header.ColumnCount);

    internal Span<NativeWorkItem> MeshJobs =>
        ReadRange<NativeWorkItem>(header.MeshJobOffset, header.ChunkCount);

    internal Span<NativeRenderPacketRecord> Packets =>
        ReadRange<NativeRenderPacketRecord>(
            header.PacketOffset,
            header.ChunkCount);

    internal int ColumnCount => header.ColumnCount;

    internal int ChunkCount => header.ChunkCount;

    internal int ProfileCount => header.ProfileCount;

    internal int GetColumnIndex(int chunkX, int chunkZ)
    {
        int localX = chunkX - header.MinimumChunkX;
        int localZ = chunkZ - header.MinimumChunkZ;
        if ((uint)localX >= (uint)header.ColumnWidth ||
            (uint)localZ >= (uint)header.ColumnWidth)
        {
            return -1;
        }

        return checked(localX * header.ColumnWidth + localZ);
    }

    internal int GetChunkIndex(int chunkX, int chunkY, int chunkZ)
    {
        int columnIndex = GetColumnIndex(chunkX, chunkZ);
        int localY = chunkY - header.MinimumChunkY;
        if (columnIndex < 0 ||
            (uint)localY >= (uint)header.VerticalChunkCount)
        {
            return -1;
        }

        return checked(columnIndex * header.VerticalChunkCount + localY);
    }

    private Span<T> ReadRange<T>(int offset, int count)
        where T : unmanaged
    {
        int byteCount = checked(count * Unsafe.SizeOf<T>());
        return MemoryMarshal.Cast<byte, T>(bytes.Slice(offset, byteCount));
    }

    private void ValidateRange<T>(int offset, int count)
        where T : unmanaged
    {
        if (offset < 0 || count < 0)
            throw new InvalidDataException("A native GTRT range is negative.");

        int end = checked(offset + checked(count * Unsafe.SizeOf<T>()));
        if (end > bytes.Length)
        {
            throw new InvalidDataException(
                "A native GTRT range is outside its owner.");
        }
    }
}

internal sealed class NativeGtrtSession : IDisposable
{
    private readonly NativeLeaseAction<byte> publishSeedAction;
    private NativeTransfer<byte>? storage;
    private long pendingSeed;

    private NativeGtrtSession(NativeTransfer<byte>? source)
    {
        try
        {
            storage = NativeTransfer<byte>.Move(ref source);
            publishSeedAction = PublishSeedCore;
        }
        finally
        {
            source?.Dispose();
        }
    }

    internal static NativeGtrtSession Create(GameSettings settings) =>
        Create(NativeGtrtSessionLayout.Create(settings));

    internal static NativeGtrtSession Create(
        NativeGtrtSessionLayout layout)
    {
        using NativeBuilder<byte> builder = new(
            preLease: layout.TotalByteCount);
        builder.Write<NativeGtrtSessionLayout>(
            layout.TotalByteCount,
            in layout,
            static (
                scoped NativeBuilderWriter<byte> writer,
                scoped in NativeGtrtSessionLayout current) =>
            {
                NativeGtrtSessionInitializer initializer = new(
                    writer.AsSpan());
                initializer.Initialize(in current);
                writer.Commit(current.TotalByteCount);
            });

        NativeTransfer<byte>? transfer = null;
        try
        {
            transfer = builder.Complete();
            return new NativeGtrtSession(
                NativeTransfer<byte>.Move(ref transfer));
        }
        finally
        {
            transfer?.Dispose();
        }
    }

    internal void PublishSeed(long seed)
    {
        pendingSeed = seed;
        if (storage is null)
            throw new ObjectDisposedException(nameof(NativeGtrtSession));

        storage.Access(publishSeedAction);
    }

    internal void Access(NativeLeaseAction<byte> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (storage is null)
            throw new ObjectDisposedException(nameof(NativeGtrtSession));

        storage.Access(action);
    }

    public void Dispose()
    {
        if (storage is null)
            return;

        storage.Dispose();
        storage = null;
    }

    private void PublishSeedCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        ref NativeGtrtSessionState state = ref view.State;
        if (Interlocked.CompareExchange(
                ref state.PublicationState,
                -1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "The native GTRT seed is already published.");
        }

        state.Seed = pendingSeed;
        state.SessionEpoch = 1;
        StartupPerformanceRecorder.RecordSeedAccepted();
        Volatile.Write(ref state.PublicationState, 1);
    }
}

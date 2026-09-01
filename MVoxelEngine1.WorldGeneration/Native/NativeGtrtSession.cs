using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
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

internal enum NativeWorkState : int
{
    Waiting = 0,
    Scheduled = 1,
    Claimed = 2,
    Completed = 3,
    Canceled = 4
}

internal enum NativeRenderPacketState : int
{
    Empty = 0,
    Writing = 1,
    Ready = 2,
    Active = 3,
    Retired = 4
}

[Flags]
internal enum NativeChunkFlags : int
{
    None = 0,
    InitialMeshRequired = 1
}

internal enum NativeGtrtFailureCode : int
{
    None = 0,
    InvalidGenerationClaim = 1,
    InvalidGenerationCompletion = 2,
    InvalidMeshDependency = 3,
    MeshReadyQueueFull = 4,
    InvalidMeshClaim = 5,
    InvalidMeshCompletion = 6,
    InvalidGenerationWorkspace = 7,
    InvalidProfileGeneration = 8,
    InvalidTerrainQuery = 9,
    InvalidMeshWorkspace = 10,
    PacketStorageExhausted = 11,
    InvalidPacketPublication = 12,
    InvalidGeneratedMesh = 13,
    InvalidPacketActivation = 14,
    InvalidPacketRetirement = 15,
    InvalidPacketRecycle = 16,
    InvalidMeshCancellation = 17,
    InvalidGenerationCancellation = 18
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
    internal int CancellationState;
    internal long MeshEnqueuePosition;
    internal long MeshDequeuePosition;
    internal int PacketWordCursor;
    internal int ClaimedMeshCount;
    internal int PacketRecycleState;
    internal int ClaimedGenerationCount;
    internal int DisposalState;
    internal int PacketConsumerCount;
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

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeGenerationWorkspaceRecord
{
    internal int State;
    internal int Epoch;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeMeshWorkspaceRecord
{
    internal int State;
    internal int Epoch;
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
    internal int RemainingDependencies;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeWorkItem
{
    internal int RecordIndex;
    internal int Epoch;
    internal NativeWorkKind Kind;
    internal NativeWorkState State;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeReadySlot
{
    internal long Sequence;
    internal int RecordIndex;
    internal int Epoch;
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
    internal NativeRenderPacketState State;
}

internal readonly ref struct NativePacketWriteView
{
    internal NativePacketWriteView(
        Span<uint> opaqueWords,
        Span<uint> transparentWords)
    {
        OpaqueWords = opaqueWords;
        TransparentWords = transparentWords;
    }

    internal Span<uint> OpaqueWords { get; }

    internal Span<uint> TransparentWords { get; }
}

internal readonly ref struct NativePacketReadView
{
    internal NativePacketReadView(
        NativeRenderPacketRecord record,
        ReadOnlySpan<uint> opaqueWords,
        ReadOnlySpan<uint> transparentWords)
    {
        Record = record;
        OpaqueWords = opaqueWords;
        TransparentWords = transparentWords;
    }

    internal NativeRenderPacketRecord Record { get; }

    internal ReadOnlySpan<uint> OpaqueWords { get; }

    internal ReadOnlySpan<uint> TransparentWords { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeGtrtSessionLayout
{
    private const int DefaultPacketWordsPerRequiredChunk = 8_192;

    internal NativeGtrtSessionLayout(
        int chunkSizeX,
        int chunkSizeY,
        int chunkSizeZ,
        int lod1Radius,
        NativeTerrainMaterialSet materials,
        int generationWorkerCount = 1,
        int meshWorkerCount = 1,
        int packetWordCapacity = 0,
        int gameSnapshotByteCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeZ);
        ArgumentOutOfRangeException.ThrowIfNegative(lod1Radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            generationWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            meshWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(packetWordCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(gameSnapshotByteCount);

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
        RequiredColumnWidth = checked(lod1Radius * 2 + 1);
        RequiredColumnCount = checked(
            RequiredColumnWidth * RequiredColumnWidth);
        RequiredChunkCount = checked(
            RequiredColumnCount * VerticalChunkCount);
        ProfilesPerColumn = checked(chunkSizeX * chunkSizeZ);
        ProfileCount = checked(ColumnCount * ProfilesPerColumn);
        GenerationWorkerCount = generationWorkerCount;
        GenerationFloatCountPerWorker = checked(
            ProfilesPerColumn * 2);
        GenerationLatticeCountPerWorker =
            TerrainGenerationUtils.GetSmoothValueNoiseLatticeCapacity(
                chunkSizeX,
                chunkSizeZ);
        MeshWorkerCount = meshWorkerCount;
        MeshFaceScratchCountPerWorker = checked(ProfilesPerColumn * 2);
        PacketWordCapacity = packetWordCapacity == 0
            ? checked(RequiredChunkCount *
                DefaultPacketWordsPerRequiredChunk)
            : packetWordCapacity;
        Materials = materials;
        GameSnapshotByteCount = gameSnapshotByteCount;

        int cursor = Align(
            Unsafe.SizeOf<NativeGtrtSessionHeader>(),
            8);
        StateOffset = cursor;
        cursor = AddRange<NativeGtrtSessionState>(cursor, 1, 8);
        NoiseStateOffset = cursor;
        cursor = AddRange<byte>(
            cursor,
            NativeOpenSimplexNoiseState.StateByteCount,
            8);
        MaterialOffset = cursor;
        cursor = AddRange<NativeTerrainMaterialSet>(cursor, 1, 8);
        ColumnOffset = cursor;
        cursor = AddRange<NativeColumnRecord>(cursor, ColumnCount, 8);
        ProfileOffset = cursor;
        cursor = AddRange<BlockColumnProfile>(cursor, ProfileCount, 8);
        ColumnSummaryOffset = cursor;
        cursor = AddRange<NativeColumnSummary>(cursor, ColumnCount, 8);
        GenerationWorkspaceOffset = cursor;
        cursor = AddRange<NativeGenerationWorkspaceRecord>(
            cursor,
            GenerationWorkerCount,
            8);
        GenerationFloatScratchOffset = cursor;
        cursor = AddRange<float>(
            cursor,
            checked(GenerationWorkerCount *
                GenerationFloatCountPerWorker),
            8);
        GenerationXScratchOffset = cursor;
        cursor = AddRange<TerrainGenerationUtils.NoiseAxisSample>(
            cursor,
            checked(GenerationWorkerCount * ChunkSizeX),
            8);
        GenerationZScratchOffset = cursor;
        cursor = AddRange<TerrainGenerationUtils.NoiseAxisSample>(
            cursor,
            checked(GenerationWorkerCount * ChunkSizeZ),
            8);
        GenerationLatticeScratchOffset = cursor;
        cursor = AddRange<float>(
            cursor,
            checked(GenerationWorkerCount *
                GenerationLatticeCountPerWorker),
            8);
        MeshWorkspaceOffset = cursor;
        cursor = AddRange<NativeMeshWorkspaceRecord>(
            cursor,
            MeshWorkerCount,
            8);
        MeshFaceScratchOffset = cursor;
        cursor = AddRange<int>(
            cursor,
            checked(MeshWorkerCount * MeshFaceScratchCountPerWorker),
            8);
        ChunkOffset = cursor;
        cursor = AddRange<NativeChunkRecord>(cursor, ChunkCount, 8);
        GenerationJobOffset = cursor;
        cursor = AddRange<NativeWorkItem>(cursor, ColumnCount, 8);
        MeshJobOffset = cursor;
        cursor = AddRange<NativeWorkItem>(cursor, ChunkCount, 8);
        PacketOffset = cursor;
        cursor = AddRange<NativeRenderPacketRecord>(cursor, ChunkCount, 8);
        PacketWordOffset = cursor;
        cursor = AddRange<uint>(cursor, PacketWordCapacity, 8);
        MeshReadyOffset = cursor;
        cursor = AddRange<NativeReadySlot>(
            cursor,
            RequiredChunkCount,
            8);
        GameSnapshotOffset = cursor;
        cursor = AddRange<byte>(
            cursor,
            GameSnapshotByteCount,
            8);
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

    internal int RequiredColumnWidth { get; }

    internal int RequiredColumnCount { get; }

    internal int RequiredChunkCount { get; }

    internal int ProfilesPerColumn { get; }

    internal int ProfileCount { get; }

    internal int GenerationWorkerCount { get; }

    internal int GenerationFloatCountPerWorker { get; }

    internal int GenerationLatticeCountPerWorker { get; }

    internal int MeshWorkerCount { get; }

    internal int MeshFaceScratchCountPerWorker { get; }

    internal int PacketWordCapacity { get; }

    internal NativeTerrainMaterialSet Materials { get; }

    internal int GameSnapshotByteCount { get; }

    internal int StateOffset { get; }

    internal int NoiseStateOffset { get; }

    internal int MaterialOffset { get; }

    internal int ColumnOffset { get; }

    internal int ProfileOffset { get; }

    internal int ColumnSummaryOffset { get; }

    internal int GenerationWorkspaceOffset { get; }

    internal int GenerationFloatScratchOffset { get; }

    internal int GenerationXScratchOffset { get; }

    internal int GenerationZScratchOffset { get; }

    internal int GenerationLatticeScratchOffset { get; }

    internal int MeshWorkspaceOffset { get; }

    internal int MeshFaceScratchOffset { get; }

    internal int ChunkOffset { get; }

    internal int GenerationJobOffset { get; }

    internal int MeshJobOffset { get; }

    internal int PacketOffset { get; }

    internal int PacketWordOffset { get; }

    internal int MeshReadyOffset { get; }

    internal int GameSnapshotOffset { get; }

    internal int TotalByteCount { get; }

    internal static NativeGtrtSessionLayout Create(
        GameSettings settings,
        NativeTerrainMaterialSet materials,
        int generationWorkerCount = 1,
        int meshWorkerCount = 1,
        int gameSnapshotByteCount = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new NativeGtrtSessionLayout(
            settings.chunkMaxX,
            settings.chunkMaxY,
            settings.chunkMaxZ,
            settings.lod1RenderDistance,
            materials,
            generationWorkerCount,
            meshWorkerCount,
            gameSnapshotByteCount: gameSnapshotByteCount);
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
    internal const int ExpectedVersion = 9;

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
        RequiredColumnWidth = layout.RequiredColumnWidth;
        RequiredColumnCount = layout.RequiredColumnCount;
        RequiredChunkCount = layout.RequiredChunkCount;
        ProfilesPerColumn = layout.ProfilesPerColumn;
        ProfileCount = layout.ProfileCount;
        GenerationWorkerCount = layout.GenerationWorkerCount;
        GenerationFloatCountPerWorker =
            layout.GenerationFloatCountPerWorker;
        GenerationLatticeCountPerWorker =
            layout.GenerationLatticeCountPerWorker;
        StateOffset = layout.StateOffset;
        NoiseStateOffset = layout.NoiseStateOffset;
        MaterialOffset = layout.MaterialOffset;
        ColumnOffset = layout.ColumnOffset;
        ProfileOffset = layout.ProfileOffset;
        ColumnSummaryOffset = layout.ColumnSummaryOffset;
        GenerationWorkspaceOffset = layout.GenerationWorkspaceOffset;
        GenerationFloatScratchOffset =
            layout.GenerationFloatScratchOffset;
        GenerationXScratchOffset = layout.GenerationXScratchOffset;
        GenerationZScratchOffset = layout.GenerationZScratchOffset;
        GenerationLatticeScratchOffset =
            layout.GenerationLatticeScratchOffset;
        ChunkOffset = layout.ChunkOffset;
        GenerationJobOffset = layout.GenerationJobOffset;
        MeshJobOffset = layout.MeshJobOffset;
        PacketOffset = layout.PacketOffset;
        MeshReadyOffset = layout.MeshReadyOffset;
        MeshWorkerCount = layout.MeshWorkerCount;
        MeshFaceScratchCountPerWorker =
            layout.MeshFaceScratchCountPerWorker;
        PacketWordCapacity = layout.PacketWordCapacity;
        MeshWorkspaceOffset = layout.MeshWorkspaceOffset;
        MeshFaceScratchOffset = layout.MeshFaceScratchOffset;
        PacketWordOffset = layout.PacketWordOffset;
        GameSnapshotOffset = layout.GameSnapshotOffset;
        GameSnapshotByteCount = layout.GameSnapshotByteCount;
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
    internal int RequiredColumnWidth { get; }
    internal int RequiredColumnCount { get; }
    internal int RequiredChunkCount { get; }
    internal int ProfilesPerColumn { get; }
    internal int ProfileCount { get; }
    internal int GenerationWorkerCount { get; }
    internal int GenerationFloatCountPerWorker { get; }
    internal int GenerationLatticeCountPerWorker { get; }
    internal int StateOffset { get; }
    internal int NoiseStateOffset { get; }
    internal int MaterialOffset { get; }
    internal int ColumnOffset { get; }
    internal int ProfileOffset { get; }
    internal int ColumnSummaryOffset { get; }
    internal int GenerationWorkspaceOffset { get; }
    internal int GenerationFloatScratchOffset { get; }
    internal int GenerationXScratchOffset { get; }
    internal int GenerationZScratchOffset { get; }
    internal int GenerationLatticeScratchOffset { get; }
    internal int ChunkOffset { get; }
    internal int GenerationJobOffset { get; }
    internal int MeshJobOffset { get; }
    internal int PacketOffset { get; }
    internal int MeshReadyOffset { get; }
    internal int MeshWorkerCount { get; }
    internal int MeshFaceScratchCountPerWorker { get; }
    internal int PacketWordCapacity { get; }
    internal int MeshWorkspaceOffset { get; }
    internal int MeshFaceScratchOffset { get; }
    internal int PacketWordOffset { get; }
    internal int GameSnapshotOffset { get; }
    internal int GameSnapshotByteCount { get; }
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
        WriteMaterials(layout.MaterialOffset, layout.Materials);
        WriteInt32(
            layout.StateOffset + 20,
            layout.ColumnCount);
        WriteInt32(
            layout.StateOffset + 28,
            layout.RequiredChunkCount);

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
                WriteInt32(
                    generationJobOffset + 12,
                    (int)NativeWorkState.Scheduled);

                bool initialMeshRequired =
                    chunkX >= -layout.Lod1Radius &&
                    chunkX <= layout.Lod1Radius &&
                    chunkZ >= -layout.Lod1Radius &&
                    chunkZ <= layout.Lod1Radius;

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
                    if (initialMeshRequired)
                    {
                        WriteInt32(
                            chunkOffset + 44,
                            (int)NativeChunkFlags.InitialMeshRequired);
                        WriteInt32(chunkOffset + 48, 5);
                    }

                    int meshJobOffset = checked(
                        layout.MeshJobOffset +
                        chunkIndex * workItemSize);
                    WriteInt32(meshJobOffset, chunkIndex);
                    WriteInt32(meshJobOffset + 4, 1);
                    WriteInt32(
                        meshJobOffset + 8,
                        (int)NativeWorkKind.BuildChunkMesh);
                    if (!initialMeshRequired)
                    {
                        WriteInt32(
                            meshJobOffset + 12,
                            (int)NativeWorkState.Canceled);
                    }
                }
            }
        }

        int readySlotSize = Unsafe.SizeOf<NativeReadySlot>();
        for (int index = 0;
             index < layout.RequiredChunkCount;
             index++)
        {
            WriteInt64(
                checked(layout.MeshReadyOffset + index * readySlotSize),
                index);
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
        WriteInt32(offset, layout.RequiredColumnWidth);
        offset += sizeof(int);
        WriteInt32(offset, layout.RequiredColumnCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.RequiredChunkCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.ProfilesPerColumn);
        offset += sizeof(int);
        WriteInt32(offset, layout.ProfileCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationWorkerCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationFloatCountPerWorker);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationLatticeCountPerWorker);
        offset += sizeof(int);
        WriteInt32(offset, layout.StateOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.NoiseStateOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.MaterialOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.ColumnOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.ProfileOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.ColumnSummaryOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationWorkspaceOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationFloatScratchOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationXScratchOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationZScratchOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationLatticeScratchOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.ChunkOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GenerationJobOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.MeshJobOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.PacketOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.MeshReadyOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.MeshWorkerCount);
        offset += sizeof(int);
        WriteInt32(offset, layout.MeshFaceScratchCountPerWorker);
        offset += sizeof(int);
        WriteInt32(offset, layout.PacketWordCapacity);
        offset += sizeof(int);
        WriteInt32(offset, layout.MeshWorkspaceOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.MeshFaceScratchOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.PacketWordOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GameSnapshotOffset);
        offset += sizeof(int);
        WriteInt32(offset, layout.GameSnapshotByteCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt64(int offset, long value)
    {
        ulong bits = unchecked((ulong)value);
        if (BitConverter.IsLittleEndian)
        {
            WriteUInt32(offset, (uint)bits);
            WriteUInt32(offset + sizeof(uint), (uint)(bits >> 32));
            return;
        }

        WriteUInt32(offset, (uint)(bits >> 32));
        WriteUInt32(offset + sizeof(uint), (uint)bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt32(int offset, int value) =>
        WriteUInt32(offset, unchecked((uint)value));

    private void WriteMaterials(
        int offset,
        NativeTerrainMaterialSet materials)
    {
        WriteBlockDescriptor(offset, materials.Stone);
        offset += Unsafe.SizeOf<NativeBlockDescriptor>();
        WriteBlockDescriptor(offset, materials.Soil);
        offset += Unsafe.SizeOf<NativeBlockDescriptor>();
        WriteBlockDescriptor(offset, materials.Water);
    }

    private void WriteBlockDescriptor(
        int offset,
        NativeBlockDescriptor descriptor)
    {
        WriteUInt16(offset, descriptor.Id);
        WriteUInt16(offset + 2, descriptor.BaseType);
        bytes[offset + 4] = descriptor.StateOfMatter;
        bytes[offset + 5] = (byte)descriptor.Flags;
        WriteUInt16(offset + 6, descriptor.LeftTile);
        WriteUInt16(offset + 8, descriptor.RightTile);
        WriteUInt16(offset + 10, descriptor.BottomTile);
        WriteUInt16(offset + 12, descriptor.TopTile);
        WriteUInt16(offset + 14, descriptor.BackTile);
        WriteUInt16(offset + 16, descriptor.FrontTile);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUInt16(int offset, ushort value)
    {
        if (BitConverter.IsLittleEndian)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            return;
        }

        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

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
        ValidateRange<byte>(
            header.NoiseStateOffset,
            NativeOpenSimplexNoiseState.StateByteCount);
        ValidateRange<NativeTerrainMaterialSet>(header.MaterialOffset, 1);
        ValidateRange<NativeColumnRecord>(
            header.ColumnOffset,
            header.ColumnCount);
        ValidateRange<BlockColumnProfile>(
            header.ProfileOffset,
            header.ProfileCount);
        ValidateRange<NativeColumnSummary>(
            header.ColumnSummaryOffset,
            header.ColumnCount);
        ValidateRange<NativeGenerationWorkspaceRecord>(
            header.GenerationWorkspaceOffset,
            header.GenerationWorkerCount);
        ValidateRange<float>(
            header.GenerationFloatScratchOffset,
            checked(header.GenerationWorkerCount *
                header.GenerationFloatCountPerWorker));
        ValidateRange<TerrainGenerationUtils.NoiseAxisSample>(
            header.GenerationXScratchOffset,
            checked(header.GenerationWorkerCount * header.ChunkSizeX));
        ValidateRange<TerrainGenerationUtils.NoiseAxisSample>(
            header.GenerationZScratchOffset,
            checked(header.GenerationWorkerCount * header.ChunkSizeZ));
        ValidateRange<float>(
            header.GenerationLatticeScratchOffset,
            checked(header.GenerationWorkerCount *
                header.GenerationLatticeCountPerWorker));
        ValidateRange<NativeMeshWorkspaceRecord>(
            header.MeshWorkspaceOffset,
            header.MeshWorkerCount);
        ValidateRange<int>(
            header.MeshFaceScratchOffset,
            checked(header.MeshWorkerCount *
                header.MeshFaceScratchCountPerWorker));
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
        ValidateRange<uint>(
            header.PacketWordOffset,
            header.PacketWordCapacity);
        ValidateRange<NativeReadySlot>(
            header.MeshReadyOffset,
            header.RequiredChunkCount);
        ValidateRange<byte>(
            header.GameSnapshotOffset,
            header.GameSnapshotByteCount);
    }

    internal ref NativeGtrtSessionState State =>
        ref ReadRange<NativeGtrtSessionState>(header.StateOffset, 1)[0];

    internal NativeOpenSimplexNoiseState NoiseState =>
        new(ReadRange<byte>(
            header.NoiseStateOffset,
            NativeOpenSimplexNoiseState.StateByteCount));

    internal ref readonly NativeTerrainMaterialSet Materials =>
        ref ReadRange<NativeTerrainMaterialSet>(header.MaterialOffset, 1)[0];

    internal Span<NativeColumnRecord> Columns =>
        ReadRange<NativeColumnRecord>(header.ColumnOffset, header.ColumnCount);

    internal Span<BlockColumnProfile> Profiles =>
        ReadRange<BlockColumnProfile>(header.ProfileOffset, header.ProfileCount);

    internal Span<NativeColumnSummary> ColumnSummaries =>
        ReadRange<NativeColumnSummary>(
            header.ColumnSummaryOffset,
            header.ColumnCount);

    internal Span<NativeGenerationWorkspaceRecord> GenerationWorkspaces =>
        ReadRange<NativeGenerationWorkspaceRecord>(
            header.GenerationWorkspaceOffset,
            header.GenerationWorkerCount);

    internal Span<NativeChunkRecord> Chunks =>
        ReadRange<NativeChunkRecord>(header.ChunkOffset, header.ChunkCount);

    internal Span<NativeWorkItem> GenerationJobs =>
        ReadRange<NativeWorkItem>(
            header.GenerationJobOffset,
            header.ColumnCount);

    internal Span<NativeWorkItem> MeshJobs =>
        ReadRange<NativeWorkItem>(header.MeshJobOffset, header.ChunkCount);

    internal Span<NativeMeshWorkspaceRecord> MeshWorkspaces =>
        ReadRange<NativeMeshWorkspaceRecord>(
            header.MeshWorkspaceOffset,
            header.MeshWorkerCount);

    internal Span<NativeRenderPacketRecord> Packets =>
        ReadRange<NativeRenderPacketRecord>(
            header.PacketOffset,
            header.ChunkCount);

    internal Span<uint> PacketWords =>
        ReadRange<uint>(
            header.PacketWordOffset,
            header.PacketWordCapacity);

    internal Span<NativeReadySlot> MeshReadySlots =>
        ReadRange<NativeReadySlot>(
            header.MeshReadyOffset,
            header.RequiredChunkCount);

    internal Span<byte> GameSnapshotBytes =>
        ReadRange<byte>(
            header.GameSnapshotOffset,
            header.GameSnapshotByteCount);

    internal NativeGameSnapshotView GameSnapshot =>
        new(GameSnapshotBytes);

    internal int ColumnCount => header.ColumnCount;

    internal int ChunkCount => header.ChunkCount;

    internal int ProfileCount => header.ProfileCount;

    internal int ProfilesPerColumn => header.ProfilesPerColumn;

    internal int ChunkSizeX => header.ChunkSizeX;

    internal int ChunkSizeY => header.ChunkSizeY;

    internal int ChunkSizeZ => header.ChunkSizeZ;

    internal int GenerationWorkerCount => header.GenerationWorkerCount;

    internal int MeshWorkerCount => header.MeshWorkerCount;

    internal int PacketWordCapacity => header.PacketWordCapacity;

    internal int RequiredChunkCount => header.RequiredChunkCount;

    internal Span<BlockColumnProfile> GetColumnProfiles(int columnIndex) =>
        Profiles.Slice(
            checked(columnIndex * header.ProfilesPerColumn),
            header.ProfilesPerColumn);

    internal Span<float> GetGenerationFloatScratch(int workerIndex) =>
        ReadRange<float>(
            checked(header.GenerationFloatScratchOffset +
                workerIndex * header.GenerationFloatCountPerWorker *
                Unsafe.SizeOf<float>()),
            header.GenerationFloatCountPerWorker);

    internal Span<TerrainGenerationUtils.NoiseAxisSample>
        GetGenerationXScratch(int workerIndex) =>
        ReadRange<TerrainGenerationUtils.NoiseAxisSample>(
            checked(header.GenerationXScratchOffset +
                workerIndex * header.ChunkSizeX *
                Unsafe.SizeOf<TerrainGenerationUtils.NoiseAxisSample>()),
            header.ChunkSizeX);

    internal Span<TerrainGenerationUtils.NoiseAxisSample>
        GetGenerationZScratch(int workerIndex) =>
        ReadRange<TerrainGenerationUtils.NoiseAxisSample>(
            checked(header.GenerationZScratchOffset +
                workerIndex * header.ChunkSizeZ *
                Unsafe.SizeOf<TerrainGenerationUtils.NoiseAxisSample>()),
            header.ChunkSizeZ);

    internal Span<float> GetGenerationLatticeScratch(int workerIndex) =>
        ReadRange<float>(
            checked(header.GenerationLatticeScratchOffset +
                workerIndex * header.GenerationLatticeCountPerWorker *
                Unsafe.SizeOf<float>()),
            header.GenerationLatticeCountPerWorker);

    internal Span<int> GetMeshBottomFaceScratch(int workerIndex) =>
        ReadRange<int>(
            GetMeshScratchOffset(workerIndex),
            header.ChunkSizeX * header.ChunkSizeZ);

    internal Span<int> GetMeshTopFaceScratch(int workerIndex) =>
        ReadRange<int>(
            checked(GetMeshScratchOffset(workerIndex) +
                header.ChunkSizeX * header.ChunkSizeZ *
                Unsafe.SizeOf<int>()),
            header.ChunkSizeX * header.ChunkSizeZ);

    internal bool TryAcquireGenerationWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.GenerationWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
            return false;
        }

        Span<NativeGenerationWorkspaceRecord> workspaces =
            GenerationWorkspaces;
        ref NativeGenerationWorkspaceRecord workspace =
            ref workspaces[workerIndex];
        if (Interlocked.CompareExchange(
                ref workspace.State,
                1,
                0) != 0)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
            return false;
        }

        workspace.Epoch = State.SessionEpoch;
        return true;
    }

    internal void ReleaseGenerationWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.GenerationWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
            return;
        }

        Span<NativeGenerationWorkspaceRecord> workspaces =
            GenerationWorkspaces;
        ref NativeGenerationWorkspaceRecord workspace =
            ref workspaces[workerIndex];
        workspace.Epoch = 0;
        if (Interlocked.CompareExchange(
                ref workspace.State,
                0,
                1) != 1)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
        }
    }

    internal bool TryAcquireMeshWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.MeshWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return false;
        }

        Span<NativeMeshWorkspaceRecord> workspaces = MeshWorkspaces;
        ref NativeMeshWorkspaceRecord workspace = ref workspaces[workerIndex];
        if (Interlocked.CompareExchange(ref workspace.State, 1, 0) != 0)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return false;
        }

        workspace.Epoch = State.SessionEpoch;
        return true;
    }

    internal void ReleaseMeshWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.MeshWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return;
        }

        Span<NativeMeshWorkspaceRecord> workspaces = MeshWorkspaces;
        ref NativeMeshWorkspaceRecord workspace = ref workspaces[workerIndex];
        workspace.Epoch = 0;
        if (Interlocked.CompareExchange(ref workspace.State, 0, 1) != 1)
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
    }

    internal bool TryBeginPacket(
        scoped in NativeWorkItem claimedWork,
        int opaqueWordCount,
        int opaqueFaceCount,
        int transparentWordCount,
        int transparentFaceCount,
        out NativePacketWriteView packet)
    {
        packet = default;
        if (claimedWork.Kind != NativeWorkKind.BuildChunkMesh ||
            claimedWork.Epoch != State.SessionEpoch ||
            (uint)claimedWork.RecordIndex >= (uint)Chunks.Length ||
            opaqueWordCount < 0 ||
            transparentWordCount < 0 ||
            opaqueWordCount > int.MaxValue - transparentWordCount ||
            (opaqueWordCount & 1) != 0 ||
            (transparentWordCount & 1) != 0 ||
            opaqueFaceCount < opaqueWordCount / 2 ||
            transparentFaceCount < transparentWordCount / 2)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        if (Volatile.Read(ref State.DisposalState) != 0 ||
            Volatile.Read(ref State.PacketRecycleState) != 0)
            return false;

        int chunkIndex = claimedWork.RecordIndex;
        ref NativeWorkItem job = ref MeshJobs[chunkIndex];
        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        if (ReadState(ref job.State) != NativeWorkState.Claimed ||
            ReadState(ref chunk.State) != NativeChunkState.MeshReady)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        ref NativeRenderPacketRecord record = ref Packets[chunk.PacketIndex];
        if (!TryTransition(
                ref record.State,
                NativeRenderPacketState.Empty,
                NativeRenderPacketState.Writing))
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        record.ChunkIndex = chunkIndex;
        record.RegistryEpoch = claimedWork.Epoch;
        record.PublicationEpoch = 0;
        int totalWordCount = checked(
            opaqueWordCount + transparentWordCount);
        if (!TryReservePacketWords(totalWordCount, out int wordOffset))
        {
            TryTransition(
                ref record.State,
                NativeRenderPacketState.Writing,
                NativeRenderPacketState.Retired);
            Fail(NativeGtrtFailureCode.PacketStorageExhausted);
            return false;
        }

        record.RenderDataId = ((long)claimedWork.Epoch << 32) |
            (uint)chunkIndex;
        record.OpaqueWordOffset = wordOffset;
        record.OpaqueWordCount = opaqueWordCount;
        record.OpaqueFaceCount = opaqueFaceCount;
        record.TransparentWordOffset = checked(
            wordOffset + opaqueWordCount);
        record.TransparentWordCount = transparentWordCount;
        record.TransparentFaceCount = transparentFaceCount;
        Span<uint> words = PacketWords;
        packet = new NativePacketWriteView(
            words.Slice(record.OpaqueWordOffset, opaqueWordCount),
            words.Slice(
                record.TransparentWordOffset,
                transparentWordCount));
        return true;
    }

    internal bool TryActivatePacket(
        int chunkIndex,
        out NativePacketReadView packet)
    {
        packet = default;
        if (!TryEnterPacketConsumer())
            return false;

        try
        {
            return TryActivatePacketCore(chunkIndex, out packet);
        }
        finally
        {
            ExitPacketConsumer();
        }
    }

    private bool TryActivatePacketCore(
        int chunkIndex,
        out NativePacketReadView packet)
    {
        packet = default;
        if ((uint)chunkIndex >= (uint)Chunks.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketActivation);
            return false;
        }

        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        ref NativeRenderPacketRecord source =
            ref Packets[chunk.PacketIndex];
        if (ReadState(ref chunk.State) != NativeChunkState.PacketReady ||
            source.PublicationEpoch != State.SessionEpoch ||
            source.ChunkIndex != chunkIndex ||
            !TryTransition(
                ref source.State,
                NativeRenderPacketState.Ready,
                NativeRenderPacketState.Active))
        {
            return false;
        }

        if (!TryTransition(
                ref chunk.State,
                NativeChunkState.PacketReady,
                NativeChunkState.Active))
        {
            TryTransition(
                ref source.State,
                NativeRenderPacketState.Active,
                NativeRenderPacketState.Retired);
            Fail(NativeGtrtFailureCode.InvalidPacketActivation);
            return false;
        }

        int ready = Interlocked.Decrement(ref State.ReadyPacketCount);
        if (ready < 0)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketActivation);
            return false;
        }

        NativeRenderPacketRecord record = source;
        Span<uint> words = PacketWords;
        packet = new NativePacketReadView(
            record,
            words.Slice(record.OpaqueWordOffset, record.OpaqueWordCount),
            words.Slice(
                record.TransparentWordOffset,
                record.TransparentWordCount));
        return true;
    }

    internal bool TryRetirePacket(int chunkIndex)
    {
        if (!TryEnterPacketConsumer())
            return false;

        try
        {
            return TryRetirePacketCore(chunkIndex);
        }
        finally
        {
            ExitPacketConsumer();
        }
    }

    private bool TryRetirePacketCore(int chunkIndex)
    {
        if ((uint)chunkIndex >= (uint)Chunks.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketRetirement);
            return false;
        }

        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        ref NativeRenderPacketRecord packet =
            ref Packets[chunk.PacketIndex];
        NativeRenderPacketState packetState = ReadState(ref packet.State);
        NativeChunkState expectedChunkState;
        switch (packetState)
        {
            case NativeRenderPacketState.Ready:
                expectedChunkState = NativeChunkState.PacketReady;
                break;
            case NativeRenderPacketState.Active:
                expectedChunkState = NativeChunkState.Active;
                break;
            default:
                return false;
        }

        if (packet.PublicationEpoch != State.SessionEpoch ||
            packet.ChunkIndex != chunkIndex ||
            ReadState(ref chunk.State) != expectedChunkState ||
            !TryTransition(
                ref packet.State,
                packetState,
                NativeRenderPacketState.Retired))
        {
            return false;
        }

        if (!TryTransition(
                ref chunk.State,
                expectedChunkState,
                NativeChunkState.Retired))
        {
            Fail(NativeGtrtFailureCode.InvalidPacketRetirement);
            return false;
        }

        if (packetState == NativeRenderPacketState.Ready)
        {
            int ready = Interlocked.Decrement(ref State.ReadyPacketCount);
            if (ready < 0)
            {
                Fail(NativeGtrtFailureCode.InvalidPacketRetirement);
                return false;
            }
        }

        return true;
    }

    internal bool TryAbandonMesh(
        scoped in NativeWorkItem claimedWork)
    {
        if (claimedWork.Kind != NativeWorkKind.BuildChunkMesh ||
            claimedWork.Epoch != State.SessionEpoch ||
            (uint)claimedWork.RecordIndex >= (uint)MeshJobs.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
            return false;
        }

        int chunkIndex = claimedWork.RecordIndex;
        ref NativeWorkItem job = ref MeshJobs[chunkIndex];
        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        ref NativeRenderPacketRecord packet =
            ref Packets[chunk.PacketIndex];
        if (ReadState(ref job.State) != NativeWorkState.Claimed ||
            ReadState(ref chunk.State) != NativeChunkState.MeshReady)
        {
            return false;
        }

        NativeRenderPacketState packetState = ReadState(ref packet.State);
        if (packetState == NativeRenderPacketState.Writing)
        {
            if (!TryTransition(
                    ref packet.State,
                    NativeRenderPacketState.Writing,
                    NativeRenderPacketState.Retired))
            {
                Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
                return false;
            }
        }
        else if (packetState != NativeRenderPacketState.Empty &&
                 packetState != NativeRenderPacketState.Retired)
        {
            return false;
        }

        if (!TryTransition(
                ref chunk.State,
                NativeChunkState.MeshReady,
                NativeChunkState.Retired) ||
            !TryTransition(
                ref job.State,
                NativeWorkState.Claimed,
                NativeWorkState.Canceled))
        {
            Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
            return false;
        }

        int claimed = Interlocked.Decrement(ref State.ClaimedMeshCount);
        if (claimed < 0)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
            return false;
        }

        return true;
    }

    internal bool TryRecyclePacketStorage()
    {
        ref NativeGtrtSessionState state = ref State;
        if (Interlocked.CompareExchange(
                ref state.PacketRecycleState,
                1,
                0) != 0)
        {
            return false;
        }

        try
        {
            if (Volatile.Read(ref state.ClaimedMeshCount) != 0 ||
                Volatile.Read(ref state.PacketConsumerCount) != 0 ||
                Volatile.Read(ref state.ReadyPacketCount) != 0)
            {
                return false;
            }

            Span<NativeMeshWorkspaceRecord> workspaces = MeshWorkspaces;
            for (int index = 0; index < workspaces.Length; index++)
            {
                if (Volatile.Read(ref workspaces[index].State) != 0)
                    return false;
            }

            Span<NativeRenderPacketRecord> packets = Packets;
            Span<NativeChunkRecord> chunks = Chunks;
            for (int index = 0; index < packets.Length; index++)
            {
                ref NativeRenderPacketRecord packet = ref packets[index];
                NativeRenderPacketState packetState =
                    ReadState(ref packet.State);
                if (packetState == NativeRenderPacketState.Empty)
                    continue;
                if (packetState != NativeRenderPacketState.Retired)
                    return false;
                if ((uint)packet.ChunkIndex >= (uint)chunks.Length ||
                    chunks[packet.ChunkIndex].PacketIndex != index ||
                    ReadState(ref chunks[packet.ChunkIndex].State) !=
                        NativeChunkState.Retired)
                {
                    Fail(NativeGtrtFailureCode.InvalidPacketRecycle);
                    return false;
                }
            }

            for (int index = 0; index < packets.Length; index++)
            {
                if (ReadState(ref packets[index].State) ==
                    NativeRenderPacketState.Retired)
                {
                    packets[index] = default;
                }
            }

            Volatile.Write(ref state.PacketWordCursor, 0);
            return true;
        }
        finally
        {
            Volatile.Write(ref state.PacketRecycleState, 0);
        }
    }

    internal bool TryPrepareForDisposal()
    {
        ref NativeGtrtSessionState state = ref State;
        int disposalState = Volatile.Read(ref state.DisposalState);
        if (disposalState == 0)
        {
            disposalState = Interlocked.CompareExchange(
                ref state.DisposalState,
                1,
                0);
        }
        if (disposalState != 0 && disposalState != 1)
            return false;

        RequestCancellation();
        if (Volatile.Read(ref state.ClaimedGenerationCount) != 0 ||
            Volatile.Read(ref state.ClaimedMeshCount) != 0)
            return false;

        Span<NativeGenerationWorkspaceRecord> generationWorkspaces =
            GenerationWorkspaces;
        for (int index = 0; index < generationWorkspaces.Length; index++)
        {
            if (Volatile.Read(ref generationWorkspaces[index].State) != 0)
                return false;
        }

        Span<NativeMeshWorkspaceRecord> meshWorkspaces = MeshWorkspaces;
        for (int index = 0; index < meshWorkspaces.Length; index++)
        {
            if (Volatile.Read(ref meshWorkspaces[index].State) != 0)
                return false;
        }

        Span<NativeRenderPacketRecord> packets = Packets;
        for (int index = 0; index < packets.Length; index++)
        {
            NativeRenderPacketState packetState =
                ReadState(ref packets[index].State);
            if (packetState == NativeRenderPacketState.Empty ||
                packetState == NativeRenderPacketState.Retired)
            {
                continue;
            }
            if (packetState == NativeRenderPacketState.Writing ||
                packetState == NativeRenderPacketState.Active)
                return false;
            if (packetState != NativeRenderPacketState.Ready)
            {
                Fail(NativeGtrtFailureCode.InvalidPacketRecycle);
                return false;
            }
            if (!TryRetirePacket(packets[index].ChunkIndex))
                return false;
        }

        return TryRecyclePacketStorage();
    }

    internal bool TryReadPacket(
        int chunkIndex,
        out NativePacketReadView packet)
    {
        packet = default;
        if ((uint)chunkIndex >= (uint)Chunks.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        NativeChunkRecord chunk = Chunks[chunkIndex];
        ref NativeRenderPacketRecord source = ref Packets[chunk.PacketIndex];
        if (ReadState(ref chunk.State) != NativeChunkState.PacketReady ||
            ReadState(ref source.State) != NativeRenderPacketState.Ready ||
            source.PublicationEpoch != State.SessionEpoch ||
            source.ChunkIndex != chunkIndex)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        NativeRenderPacketRecord record = source;
        Span<uint> words = PacketWords;
        packet = new NativePacketReadView(
            record,
            words.Slice(record.OpaqueWordOffset, record.OpaqueWordCount),
            words.Slice(
                record.TransparentWordOffset,
                record.TransparentWordCount));
        return true;
    }

    internal void Fail(NativeGtrtFailureCode failure) =>
        RecordFailure(ref State, failure);

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

    internal bool TryClaimGeneration(out NativeWorkItem work)
    {
        Span<NativeWorkItem> jobs = GenerationJobs;
        Span<NativeColumnRecord> columns = Columns;
        ref NativeGtrtSessionState state = ref State;
        if (Volatile.Read(ref state.CancellationState) != 0 ||
            Volatile.Read(ref state.DisposalState) != 0)
        {
            work = default;
            return false;
        }

        Interlocked.Increment(ref state.ClaimedGenerationCount);
        if (Volatile.Read(ref state.CancellationState) != 0 ||
            Volatile.Read(ref state.DisposalState) != 0)
        {
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            work = default;
            return false;
        }

        while (true)
        {
            if (Volatile.Read(ref state.CancellationState) != 0)
            {
                Interlocked.Decrement(ref state.ClaimedGenerationCount);
                work = default;
                return false;
            }

            int index = Interlocked.Increment(
                ref state.GenerationCursor) - 1;
            if ((uint)index >= (uint)jobs.Length)
            {
                Interlocked.Decrement(ref state.ClaimedGenerationCount);
                work = default;
                return false;
            }

            ref NativeWorkItem job = ref jobs[index];
            if (!TryTransition(
                    ref job.State,
                    NativeWorkState.Scheduled,
                    NativeWorkState.Claimed))
            {
                continue;
            }

            ref NativeColumnRecord column = ref columns[job.RecordIndex];
            if (!TryTransition(
                    ref column.State,
                    NativeColumnState.Empty,
                    NativeColumnState.Reserved))
            {
                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.InvalidGenerationClaim);
                Interlocked.Decrement(ref state.ClaimedGenerationCount);
                work = default;
                return false;
            }

            work = job;
            return true;
        }
    }

    internal bool TryCompleteGeneration(
        scoped in NativeWorkItem claimedWork)
    {
        ref NativeGtrtSessionState state = ref State;
        if (claimedWork.Kind != NativeWorkKind.GenerateColumn ||
            claimedWork.Epoch != state.SessionEpoch ||
            (uint)claimedWork.RecordIndex >= (uint)Columns.Length)
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidGenerationCompletion);
            return false;
        }

        Span<NativeWorkItem> jobs = GenerationJobs;
        ref NativeWorkItem job = ref jobs[claimedWork.RecordIndex];
        if (!TryTransition(
                ref job.State,
                NativeWorkState.Claimed,
                NativeWorkState.Completed))
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidGenerationCompletion);
            return false;
        }

        Span<NativeColumnRecord> columns = Columns;
        ref NativeColumnRecord column =
            ref columns[claimedWork.RecordIndex];
        if (!TryTransition(
                ref column.State,
                NativeColumnState.Reserved,
                NativeColumnState.Generated))
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidGenerationCompletion);
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            return false;
        }

        if (!PublishGeneratedChunks(
                column.ChunkX,
                column.ChunkZ,
                ref state))
        {
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            return false;
        }

        int remaining = Interlocked.Decrement(
            ref state.RemainingColumns);
        if (remaining < 0)
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidGenerationCompletion);
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            return false;
        }

        bool released = ReleaseMeshDependencies(
            column.ChunkX,
            column.ChunkZ,
            claimedWork.Epoch,
            ref state);
        int claimed = Interlocked.Decrement(
            ref state.ClaimedGenerationCount);
        if (claimed < 0)
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidGenerationCompletion);
            return false;
        }

        return released;
    }

    internal bool TryAbandonGeneration(
        scoped in NativeWorkItem claimedWork)
    {
        if (claimedWork.Kind != NativeWorkKind.GenerateColumn ||
            claimedWork.Epoch != State.SessionEpoch ||
            (uint)claimedWork.RecordIndex >= (uint)GenerationJobs.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationCancellation);
            return false;
        }

        ref NativeWorkItem job =
            ref GenerationJobs[claimedWork.RecordIndex];
        ref NativeColumnRecord column =
            ref Columns[claimedWork.RecordIndex];
        if (ReadState(ref job.State) != NativeWorkState.Claimed ||
            ReadState(ref column.State) != NativeColumnState.Reserved)
        {
            return false;
        }

        if (!TryTransition(
                ref column.State,
                NativeColumnState.Reserved,
                NativeColumnState.Retired) ||
            !TryTransition(
                ref job.State,
                NativeWorkState.Claimed,
                NativeWorkState.Canceled))
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationCancellation);
            return false;
        }

        int claimed = Interlocked.Decrement(
            ref State.ClaimedGenerationCount);
        if (claimed < 0)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationCancellation);
            return false;
        }

        return true;
    }

    internal bool TryClaimMesh(out NativeWorkItem work)
    {
        ref NativeGtrtSessionState state = ref State;
        if (Volatile.Read(ref state.CancellationState) != 0 ||
            Volatile.Read(ref state.DisposalState) != 0 ||
            Volatile.Read(ref state.PacketRecycleState) != 0)
        {
            work = default;
            return false;
        }

        Interlocked.Increment(ref state.ClaimedMeshCount);
        if (Volatile.Read(ref state.CancellationState) != 0 ||
            Volatile.Read(ref state.DisposalState) != 0 ||
            Volatile.Read(ref state.PacketRecycleState) != 0)
        {
            Interlocked.Decrement(ref state.ClaimedMeshCount);
            work = default;
            return false;
        }

        while (TryDequeueMeshReady(
            out int chunkIndex,
            out int epoch))
        {
            if (epoch != state.SessionEpoch ||
                (uint)chunkIndex >= (uint)MeshJobs.Length)
            {
                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.InvalidMeshClaim);
                Interlocked.Decrement(ref state.ClaimedMeshCount);
                work = default;
                return false;
            }

            Span<NativeWorkItem> jobs = MeshJobs;
            ref NativeWorkItem job = ref jobs[chunkIndex];
            if (!TryTransition(
                    ref job.State,
                    NativeWorkState.Scheduled,
                    NativeWorkState.Claimed))
            {
                if (ReadState(ref job.State) ==
                    NativeWorkState.Canceled)
                {
                    continue;
                }

                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.InvalidMeshClaim);
                Interlocked.Decrement(ref state.ClaimedMeshCount);
                work = default;
                return false;
            }

            Span<NativeChunkRecord> chunks = Chunks;
            ref NativeChunkRecord chunk = ref chunks[chunkIndex];
            if (!TryTransition(
                    ref chunk.State,
                    NativeChunkState.Generated,
                    NativeChunkState.MeshReady))
            {
                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.InvalidMeshClaim);
                Interlocked.Decrement(ref state.ClaimedMeshCount);
                work = default;
                return false;
            }

            work = job;
            return true;
        }

        Interlocked.Decrement(ref state.ClaimedMeshCount);
        work = default;
        return false;
    }

    internal bool TryCompleteMesh(
        scoped in NativeWorkItem claimedWork)
    {
        ref NativeGtrtSessionState state = ref State;
        if (claimedWork.Kind != NativeWorkKind.BuildChunkMesh ||
            claimedWork.Epoch != state.SessionEpoch ||
            (uint)claimedWork.RecordIndex >= (uint)MeshJobs.Length)
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        Span<NativeWorkItem> jobs = MeshJobs;
        ref NativeWorkItem job = ref jobs[claimedWork.RecordIndex];
        Span<NativeChunkRecord> chunks = Chunks;
        ref NativeChunkRecord chunk =
            ref chunks[claimedWork.RecordIndex];
        ref NativeRenderPacketRecord packet =
            ref Packets[chunk.PacketIndex];
        if (ReadState(ref packet.State) !=
                NativeRenderPacketState.Writing ||
            packet.ChunkIndex != claimedWork.RecordIndex ||
            packet.RegistryEpoch != claimedWork.Epoch ||
            packet.PublicationEpoch != 0)
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        packet.PublicationEpoch = claimedWork.Epoch;
        chunk.MeshEpoch = claimedWork.Epoch;
        if (!TryTransition(
                ref chunk.State,
                NativeChunkState.MeshReady,
                NativeChunkState.PacketReady))
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        Interlocked.Increment(ref state.ReadyPacketCount);
        if (!TryTransition(
                ref job.State,
                NativeWorkState.Claimed,
                NativeWorkState.Completed))
        {
            Interlocked.Decrement(ref state.ReadyPacketCount);
            TryTransition(
                ref chunk.State,
                NativeChunkState.PacketReady,
                NativeChunkState.Retired);
            TryTransition(
                ref packet.State,
                NativeRenderPacketState.Writing,
                NativeRenderPacketState.Retired);
            Interlocked.Decrement(ref state.ClaimedMeshCount);
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        ref int packetState = ref Unsafe.As<NativeRenderPacketState, int>(
            ref packet.State);
        Volatile.Write(ref packetState, (int)NativeRenderPacketState.Ready);
        int claimed = Interlocked.Decrement(ref state.ClaimedMeshCount);
        if (claimed < 0)
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        int remaining = Interlocked.Decrement(
            ref state.RemainingChunks);
        if (remaining < 0)
        {
            RecordFailure(
                ref state,
                NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        return true;
    }

    internal void RequestCancellation() =>
        Interlocked.Exchange(ref State.CancellationState, 1);

    internal bool CancellationRequested =>
        Volatile.Read(ref State.CancellationState) != 0;

    private bool PublishGeneratedChunks(
        int chunkX,
        int chunkZ,
        ref NativeGtrtSessionState state)
    {
        Span<NativeChunkRecord> chunks = Chunks;
        for (int chunkY = header.MinimumChunkY;
             chunkY <= header.MaximumChunkY;
             chunkY++)
        {
            int chunkIndex = GetChunkIndex(chunkX, chunkY, chunkZ);
            ref NativeChunkRecord chunk = ref chunks[chunkIndex];
            chunk.GenerationEpoch = state.SessionEpoch;
            if (!TryTransition(
                    ref chunk.State,
                    NativeChunkState.Empty,
                    NativeChunkState.Generated))
            {
                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.InvalidGenerationCompletion);
                return false;
            }
        }

        return true;
    }

    private bool ReleaseMeshDependencies(
        int generatedChunkX,
        int generatedChunkZ,
        int epoch,
        ref NativeGtrtSessionState state)
    {
        if (!ReleaseMeshColumn(
                generatedChunkX,
                generatedChunkZ,
                epoch,
                ref state) ||
            !ReleaseMeshColumn(
                generatedChunkX - 1,
                generatedChunkZ,
                epoch,
                ref state) ||
            !ReleaseMeshColumn(
                generatedChunkX + 1,
                generatedChunkZ,
                epoch,
                ref state) ||
            !ReleaseMeshColumn(
                generatedChunkX,
                generatedChunkZ - 1,
                epoch,
                ref state) ||
            !ReleaseMeshColumn(
                generatedChunkX,
                generatedChunkZ + 1,
                epoch,
                ref state))
        {
            return false;
        }

        return true;
    }

    private bool ReleaseMeshColumn(
        int chunkX,
        int chunkZ,
        int epoch,
        ref NativeGtrtSessionState state)
    {
        if (chunkX < -header.Lod1Radius ||
            chunkX > header.Lod1Radius ||
            chunkZ < -header.Lod1Radius ||
            chunkZ > header.Lod1Radius)
        {
            return true;
        }

        Span<NativeChunkRecord> chunks = Chunks;
        Span<NativeWorkItem> jobs = MeshJobs;
        for (int chunkY = header.MinimumChunkY;
             chunkY <= header.MaximumChunkY;
             chunkY++)
        {
            int chunkIndex = GetChunkIndex(chunkX, chunkY, chunkZ);
            ref NativeChunkRecord chunk = ref chunks[chunkIndex];
            int dependencies = Interlocked.Decrement(
                ref chunk.RemainingDependencies);
            if (dependencies < 0)
            {
                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.InvalidMeshDependency);
                return false;
            }

            if (dependencies != 0)
            {
                continue;
            }

            ref NativeWorkItem job = ref jobs[chunkIndex];
            if (!TryTransition(
                    ref job.State,
                    NativeWorkState.Waiting,
                    NativeWorkState.Scheduled))
            {
                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.InvalidMeshDependency);
                return false;
            }

            if (!TryEnqueueMeshReady(chunkIndex, epoch))
            {
                TryTransition(
                    ref job.State,
                    NativeWorkState.Scheduled,
                    NativeWorkState.Waiting);
                RecordFailure(
                    ref state,
                    NativeGtrtFailureCode.MeshReadyQueueFull);
                return false;
            }
        }

        return true;
    }

    private bool TryEnqueueMeshReady(int chunkIndex, int epoch)
    {
        Span<NativeReadySlot> slots = MeshReadySlots;
        ref long enqueuePosition = ref State.MeshEnqueuePosition;
        while (true)
        {
            long position = Volatile.Read(ref enqueuePosition);
            ref NativeReadySlot slot = ref slots[
                (int)(position % slots.Length)];
            long sequence = Volatile.Read(ref slot.Sequence);
            long difference = sequence - position;
            if (difference == 0)
            {
                if (Interlocked.CompareExchange(
                        ref enqueuePosition,
                        position + 1,
                        position) == position)
                {
                    slot.RecordIndex = chunkIndex;
                    slot.Epoch = epoch;
                    Volatile.Write(ref slot.Sequence, position + 1);
                    return true;
                }
            }
            else if (difference < 0)
            {
                return false;
            }
            else
            {
                Thread.SpinWait(1);
            }
        }
    }

    private bool TryDequeueMeshReady(
        out int chunkIndex,
        out int epoch)
    {
        Span<NativeReadySlot> slots = MeshReadySlots;
        ref long dequeuePosition = ref State.MeshDequeuePosition;
        while (true)
        {
            long position = Volatile.Read(ref dequeuePosition);
            ref NativeReadySlot slot = ref slots[
                (int)(position % slots.Length)];
            long sequence = Volatile.Read(ref slot.Sequence);
            long difference = sequence - (position + 1);
            if (difference == 0)
            {
                if (Interlocked.CompareExchange(
                        ref dequeuePosition,
                        position + 1,
                        position) == position)
                {
                    chunkIndex = slot.RecordIndex;
                    epoch = slot.Epoch;
                    Volatile.Write(
                        ref slot.Sequence,
                        position + slots.Length);
                    return true;
                }
            }
            else if (difference < 0)
            {
                chunkIndex = -1;
                epoch = 0;
                return false;
            }
            else
            {
                Thread.SpinWait(1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMeshScratchOffset(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.MeshWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return header.MeshFaceScratchOffset;
        }

        return checked(header.MeshFaceScratchOffset +
            workerIndex * header.MeshFaceScratchCountPerWorker *
            Unsafe.SizeOf<int>());
    }

    private bool TryEnterPacketConsumer()
    {
        ref NativeGtrtSessionState state = ref State;
        if (Volatile.Read(ref state.PacketRecycleState) != 0)
            return false;

        Interlocked.Increment(ref state.PacketConsumerCount);
        if (Volatile.Read(ref state.PacketRecycleState) == 0)
            return true;

        Interlocked.Decrement(ref state.PacketConsumerCount);
        return false;
    }

    private void ExitPacketConsumer()
    {
        int consumers = Interlocked.Decrement(
            ref State.PacketConsumerCount);
        if (consumers < 0)
            Fail(NativeGtrtFailureCode.InvalidPacketRecycle);
    }

    private bool TryReservePacketWords(
        int wordCount,
        out int wordOffset)
    {
        ref int cursor = ref State.PacketWordCursor;
        while (true)
        {
            int observed = Volatile.Read(ref cursor);
            if (wordCount > header.PacketWordCapacity - observed)
            {
                wordOffset = 0;
                return false;
            }

            int next = observed + wordCount;
            if (Interlocked.CompareExchange(
                    ref cursor,
                    next,
                    observed) == observed)
            {
                wordOffset = observed;
                return true;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryTransition<TState>(
        ref TState state,
        TState expected,
        TState next)
        where TState : unmanaged, Enum
    {
        ref int value = ref Unsafe.As<TState, int>(ref state);
        int expectedValue = Unsafe.As<TState, int>(ref expected);
        int nextValue = Unsafe.As<TState, int>(ref next);
        return Interlocked.CompareExchange(
            ref value,
            nextValue,
            expectedValue) == expectedValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TState ReadState<TState>(ref TState state)
        where TState : unmanaged, Enum
    {
        ref int value = ref Unsafe.As<TState, int>(ref state);
        int observed = Volatile.Read(ref value);
        return Unsafe.As<int, TState>(ref observed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordFailure(
        ref NativeGtrtSessionState state,
        NativeGtrtFailureCode failure) =>
        Interlocked.CompareExchange(
            ref state.FailureCode,
            (int)failure,
            (int)NativeGtrtFailureCode.None);

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
    private static readonly NativeLeaseAction<byte> RequestCancellationAction =
        RequestCancellationCore;
    private readonly NativeLeaseAction<byte> publishSeedAction;
    private readonly NativeLeaseAction<byte> initializeGameSnapshotAction;
    private readonly NativeLeaseAction<byte> prepareForDisposalAction;
    private NativeTransfer<byte>? storage;
    private long pendingSeed;
    private byte[]? pendingGameSnapshot;
    private bool disposalPrepared;

    private NativeGtrtSession(NativeTransfer<byte>? source)
    {
        try
        {
            storage = NativeTransfer<byte>.Move(ref source);
            publishSeedAction = PublishSeedCore;
            initializeGameSnapshotAction = InitializeGameSnapshotCore;
            prepareForDisposalAction = PrepareForDisposalCore;
        }
        finally
        {
            source?.Dispose();
        }
    }

    internal static NativeGtrtSession Create(
        GameSettings settings,
        NativeTerrainMaterialSet materials,
        int generationWorkerCount = 1,
        int meshWorkerCount = 1) =>
        Create(NativeGtrtSessionLayout.Create(
            settings,
            materials,
            generationWorkerCount,
            meshWorkerCount));

    internal static NativeGtrtSession Create(
        GameSettings settings,
        NativeGameSnapshot game,
        int generationWorkerCount = 1,
        int meshWorkerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(game);
        byte[] gameSnapshot = game.CopyBytes();
        var gameView = new NativeGameSnapshotView(gameSnapshot);
        NativeGtrtSession session = Create(
            NativeGtrtSessionLayout.Create(
                settings,
                gameView.GetGeneratedMaterials(),
                generationWorkerCount,
                meshWorkerCount,
                gameSnapshot.Length));
        try
        {
            session.InitializeGameSnapshot(gameSnapshot);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal static NativeGtrtSession Create(
        NativeGtrtSessionLayout layout,
        NativeGameSnapshot game)
    {
        ArgumentNullException.ThrowIfNull(game);
        byte[] gameSnapshot = game.CopyBytes();
        if (layout.GameSnapshotByteCount != gameSnapshot.Length)
        {
            throw new ArgumentException(
                "The native game snapshot capacity does not match the source.",
                nameof(layout));
        }

        NativeGtrtSession session = Create(layout);
        try
        {
            session.InitializeGameSnapshot(gameSnapshot);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

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

    private void InitializeGameSnapshot(byte[] gameSnapshot)
    {
        pendingGameSnapshot = gameSnapshot;
        try
        {
            if (storage is null)
                throw new ObjectDisposedException(nameof(NativeGtrtSession));

            storage.Access(initializeGameSnapshotAction);
        }
        finally
        {
            pendingGameSnapshot = null;
        }
    }

    internal void Access(NativeLeaseAction<byte> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (storage is null)
            throw new ObjectDisposedException(nameof(NativeGtrtSession));

        storage.Access(action);
    }

    internal void RequestCancellation()
    {
        if (storage is null)
            return;

        storage.Access(RequestCancellationAction);
    }

    public void Dispose()
    {
        if (storage is null)
            return;

        disposalPrepared = false;
        storage.Access(prepareForDisposalAction);
        if (!disposalPrepared)
        {
            throw new InvalidOperationException(
                "The native GTRT session still has active mesh work.");
        }

        try
        {
            storage.Dispose();
        }
        finally
        {
            storage = null;
        }
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
        NativeOpenSimplexNoiseState noise = view.NoiseState;
        noise.Initialize(pendingSeed);
        Volatile.Write(ref state.PublicationState, 1);
    }

    private void InitializeGameSnapshotCore(
        scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        if (view.State.PublicationState != 0 ||
            pendingGameSnapshot is null ||
            pendingGameSnapshot.Length != view.GameSnapshotBytes.Length)
        {
            throw new InvalidOperationException(
                "The native game snapshot cannot be initialized.");
        }

        pendingGameSnapshot.CopyTo(view.GameSnapshotBytes);
        _ = view.GameSnapshot;
    }

    private static void RequestCancellationCore(
        scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        view.RequestCancellation();
    }

    private void PrepareForDisposalCore(
        scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        disposalPrepared = view.TryPrepareForDisposal();
    }
}

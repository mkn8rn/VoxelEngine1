using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeGtrtSessionLayout
{
    private const int DefaultPacketWordsPerRequiredChunk = 8_192;
    internal const int DefaultMaterializedChunkCapacity = 64;
    internal const int DefaultMaterializedSectionCapacity = 4_096;
#pragma warning disable MA0051 // Immutable native layout properties must be assigned in this constructor; keeping the complete versioned mapping together preserves ABI auditability.
    internal NativeGtrtSessionLayout(int chunkSizeX, int chunkSizeY, int chunkSizeZ, int lod1Radius, NativeTerrainMaterialSet materials, int generationWorkerCount = 1, int meshWorkerCount = 1, int packetWordCapacity = 0, int gameSnapshotByteCount = 0, int materializedChunkCapacity = 0, int materializedSectionCapacity = 0, int materializedRawSectionCapacity = -1, int materializedPaletteCapacity = 0, int materializedPackedWordCapacity = 0)
#pragma warning restore MA0051
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSizeZ);
        ArgumentOutOfRangeException.ThrowIfNegative(lod1Radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(meshWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(packetWordCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(gameSnapshotByteCount);
        ArgumentOutOfRangeException.ThrowIfNegative(materializedChunkCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(materializedSectionCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(materializedRawSectionCapacity, -1);

        ArgumentOutOfRangeException.ThrowIfNegative(materializedPaletteCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(materializedPackedWordCapacity);
        int resolvedRawSectionCapacity = materializedRawSectionCapacity < 0 ? materializedSectionCapacity : materializedRawSectionCapacity;
        if ((materializedChunkCapacity == 0) != (materializedSectionCapacity == 0))
        {
            throw new ArgumentException("Materialized chunk and section capacities must both be zero or positive.", nameof(materializedSectionCapacity));
        }

        if (materializedSectionCapacity == 0 && (resolvedRawSectionCapacity != 0 || materializedPaletteCapacity != 0 || materializedPackedWordCapacity != 0))
        {
            throw new ArgumentException("Materialized payload capacities require section storage.", nameof(materializedSectionCapacity));
        }

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
        RequiredColumnCount = checked(RequiredColumnWidth * RequiredColumnWidth);
        RequiredChunkCount = checked(RequiredColumnCount * VerticalChunkCount);
        ProfilesPerColumn = checked(chunkSizeX * chunkSizeZ);
        ProfileCount = checked(ColumnCount * ProfilesPerColumn);
        SectionCountX = checked((chunkSizeX + VoxelSection.Size - 1) / VoxelSection.Size);
        SectionCountY = checked((chunkSizeY + VoxelSection.Size - 1) / VoxelSection.Size);
        SectionCountZ = checked((chunkSizeZ + VoxelSection.Size - 1) / VoxelSection.Size);
        SectionsPerChunk = checked(SectionCountX * SectionCountY * SectionCountZ);
        MaterializedChunkCapacity = materializedChunkCapacity;
        MaterializedIndexCapacity = materializedChunkCapacity == 0 ? 0 : checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2(checked((uint)materializedChunkCapacity * 2)));
        MaterializedSectionCapacity = materializedSectionCapacity;
        MaterializedRawSectionCapacity = resolvedRawSectionCapacity;
        MaterializedPaletteCapacity = materializedPaletteCapacity;
        MaterializedPackedWordCapacity = materializedPackedWordCapacity;
        MaterializedSectionMapCount = checked(materializedChunkCapacity * SectionsPerChunk);
        MaterializedRawVoxelCount = checked(resolvedRawSectionCapacity * VoxelSection.VoxelCount);
        GenerationWorkerCount = generationWorkerCount;
        GenerationFloatCountPerWorker = checked(ProfilesPerColumn * 2);
        GenerationLatticeCountPerWorker = TerrainGenerationUtils.GetSmoothValueNoiseLatticeCapacity(chunkSizeX, chunkSizeZ);
        MeshWorkerCount = meshWorkerCount;
        int maximumFacePlaneCount = Math.Max(ProfilesPerColumn, Math.Max(checked(chunkSizeX * chunkSizeY), checked(chunkSizeY * chunkSizeZ)));
        MeshFaceScratchCountPerWorker = checked(maximumFacePlaneCount * 2);
        PacketWordCapacity = packetWordCapacity == 0 ? checked(RequiredChunkCount * DefaultPacketWordsPerRequiredChunk) : packetWordCapacity;
        Materials = materials;
        GameSnapshotByteCount = gameSnapshotByteCount;
        int cursor = Align(Unsafe.SizeOf<NativeGtrtSessionHeader>(), 8);
        StateOffset = cursor;
        cursor = AddRange<NativeGtrtSessionState>(cursor, 1, 8);
        NoiseStateOffset = cursor;
        cursor = AddRange<byte>(cursor, NativeOpenSimplexNoiseState.StateByteCount, 8);
        MaterialOffset = cursor;
        cursor = AddRange<NativeTerrainMaterialSet>(cursor, 1, 8);
        ColumnOffset = cursor;
        cursor = AddRange<NativeColumnRecord>(cursor, ColumnCount, 8);
        ProfileOffset = cursor;
        cursor = AddRange<BlockColumnProfile>(cursor, ProfileCount, 8);
        ColumnSummaryOffset = cursor;
        cursor = AddRange<NativeColumnSummary>(cursor, ColumnCount, 8);
        GenerationWorkspaceOffset = cursor;
        cursor = AddRange<NativeGenerationWorkspaceRecord>(cursor, GenerationWorkerCount, 8);
        GenerationFloatScratchOffset = cursor;
        cursor = AddRange<float>(cursor, checked(GenerationWorkerCount * GenerationFloatCountPerWorker), 8);
        GenerationXScratchOffset = cursor;
        cursor = AddRange<TerrainGenerationUtils.NoiseAxisSample>(cursor, checked(GenerationWorkerCount * ChunkSizeX), 8);
        GenerationZScratchOffset = cursor;
        cursor = AddRange<TerrainGenerationUtils.NoiseAxisSample>(cursor, checked(GenerationWorkerCount * ChunkSizeZ), 8);
        GenerationLatticeScratchOffset = cursor;
        cursor = AddRange<float>(cursor, checked(GenerationWorkerCount * GenerationLatticeCountPerWorker), 8);
        MeshWorkspaceOffset = cursor;
        cursor = AddRange<NativeMeshWorkspaceRecord>(cursor, MeshWorkerCount, 8);
        MeshFaceScratchOffset = cursor;
        cursor = AddRange<int>(cursor, checked(MeshWorkerCount * MeshFaceScratchCountPerWorker), 8);
        ChunkOffset = cursor;
        cursor = AddRange<NativeChunkRecord>(cursor, ChunkCount, 8);
        MaterializedChunkOffset = cursor;
        cursor = AddRange<NativeMaterializedChunkRecord>(cursor, MaterializedChunkCapacity, 8);
        MaterializedSectionMapOffset = cursor;
        cursor = AddRange<int>(cursor, MaterializedSectionMapCount, 8);
        MaterializedSectionOffset = cursor;
        cursor = AddRange<NativeMaterializedSectionRecord>(cursor, MaterializedSectionCapacity, 8);
        MaterializedRawVoxelOffset = cursor;
        cursor = AddRange<ushort>(cursor, MaterializedRawVoxelCount, 8);
        MaterializedPaletteOffset = cursor;
        cursor = AddRange<ushort>(cursor, MaterializedPaletteCapacity, 8);
        MaterializedPackedWordOffset = cursor;
        cursor = AddRange<uint>(cursor, MaterializedPackedWordCapacity, 8);
        GenerationJobOffset = cursor;
        cursor = AddRange<NativeWorkItem>(cursor, ColumnCount, 8);
        MeshJobOffset = cursor;
        cursor = AddRange<NativeWorkItem>(cursor, ChunkCount, 8);
        PacketOffset = cursor;
        cursor = AddRange<NativeRenderPacketRecord>(cursor, ChunkCount, 8);
        PacketWordOffset = cursor;
        cursor = AddRange<uint>(cursor, PacketWordCapacity, 8);
        MeshReadyOffset = cursor;
        cursor = AddRange<NativeReadySlot>(cursor, RequiredChunkCount, 8);
        GameSnapshotOffset = cursor;
        cursor = AddRange<byte>(cursor, GameSnapshotByteCount, 8);
        MaterializedIndexOffset = cursor;
        cursor = AddRange<int>(cursor, MaterializedIndexCapacity, 8);
        Streaming = new NativeStreamingLayout(cursor, ColumnCount, ChunkCount, ProfileCount, RequiredChunkCount);
        TotalByteCount = Streaming.EndOffset;
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
    internal int SectionCountX { get; }
    internal int SectionCountY { get; }
    internal int SectionCountZ { get; }
    internal int SectionsPerChunk { get; }
    internal int MaterializedChunkCapacity { get; }
    internal int MaterializedSectionCapacity { get; }
    internal int MaterializedRawSectionCapacity { get; }
    internal int MaterializedPaletteCapacity { get; }
    internal int MaterializedPackedWordCapacity { get; }
    internal int MaterializedSectionMapCount { get; }
    internal int MaterializedRawVoxelCount { get; }
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
    internal int MaterializedChunkOffset { get; }
    internal int MaterializedSectionMapOffset { get; }
    internal int MaterializedSectionOffset { get; }
    internal int MaterializedRawVoxelOffset { get; }
    internal int MaterializedPaletteOffset { get; }
    internal int MaterializedPackedWordOffset { get; }
    internal int GenerationJobOffset { get; }
    internal int MeshJobOffset { get; }
    internal int PacketOffset { get; }
    internal int PacketWordOffset { get; }
    internal int MeshReadyOffset { get; }
    internal int GameSnapshotOffset { get; }
    internal int MaterializedIndexOffset { get; }
    internal int MaterializedIndexCapacity { get; }
    internal NativeStreamingLayout Streaming { get; }
    internal int TotalByteCount { get; }

    internal static NativeGtrtSessionLayout Create(GameSettings settings, NativeTerrainMaterialSet materials, int generationWorkerCount = 1, int meshWorkerCount = 1, int gameSnapshotByteCount = 0, int materializedChunkCapacity = 0, int materializedSectionCapacity = 0, int materializedRawSectionCapacity = -1, int materializedPaletteCapacity = 0, int materializedPackedWordCapacity = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new NativeGtrtSessionLayout(settings.chunkMaxX, settings.chunkMaxY, settings.chunkMaxZ, settings.lod1RenderDistance, materials, generationWorkerCount, meshWorkerCount, gameSnapshotByteCount: gameSnapshotByteCount, materializedChunkCapacity: materializedChunkCapacity, materializedSectionCapacity: materializedSectionCapacity, materializedRawSectionCapacity: materializedRawSectionCapacity, materializedPaletteCapacity: materializedPaletteCapacity, materializedPackedWordCapacity: materializedPackedWordCapacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetColumnIndex(int chunkX, int chunkZ)
    {
        int localX = chunkX - MinimumChunkX;
        int localZ = chunkZ - MinimumChunkZ;
        if ((uint)localX >= (uint)ColumnWidth || (uint)localZ >= (uint)ColumnWidth)
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

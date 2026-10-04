using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeGtrtSessionHeader
{
    internal const uint ExpectedMagic = 0x54525447;
    internal const int ExpectedVersion = 19;
#pragma warning disable MA0051 // Immutable native layout properties must be assigned in this constructor; keeping the complete versioned mapping together preserves ABI auditability.
    internal NativeGtrtSessionHeader(NativeGtrtSessionLayout layout)
#pragma warning restore MA0051
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
        SectionCountX = layout.SectionCountX;
        SectionCountY = layout.SectionCountY;
        SectionCountZ = layout.SectionCountZ;
        SectionsPerChunk = layout.SectionsPerChunk;
        MaterializedChunkCapacity = layout.MaterializedChunkCapacity;
        MaterializedSectionCapacity = layout.MaterializedSectionCapacity;
        MaterializedRawSectionCapacity = layout.MaterializedRawSectionCapacity;
        MaterializedPaletteCapacity = layout.MaterializedPaletteCapacity;
        MaterializedPackedWordCapacity = layout.MaterializedPackedWordCapacity;
        MaterializedSectionMapCount = layout.MaterializedSectionMapCount;
        MaterializedRawVoxelCount = layout.MaterializedRawVoxelCount;
        GenerationWorkerCount = layout.GenerationWorkerCount;
        GenerationFloatCountPerWorker = layout.GenerationFloatCountPerWorker;
        GenerationLatticeCountPerWorker = layout.GenerationLatticeCountPerWorker;
        StateOffset = layout.StateOffset;
        NoiseStateOffset = layout.NoiseStateOffset;
        MaterialOffset = layout.MaterialOffset;
        ColumnOffset = layout.ColumnOffset;
        ProfileOffset = layout.ProfileOffset;
        ColumnSummaryOffset = layout.ColumnSummaryOffset;
        GenerationWorkspaceOffset = layout.GenerationWorkspaceOffset;
        GenerationFloatScratchOffset = layout.GenerationFloatScratchOffset;
        GenerationXScratchOffset = layout.GenerationXScratchOffset;
        GenerationZScratchOffset = layout.GenerationZScratchOffset;
        GenerationLatticeScratchOffset = layout.GenerationLatticeScratchOffset;
        ChunkOffset = layout.ChunkOffset;
        MaterializedChunkOffset = layout.MaterializedChunkOffset;
        MaterializedSectionMapOffset = layout.MaterializedSectionMapOffset;
        MaterializedSectionOffset = layout.MaterializedSectionOffset;
        MaterializedRawVoxelOffset = layout.MaterializedRawVoxelOffset;
        MaterializedPaletteOffset = layout.MaterializedPaletteOffset;
        MaterializedPackedWordOffset = layout.MaterializedPackedWordOffset;
        GenerationJobOffset = layout.GenerationJobOffset;
        MeshJobOffset = layout.MeshJobOffset;
        PacketOffset = layout.PacketOffset;
        MeshReadyOffset = layout.MeshReadyOffset;
        MeshWorkerCount = layout.MeshWorkerCount;
        MeshFaceScratchCountPerWorker = layout.MeshFaceScratchCountPerWorker;
        PacketWordCapacity = layout.PacketWordCapacity;
        MeshWorkspaceOffset = layout.MeshWorkspaceOffset;
        MeshFaceScratchOffset = layout.MeshFaceScratchOffset;
        PacketWordOffset = layout.PacketWordOffset;
        GameSnapshotOffset = layout.GameSnapshotOffset;
        GameSnapshotByteCount = layout.GameSnapshotByteCount;
        Streaming = layout.Streaming;
        MaterializedIndexOffset = layout.MaterializedIndexOffset;
        MaterializedIndexCapacity = layout.MaterializedIndexCapacity;
    }

    internal int MaterializedIndexOffset { get; }
    internal int MaterializedIndexCapacity { get; }
    internal NativeStreamingLayout Streaming { get; }
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
    internal int MaterializedChunkOffset { get; }
    internal int MaterializedSectionMapOffset { get; }
    internal int MaterializedSectionOffset { get; }
    internal int MaterializedRawVoxelOffset { get; }
    internal int MaterializedPaletteOffset { get; }
    internal int MaterializedPackedWordOffset { get; }
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

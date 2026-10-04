using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal ref struct NativeGtrtSessionInitializer
{
    private Span<byte> bytes;
    internal NativeGtrtSessionInitializer(Span<byte> bytes)
    {
        this.bytes = bytes;
    }

    internal void Initialize(scoped in NativeGtrtSessionLayout layout)
    {
        bytes.Clear();
        var header = new NativeGtrtSessionHeader(layout);
        MemoryMarshal.Write(bytes, in header);
        NativeTerrainMaterialSet materials = layout.Materials;
        MemoryMarshal.Write(bytes.Slice(layout.MaterialOffset), in materials);
        var state = new NativeGtrtSessionState
        {
            RemainingColumns = layout.ColumnCount,
            RemainingChunks = layout.RequiredChunkCount,
            PlannedColumns = layout.ColumnCount,
            PlannedMeshes = layout.RequiredChunkCount,
            FreeRangeCount = 1
        };
        MemoryMarshal.Write(bytes.Slice(layout.StateOffset), in state);
        bytes.Slice(layout.ProfileOffset, checked(layout.ProfileCount * Unsafe.SizeOf<BlockColumnProfile>())).Fill(byte.MaxValue);
        int emptyMapEntry = -1;
        for (int index = 0; index < layout.MaterializedSectionMapCount; index++)
        {
            MemoryMarshal.Write(bytes.Slice(checked(layout.MaterializedSectionMapOffset + index * sizeof(int))), in emptyMapEntry);
        }

        int columnSize = Unsafe.SizeOf<NativeColumnRecord>();
        int chunkSize = Unsafe.SizeOf<NativeChunkRecord>();
        int workItemSize = Unsafe.SizeOf<NativeWorkItem>();
        for (int chunkX = layout.MinimumChunkX; chunkX <= layout.MaximumChunkX; chunkX++)
        {
            for (int chunkZ = layout.MinimumChunkZ; chunkZ <= layout.MaximumChunkZ; chunkZ++)
            {
                int columnIndex = layout.GetColumnIndex(chunkX, chunkZ);
                int profileOffset = checked(columnIndex * layout.ProfilesPerColumn);
                var column = new NativeColumnRecord
                {
                    ChunkX = chunkX,
                    ChunkZ = chunkZ,
                    ProfileOffset = profileOffset,
                    BiomeIndex = -1
                };
                MemoryMarshal.Write(bytes.Slice(checked(layout.ColumnOffset + columnIndex * columnSize)), in column);
                var generationJob = new NativeWorkItem
                {
                    RecordIndex = columnIndex,
                    Epoch = 1,
                    Kind = NativeWorkKind.GenerateColumn,
                    State = NativeWorkState.Scheduled
                };
                MemoryMarshal.Write(bytes.Slice(checked(layout.GenerationJobOffset + columnIndex * workItemSize)), in generationJob);
                bool initialMeshRequired = chunkX >= -layout.Lod1Radius && chunkX <= layout.Lod1Radius && chunkZ >= -layout.Lod1Radius && chunkZ <= layout.Lod1Radius;
                for (int chunkY = layout.MinimumChunkY; chunkY <= layout.MaximumChunkY; chunkY++)
                {
                    int chunkIndex = layout.GetChunkIndex(chunkX, chunkY, chunkZ);
                    var chunk = new NativeChunkRecord
                    {
                        ChunkX = chunkX,
                        ChunkY = chunkY,
                        ChunkZ = chunkZ,
                        ColumnIndex = columnIndex,
                        ProfileOffset = profileOffset,
                        PacketIndex = chunkIndex,
                        Flags = initialMeshRequired ? (int)NativeChunkFlags.InitialMeshRequired : 0,
                        RemainingDependencies = initialMeshRequired ? 5 : 0,
                        StorageKind = NativeChunkStorageKind.GeneratedProfile,
                        MaterializedChunkIndex = -1
                    };
                    MemoryMarshal.Write(bytes.Slice(checked(layout.ChunkOffset + chunkIndex * chunkSize)), in chunk);
                    var meshJob = new NativeWorkItem
                    {
                        RecordIndex = chunkIndex,
                        Epoch = 1,
                        Kind = NativeWorkKind.BuildChunkMesh,
                        State = initialMeshRequired ? NativeWorkState.Waiting : NativeWorkState.Canceled
                    };
                    MemoryMarshal.Write(bytes.Slice(checked(layout.MeshJobOffset + chunkIndex * workItemSize)), in meshJob);
                }
            }
        }

        int readySlotSize = Unsafe.SizeOf<NativeReadySlot>();
        for (int index = 0; index < layout.RequiredChunkCount; index++)
        {
            var readySlot = new NativeReadySlot
            {
                Sequence = index
            };
            MemoryMarshal.Write(bytes.Slice(checked(layout.MeshReadyOffset + index * readySlotSize)), in readySlot);
        }

        var freeRange = new NativePacketWordRange
        {
            Count = layout.PacketWordCapacity
        };
        MemoryMarshal.Write(bytes.Slice(layout.Streaming.FreeRanges), in freeRange);
    }
}

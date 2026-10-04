using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
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
    internal NativeChunkStorageKind StorageKind;
    internal int MaterializedChunkIndex;
}

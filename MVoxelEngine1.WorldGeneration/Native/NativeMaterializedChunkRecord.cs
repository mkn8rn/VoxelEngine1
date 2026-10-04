using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeMaterializedChunkRecord
{
    internal int ChunkX;
    internal int ChunkY;
    internal int ChunkZ;
    internal NativeChunkStorageKind StorageKind;
    internal int SectionMapOffset;
    internal int State;
    internal long Revision;
    internal long PersistedRevision;
    internal ushort UniformBlockId;
    internal float Temperature;
    internal float Humidity;
    internal NativeSavedChunkSource SavedSource;
}

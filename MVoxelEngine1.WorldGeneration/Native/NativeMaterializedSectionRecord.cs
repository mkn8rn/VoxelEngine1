using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeMaterializedSectionRecord
{
    internal int OwnerChunkIndex;
    internal int SectionIndex;
    internal int RawVoxelOffset;
    internal int PaletteOffset;
    internal int PackedWordOffset;
    internal int PackedWordCount;
    internal int Revision;
    internal ushort UniformBlockId;
    internal ushort PaletteCount;
    internal byte BitsPerIndex;
    internal NativeSectionStorageKind StorageKind;
}

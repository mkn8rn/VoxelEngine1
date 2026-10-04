using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeSavedChunkSource
{
    internal int FileIndex;
    internal long PayloadOffset;
    internal int PayloadByteCount;
    internal int SectionCount;
    internal int RawSectionCount;
    internal int PaletteCount;
    internal int PackedWordCount;
    internal ushort UniformBlockId;
    internal byte IsUniform;
}

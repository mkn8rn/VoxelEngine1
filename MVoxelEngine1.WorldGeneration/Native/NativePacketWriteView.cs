using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal readonly ref struct NativePacketWriteView
{
    internal NativePacketWriteView(Span<uint> opaqueWords, Span<uint> transparentWords)
    {
        OpaqueWords = opaqueWords;
        TransparentWords = transparentWords;
    }

    internal Span<uint> OpaqueWords { get; }
    internal Span<uint> TransparentWords { get; }
}

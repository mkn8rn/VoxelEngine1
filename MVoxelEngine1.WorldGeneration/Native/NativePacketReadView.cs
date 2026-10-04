using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal readonly ref struct NativePacketReadView
{
    internal NativePacketReadView(NativeRenderPacketRecord record, ReadOnlySpan<uint> opaqueWords, ReadOnlySpan<uint> transparentWords)
    {
        Record = record;
        OpaqueWords = opaqueWords;
        TransparentWords = transparentWords;
    }

    internal NativeRenderPacketRecord Record { get; }
    internal ReadOnlySpan<uint> OpaqueWords { get; }
    internal ReadOnlySpan<uint> TransparentWords { get; }
}

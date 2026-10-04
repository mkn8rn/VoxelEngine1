using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal readonly ref struct NativeSessionCopyState(ReadOnlySpan<byte> source, NativeGtrtSessionHeader previous, NativeGtrtSessionLayout layout)
{
    internal ReadOnlySpan<byte> Source { get; } = source;
    internal NativeGtrtSessionHeader Previous { get; } = previous;
    internal NativeGtrtSessionLayout Layout { get; } = layout;
}

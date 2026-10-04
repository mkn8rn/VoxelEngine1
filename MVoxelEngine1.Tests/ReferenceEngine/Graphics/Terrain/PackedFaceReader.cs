using MVoxelEngine1.Infrastructure.Models;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Graphics.Terrain
{
    internal delegate TResult PackedFaceReader<TResult>(ReadOnlySpan<uint> rectangles);
}

using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
    public sealed record SimulatedGpuUploadBoundarySnapshot
    {
        public required long RenderDataId { get; init; }
        public required int ChunkX { get; init; }
        public required int ChunkY { get; init; }
        public required int ChunkZ { get; init; }
        public required int OpaqueFaceCount { get; init; }
        public required int OpaqueRectangleCount { get; init; }
        public required int OpaqueWordCount { get; init; }
        public required int TransparentFaceCount { get; init; }
        public required int TransparentRectangleCount { get; init; }
        public required int TransparentWordCount { get; init; }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.WorldGeneration.Native;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration
{
    public sealed class ChunkFaceManifest
    {
        public required int ChunkX { get; init; }
        public required int ChunkY { get; init; }
        public required int ChunkZ { get; init; }
        public required bool FullyOccluded { get; init; }
        public required CanonicalFaceSetDigest Faces { get; init; }
    }
}

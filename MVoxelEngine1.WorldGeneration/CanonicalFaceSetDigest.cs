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
    public sealed class CanonicalFaceSetDigest
    {
        public required long FaceCount { get; init; }
        public required long OpaqueFaceCount { get; init; }
        public required long TransparentFaceCount { get; init; }
        public required string Sha256 { get; init; }
        public required string OpaqueSha256 { get; init; }
        public required string TransparentSha256 { get; init; }
        public required IReadOnlyList<FaceDirectionDigest> OpaqueDirections { get; init; }
        public required IReadOnlyList<FaceDirectionDigest> TransparentDirections { get; init; }
    }
}

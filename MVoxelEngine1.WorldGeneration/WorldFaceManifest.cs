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
    public sealed class WorldFaceManifest
    {
        public required int SchemaVersion { get; init; }
        public required string CanonicalEncoding { get; init; }
        public required string Game { get; init; }
        public required int Seed { get; init; }
        public required FaceGenerationMode FaceGenerationMode { get; init; }
        public required int ChunkSizeX { get; init; }
        public required int ChunkSizeY { get; init; }
        public required int ChunkSizeZ { get; init; }
        public required int Lod1Radius { get; init; }
        public required int ActiveChunkCount { get; init; }
        public required int CaptureCenterChunkX { get; init; }
        public required int CaptureCenterChunkY { get; init; }
        public required int CaptureCenterChunkZ { get; init; }
        public required string ActiveCoordinateSha256 { get; init; }
        public required string GameInputSha256 { get; init; }
        public required string BlockRegistrySha256 { get; init; }
        public required CanonicalFaceSetDigest Faces { get; init; }
        public required IReadOnlyList<ChunkFaceManifest> Chunks { get; init; }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration
{
    public enum CanonicalRenderPass : byte
    {
        Opaque = 0,
        Transparent = 1
    }

    public readonly record struct CanonicalRenderFace(
        int WorldX,
        int WorldY,
        int WorldZ,
        byte Direction,
        CanonicalRenderPass RenderPass,
        ushort BlockId,
        ushort NeighborBlockId);

    public sealed class FaceDirectionDigest
    {
        public required byte Direction { get; init; }

        public required long FaceCount { get; init; }

        public required string Sha256 { get; init; }
    }

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

    public sealed class ChunkFaceManifest
    {
        public required int ChunkX { get; init; }

        public required int ChunkY { get; init; }

        public required int ChunkZ { get; init; }

        public required bool FullyOccluded { get; init; }

        public required CanonicalFaceSetDigest Faces { get; init; }
    }

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

    public static class CanonicalRenderFaceHasher
    {
        public const string Encoding =
            "chunk-major; face=(worldX:i32le,worldY:i32le,worldZ:i32le," +
            "direction:u8,pass:u8,blockId:u16le,neighborBlockId:u16le); sha256";

        private static readonly IComparer<CanonicalRenderFace> Comparer =
            Comparer<CanonicalRenderFace>.Create(CompareFaces);

        public static CanonicalFaceSetDigest Hash(
            IEnumerable<CanonicalRenderFace> source)
        {
            ArgumentNullException.ThrowIfNull(source);
            CanonicalRenderFace[] faces = source.ToArray();
            Array.Sort(faces, Comparer);

            using IncrementalHash all = CreateHasher("all");
            using IncrementalHash opaque = CreateHasher("opaque");
            using IncrementalHash transparent = CreateHasher("transparent");
            IncrementalHash[] opaqueDirections = CreateDirectionHashers("opaque");
            IncrementalHash[] transparentDirections = CreateDirectionHashers("transparent");
            var opaqueCounts = new long[6];
            var transparentCounts = new long[6];
            long opaqueCount = 0;
            long transparentCount = 0;

            try
            {
                CanonicalRenderFace? previous = null;
                Span<byte> encoded = stackalloc byte[18];
                foreach (CanonicalRenderFace face in faces)
                {
                    Validate(face);
                    if (previous.HasValue && previous.Value == face)
                        throw new InvalidOperationException($"Duplicate canonical face: {face}.");

                    Encode(face, encoded);
                    all.AppendData(encoded);
                    if (face.RenderPass == CanonicalRenderPass.Opaque)
                    {
                        opaque.AppendData(encoded);
                        opaqueDirections[face.Direction].AppendData(encoded);
                        opaqueCounts[face.Direction]++;
                        opaqueCount++;
                    }
                    else
                    {
                        transparent.AppendData(encoded);
                        transparentDirections[face.Direction].AppendData(encoded);
                        transparentCounts[face.Direction]++;
                        transparentCount++;
                    }

                    previous = face;
                }

                return new CanonicalFaceSetDigest
                {
                    FaceCount = faces.LongLength,
                    OpaqueFaceCount = opaqueCount,
                    TransparentFaceCount = transparentCount,
                    Sha256 = GetHex(all),
                    OpaqueSha256 = GetHex(opaque),
                    TransparentSha256 = GetHex(transparent),
                    OpaqueDirections = FinishDirections(
                        opaqueDirections,
                        opaqueCounts),
                    TransparentDirections = FinishDirections(
                        transparentDirections,
                        transparentCounts)
                };
            }
            finally
            {
                DisposeAll(opaqueDirections);
                DisposeAll(transparentDirections);
            }
        }

        private static IncrementalHash[] CreateDirectionHashers(string pass)
        {
            var result = new IncrementalHash[6];
            for (byte direction = 0; direction < result.Length; direction++)
                result[direction] = CreateHasher($"{pass}:{direction}");
            return result;
        }

        private static IReadOnlyList<FaceDirectionDigest> FinishDirections(
            IncrementalHash[] hashers,
            long[] counts)
        {
            var result = new FaceDirectionDigest[6];
            for (byte direction = 0; direction < result.Length; direction++)
            {
                result[direction] = new FaceDirectionDigest
                {
                    Direction = direction,
                    FaceCount = counts[direction],
                    Sha256 = GetHex(hashers[direction])
                };
            }

            return result;
        }

        private static IncrementalHash CreateHasher(string scope)
        {
            IncrementalHash result = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(result, "MVoxelEngine1.CanonicalRenderFace.v1");
            AppendString(result, scope);
            return result;
        }

        private static void Encode(
            CanonicalRenderFace face,
            Span<byte> destination)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, face.WorldX);
            BinaryPrimitives.WriteInt32LittleEndian(destination[4..], face.WorldY);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..], face.WorldZ);
            destination[12] = face.Direction;
            destination[13] = (byte)face.RenderPass;
            BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], face.BlockId);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], face.NeighborBlockId);
        }

        private static void Validate(CanonicalRenderFace face)
        {
            if (face.Direction >= 6)
                throw new ArgumentOutOfRangeException(nameof(face), "The face direction must be from 0 through 5.");
            if (!Enum.IsDefined(face.RenderPass))
                throw new ArgumentOutOfRangeException(nameof(face), "The render pass is invalid.");
            if (face.BlockId == (ushort)BaseBlockType.Empty)
                throw new ArgumentException("A rendered face cannot use the empty block identifier.", nameof(face));
        }

        private static int CompareFaces(
            CanonicalRenderFace left,
            CanonicalRenderFace right)
        {
            int comparison = left.WorldX.CompareTo(right.WorldX);
            if (comparison != 0) return comparison;
            comparison = left.WorldY.CompareTo(right.WorldY);
            if (comparison != 0) return comparison;
            comparison = left.WorldZ.CompareTo(right.WorldZ);
            if (comparison != 0) return comparison;
            comparison = left.Direction.CompareTo(right.Direction);
            if (comparison != 0) return comparison;
            comparison = left.RenderPass.CompareTo(right.RenderPass);
            if (comparison != 0) return comparison;
            comparison = left.BlockId.CompareTo(right.BlockId);
            if (comparison != 0) return comparison;
            return left.NeighborBlockId.CompareTo(right.NeighborBlockId);
        }

        internal static void AppendString(
            IncrementalHash hash,
            string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        internal static string GetHex(IncrementalHash hash)
        {
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static void DisposeAll(IEnumerable<IncrementalHash> hashers)
        {
            foreach (IncrementalHash hasher in hashers)
                hasher.Dispose();
        }
    }

    public static class WorldFaceManifestBuilder
    {
        private static readonly (int X, int Y, int Z)[] FaceNormals =
        {
            (-1, 0, 0),
            (1, 0, 0),
            (0, -1, 0),
            (0, 1, 0),
            (0, 0, -1),
            (0, 0, 1)
        };

        public static WorldFaceManifest Capture(
            World world,
            string game,
            int seed,
            FaceGenerationMode faceGenerationMode)
        {
            ArgumentNullException.ThrowIfNull(world);
            ArgumentException.ThrowIfNullOrWhiteSpace(game);

            using IDisposable stateScope = world.AcquireRenderStateReadScope();
            (int centerX, int centerY, int centerZ) = world.PlayerChunkPosition;
            WorldRenderChunk[] chunks = world.CaptureRequiredRenderChunks()
                .OrderBy(chunk => chunk.ChunkX)
                .ThenBy(chunk => chunk.ChunkY)
                .ThenBy(chunk => chunk.ChunkZ)
                .ToArray();
            var chunkManifests = new ChunkFaceManifest[chunks.Length];
            var allFaces = new List<CanonicalRenderFace>();

            for (int index = 0; index < chunks.Length; index++)
            {
                WorldRenderChunk chunk = chunks[index];
                ChunkRenderUploadData? data = chunk.UploadData;
                if (data is not null && data.FaceGenerationMode != faceGenerationMode)
                {
                    throw new InvalidOperationException(
                        $"Chunk ({chunk.ChunkX}, {chunk.ChunkY}, {chunk.ChunkZ}) uses " +
                        $"face mode {data.FaceGenerationMode}, not {faceGenerationMode}.");
                }

                List<CanonicalRenderFace> chunkFaces = data is null
                    ? new List<CanonicalRenderFace>()
                    : CaptureChunkFaces(world, chunk, data);
                CanonicalFaceSetDigest digest = CanonicalRenderFaceHasher.Hash(chunkFaces);
                bool fullyOccluded = data?.FullyOccluded ?? true;
                if (fullyOccluded && digest.FaceCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Fully occluded chunk ({chunk.ChunkX}, {chunk.ChunkY}, {chunk.ChunkZ}) has faces.");
                }

                allFaces.AddRange(chunkFaces);
                chunkManifests[index] = new ChunkFaceManifest
                {
                    ChunkX = chunk.ChunkX,
                    ChunkY = chunk.ChunkY,
                    ChunkZ = chunk.ChunkZ,
                    FullyOccluded = fullyOccluded,
                    Faces = digest
                };
            }

            return new WorldFaceManifest
            {
                SchemaVersion = 1,
                CanonicalEncoding = CanonicalRenderFaceHasher.Encoding,
                Game = game,
                Seed = seed,
                FaceGenerationMode = faceGenerationMode,
                ChunkSizeX = GameManager.settings.chunkMaxX,
                ChunkSizeY = GameManager.settings.chunkMaxY,
                ChunkSizeZ = GameManager.settings.chunkMaxZ,
                Lod1Radius = GameManager.settings.lod1RenderDistance,
                ActiveChunkCount = chunks.Length,
                CaptureCenterChunkX = centerX,
                CaptureCenterChunkY = centerY,
                CaptureCenterChunkZ = centerZ,
                ActiveCoordinateSha256 = HashCoordinates(chunks),
                GameInputSha256 = HashGameInputs(),
                BlockRegistrySha256 = HashBlockRegistry(),
                Faces = CanonicalRenderFaceHasher.Hash(allFaces),
                Chunks = chunkManifests
            };
        }

        private static List<CanonicalRenderFace> CaptureChunkFaces(
            World world,
            WorldRenderChunk chunk,
            ChunkRenderUploadData data)
        {
            var result = new List<CanonicalRenderFace>(checked(
                data.OpaqueFaceCount + data.TransparentFaceCount));
            CapturePass(
                world,
                chunk,
                data.OpaqueFaceCount,
                data.OpaqueOffsets.Span,
                data.OpaqueTileIndices.Span,
                data.OpaqueFaceDirections.Span,
                CanonicalRenderPass.Opaque,
                result);
            CapturePass(
                world,
                chunk,
                data.TransparentFaceCount,
                data.TransparentOffsets.Span,
                data.TransparentTileIndices.Span,
                data.TransparentFaceDirections.Span,
                CanonicalRenderPass.Transparent,
                result);
            return result;
        }

        private static void CapturePass(
            World world,
            WorldRenderChunk chunk,
            int faceCount,
            ReadOnlySpan<byte> offsets,
            ReadOnlySpan<uint> tileIndices,
            ReadOnlySpan<byte> directions,
            CanonicalRenderPass renderPass,
            List<CanonicalRenderFace> destination)
        {
            if (offsets.Length != checked(faceCount * 3) ||
                tileIndices.Length != faceCount ||
                directions.Length != faceCount)
            {
                throw new InvalidOperationException(
                    $"Chunk ({chunk.ChunkX}, {chunk.ChunkY}, {chunk.ChunkZ}) has invalid upload arrays.");
            }

            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;
            int originX = checked(chunk.ChunkX * maxX);
            int originY = checked(chunk.ChunkY * maxY);
            int originZ = checked(chunk.ChunkZ * maxZ);

            for (int index = 0; index < faceCount; index++)
            {
                int localX = offsets[index * 3];
                int localY = offsets[index * 3 + 1];
                int localZ = offsets[index * 3 + 2];
                byte direction = directions[index];
                if ((uint)localX >= (uint)maxX ||
                    (uint)localY >= (uint)maxY ||
                    (uint)localZ >= (uint)maxZ ||
                    direction >= FaceNormals.Length)
                {
                    throw new InvalidOperationException(
                        $"Chunk ({chunk.ChunkX}, {chunk.ChunkY}, {chunk.ChunkZ}) has an invalid face.");
                }

                ushort blockId = chunk.GetBlockLocal(localX, localY, localZ);
                bool opaque = TerrainLoader.IsOpaque(blockId);
                if ((renderPass == CanonicalRenderPass.Opaque) != opaque)
                {
                    throw new InvalidOperationException(
                        $"Chunk ({chunk.ChunkX}, {chunk.ChunkY}, {chunk.ChunkZ}) has a face in the wrong pass.");
                }

                uint expectedTileIndex = GetExpectedTileIndex(blockId, direction);
                if (tileIndices[index] != expectedTileIndex)
                {
                    throw new InvalidOperationException(
                        $"Chunk ({chunk.ChunkX}, {chunk.ChunkY}, {chunk.ChunkZ}) has " +
                        $"tile {tileIndices[index]} for block {blockId} direction {direction}, " +
                        $"but the runtime texture atlas requires tile {expectedTileIndex}.");
                }

                int worldX = originX + localX;
                int worldY = originY + localY;
                int worldZ = originZ + localZ;
                (int dx, int dy, int dz) = FaceNormals[direction];
                ushort neighborBlockId = world.GetBlock(
                    worldX + dx,
                    worldY + dy,
                    worldZ + dz);
                destination.Add(new CanonicalRenderFace(
                    worldX,
                    worldY,
                    worldZ,
                    direction,
                    renderPass,
                    blockId,
                    neighborBlockId));
            }
        }

        private static uint GetExpectedTileIndex(ushort blockId, byte direction)
        {
            var atlas = ChunkRender.terrainTextureAtlas ??
                throw new InvalidOperationException("The runtime texture atlas is not initialized.");
            var coordinates = atlas.GetBlockUVs(blockId, (Faces)direction);
            if (coordinates.Count != 4)
                throw new InvalidOperationException("A runtime texture face must have four atlas coordinates.");
            byte minimumX = byte.MaxValue;
            byte minimumY = byte.MaxValue;
            for (int index = 0; index < coordinates.Count; index++)
            {
                if (coordinates[index].x < minimumX)
                    minimumX = coordinates[index].x;
                if (coordinates[index].y < minimumY)
                    minimumY = coordinates[index].y;
            }

            return checked((uint)(minimumY * atlas.tilesX + minimumX));
        }

        private static string HashCoordinates(
            IEnumerable<WorldRenderChunk> chunks)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            CanonicalRenderFaceHasher.AppendString(
                hash,
                "MVoxelEngine1.ActiveRenderCoordinates.v1");
            Span<byte> encoded = stackalloc byte[12];
            foreach (WorldRenderChunk chunk in chunks)
            {
                BinaryPrimitives.WriteInt32LittleEndian(encoded, chunk.ChunkX);
                BinaryPrimitives.WriteInt32LittleEndian(encoded[4..], chunk.ChunkY);
                BinaryPrimitives.WriteInt32LittleEndian(encoded[8..], chunk.ChunkZ);
                hash.AppendData(encoded);
            }

            return CanonicalRenderFaceHasher.GetHex(hash);
        }

        private static string HashBlockRegistry()
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            CanonicalRenderFaceHasher.AppendString(
                hash,
                "MVoxelEngine1.RuntimeBlockRegistry.v1");
            Span<byte> encoded = stackalloc byte[5];
            Span<byte> state = stackalloc byte[4];
            foreach (BlockType block in TerrainLoader.allBlockTypeObjects.OrderBy(block => block.ID))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(encoded, block.ID);
                BinaryPrimitives.WriteUInt16LittleEndian(encoded[2..], (ushort)block.BaseType);
                encoded[4] = block.IsTransparent ? (byte)1 : (byte)0;
                hash.AppendData(encoded);
                CanonicalRenderFaceHasher.AppendString(hash, block.UniqueName);
                CanonicalRenderFaceHasher.AppendString(hash, block.Name);
                CanonicalRenderFaceHasher.AppendString(hash, block.TextureFaceBase);
                CanonicalRenderFaceHasher.AppendString(hash, block.TextureFaceTop);
                CanonicalRenderFaceHasher.AppendString(hash, block.TextureFaceFront);
                CanonicalRenderFaceHasher.AppendString(hash, block.TextureFaceBack);
                CanonicalRenderFaceHasher.AppendString(hash, block.TextureFaceLeft);
                CanonicalRenderFaceHasher.AppendString(hash, block.TextureFaceRight);
                CanonicalRenderFaceHasher.AppendString(hash, block.TextureFaceBottom);
                BinaryPrimitives.WriteInt32LittleEndian(state, (int)block.StateOfMatter);
                hash.AppendData(state);
            }

            return CanonicalRenderFaceHasher.GetHex(hash);
        }

        private static string HashGameInputs()
        {
            string gameDirectory = GameManager.settings.loadedGameDirectory;
            string savesDirectory = Path.GetFullPath(
                GameManager.settings.savesWorldDirectory);
            string savesPrefix = savesDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? savesDirectory
                : savesDirectory + Path.DirectorySeparatorChar;
            string[] files = Directory.EnumerateFiles(
                    gameDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path =>
                    !path.Equals(savesDirectory, StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith(savesPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(
                    path => Path.GetRelativePath(gameDirectory, path),
                    StringComparer.Ordinal)
                .ToArray();

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            CanonicalRenderFaceHasher.AppendString(
                hash,
                "MVoxelEngine1.GameInputs.v1");
            byte[] buffer = new byte[128 * 1024];
            Span<byte> length = stackalloc byte[8];
            foreach (string path in files)
            {
                string relativePath = Path.GetRelativePath(gameDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                CanonicalRenderFaceHasher.AppendString(hash, relativePath);
                var info = new FileInfo(path);
                BinaryPrimitives.WriteInt64LittleEndian(length, info.Length);
                hash.AppendData(length);
                using FileStream stream = File.OpenRead(path);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                    hash.AppendData(buffer.AsSpan(0, read));
            }

            return CanonicalRenderFaceHasher.GetHex(hash);
        }
    }
}

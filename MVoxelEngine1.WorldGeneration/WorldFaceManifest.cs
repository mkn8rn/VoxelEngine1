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

            using var accumulator = new CanonicalFaceDigestAccumulator();
            accumulator.AppendSorted(faces);
            return accumulator.Complete();
        }

        public static CanonicalFaceSetDigest HashOrderedBatches(
            IEnumerable<IEnumerable<CanonicalRenderFace>> batches)
        {
            ArgumentNullException.ThrowIfNull(batches);
            using var accumulator = new CanonicalFaceDigestAccumulator();
            foreach (IEnumerable<CanonicalRenderFace> batch in batches)
            {
                ArgumentNullException.ThrowIfNull(batch);
                CanonicalRenderFace[] faces = batch.ToArray();
                Array.Sort(faces, Comparer);
                accumulator.AppendSorted(faces);
            }

            return accumulator.Complete();
        }

        internal static void Sort(List<CanonicalRenderFace> faces)
        {
            ArgumentNullException.ThrowIfNull(faces);
            faces.Sort(Comparer);
        }

        internal static CanonicalFaceSetDigest HashSorted(
            IReadOnlyList<CanonicalRenderFace> faces)
        {
            ArgumentNullException.ThrowIfNull(faces);
            using var accumulator = new CanonicalFaceDigestAccumulator();
            accumulator.AppendSorted(faces);
            return accumulator.Complete();
        }

        internal sealed class CanonicalFaceDigestAccumulator : IDisposable
        {
            private readonly IncrementalHash all = CreateHasher("all");
            private readonly IncrementalHash opaque = CreateHasher("opaque");
            private readonly IncrementalHash transparent = CreateHasher("transparent");
            private readonly IncrementalHash[] opaqueDirections = CreateDirectionHashers("opaque");
            private readonly IncrementalHash[] transparentDirections = CreateDirectionHashers("transparent");
            private readonly long[] opaqueCounts = new long[6];
            private readonly long[] transparentCounts = new long[6];
            private CanonicalRenderFace? previous;
            private long faceCount;
            private long opaqueCount;
            private long transparentCount;
            private bool completed;
            private const int BytesPerHashBuffer = 18 * 128;

            public void AppendSorted(IReadOnlyList<CanonicalRenderFace> faces)
            {
                if (completed)
                    throw new InvalidOperationException("The canonical face digest is complete.");
                if (faces.Count == 0)
                    return;

                Span<byte> encoded = stackalloc byte[18];
                Span<byte> buffers = stackalloc byte[15 * BytesPerHashBuffer];
                Span<int> lengths = stackalloc int[15];
                lengths.Clear();
                for (int index = 0; index < faces.Count; index++)
                {
                    CanonicalRenderFace face = faces[index];
                    Validate(face);
                    if (previous.HasValue)
                    {
                        int comparison = CompareFaces(previous.Value, face);
                        if (comparison == 0)
                            throw new InvalidOperationException($"Duplicate canonical face: {face}.");
                        if (comparison > 0)
                        {
                            throw new InvalidOperationException(
                                "Canonical face batches are not in coordinate order.");
                        }
                    }

                    Encode(face, encoded);
                    AppendBuffered(0, buffers, lengths, encoded);
                    if (face.RenderPass == CanonicalRenderPass.Opaque)
                    {
                        AppendBuffered(1, buffers, lengths, encoded);
                        AppendBuffered(3 + face.Direction, buffers, lengths, encoded);
                        opaqueCounts[face.Direction]++;
                        opaqueCount++;
                    }
                    else
                    {
                        AppendBuffered(2, buffers, lengths, encoded);
                        AppendBuffered(9 + face.Direction, buffers, lengths, encoded);
                        transparentCounts[face.Direction]++;
                        transparentCount++;
                    }

                    faceCount++;
                    previous = face;
                }
                for (int i = 0; i < lengths.Length; i++)
                    if (lengths[i] != 0)
                        GetHasher(i).AppendData(buffers.Slice(i * BytesPerHashBuffer, lengths[i]));
            }

            private void AppendBuffered(int index, Span<byte> buffers, Span<int> lengths, ReadOnlySpan<byte> encoded)
            {
                int length = lengths[index];
                Span<byte> buffer = buffers.Slice(index * BytesPerHashBuffer, BytesPerHashBuffer);
                encoded.CopyTo(buffer.Slice(length));
                length += encoded.Length;
                if (length == BytesPerHashBuffer)
                {
                    GetHasher(index).AppendData(buffer);
                    length = 0;
                }
                lengths[index] = length;
            }

            private IncrementalHash GetHasher(int index) => index switch
            {
                0 => all,
                1 => opaque,
                2 => transparent,
                < 9 => opaqueDirections[index - 3],
                _ => transparentDirections[index - 9]
            };

            public CanonicalFaceSetDigest Complete()
            {
                if (completed)
                    throw new InvalidOperationException("The canonical face digest is complete.");

                completed = true;
                return new CanonicalFaceSetDigest
                {
                    FaceCount = faceCount,
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

            public void Dispose()
            {
                all.Dispose();
                opaque.Dispose();
                transparent.Dispose();
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
            if (face.RenderPass is not (CanonicalRenderPass.Opaque or CanonicalRenderPass.Transparent))
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
        private readonly record struct ManifestChunk(int Index, int X, int Y, int Z);

        public static WorldFaceManifest Capture(
            NativeWorld world, string game, int seed, FaceGenerationMode faceGenerationMode)
        {
            ArgumentNullException.ThrowIfNull(world);
            ArgumentException.ThrowIfNullOrWhiteSpace(game);
            if (faceGenerationMode is not (FaceGenerationMode.Reference or FaceGenerationMode.Optimized))
                throw new ArgumentOutOfRangeException(nameof(faceGenerationMode));
            (int centerX, int centerY, int centerZ) = world.PlayerChunkPosition;
            WorldFaceManifest? manifest = null;
            world.InspectState(owner =>
            {
                var view = new NativeGtrtSessionView(owner.AsSpan());
                var reference = new NativeReferenceFaceGenerator(ref view);
                var chunks = new List<ManifestChunk>();
                for (int index = 0; index < view.Chunks.Length; index++)
                {
                    if (!view.TryInspectRetiredPacket(index, out _))
                        continue;
                    NativeChunkRecord chunk = view.Chunks[index];
                    chunks.Add(new ManifestChunk(index, chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ));
                }
                chunks.Sort(static (a, b) =>
                {
                    int comparison = a.X.CompareTo(b.X);
                    if (comparison == 0) comparison = a.Y.CompareTo(b.Y);
                    return comparison == 0 ? a.Z.CompareTo(b.Z) : comparison;
                });
                var chunkManifests = new ChunkFaceManifest[chunks.Count];
                var expectedTiles = new Dictionary<int, uint>();
                var slab = new List<CanonicalRenderFace>();
                using var all = new CanonicalRenderFaceHasher.CanonicalFaceDigestAccumulator();
                int? slabX = null;
                for (int i = 0; i < chunks.Count; i++)
                {
                    ManifestChunk chunk = chunks[i];
                    if (slabX.HasValue && slabX.Value != chunk.X)
                        AppendSlab(all, slab);
                    slabX = chunk.X;
                    List<CanonicalRenderFace> faces;
                    if (faceGenerationMode == FaceGenerationMode.Reference)
                        faces = reference.Generate(ref view, chunk.Index);
                    else
                    {
                        if (!view.TryInspectRetiredPacket(chunk.Index, out NativePacketReadView packet))
                            throw new InvalidDataException("Native render packet changed during capture.");
                        faces = new List<CanonicalRenderFace>(checked(
                            packet.Record.OpaqueFaceCount + packet.Record.TransparentFaceCount));
                        CapturePass(ref view, reference, chunk.Index, packet.Record.OpaqueFaceCount,
                            packet.OpaqueWords, CanonicalRenderPass.Opaque, faces, expectedTiles);
                        CapturePass(ref view, reference, chunk.Index, packet.Record.TransparentFaceCount,
                            packet.TransparentWords, CanonicalRenderPass.Transparent, faces, expectedTiles);
                    }
                    CanonicalRenderFaceHasher.Sort(faces);
                    CanonicalFaceSetDigest digest = CanonicalRenderFaceHasher.HashSorted(faces);
                    chunkManifests[i] = new ChunkFaceManifest
                    {
                        ChunkX = chunk.X, ChunkY = chunk.Y, ChunkZ = chunk.Z,
                        FullyOccluded = digest.FaceCount == 0, Faces = digest
                    };
                    slab.AddRange(faces);
                }
                AppendSlab(all, slab);
                manifest = new WorldFaceManifest
                {
                    SchemaVersion = 1, CanonicalEncoding = CanonicalRenderFaceHasher.Encoding,
                    Game = game, Seed = seed, FaceGenerationMode = faceGenerationMode,
                    ChunkSizeX = view.ChunkSizeX, ChunkSizeY = view.ChunkSizeY, ChunkSizeZ = view.ChunkSizeZ,
                    Lod1Radius = GameManager.settings.lod1RenderDistance, ActiveChunkCount = chunks.Count,
                    CaptureCenterChunkX = centerX, CaptureCenterChunkY = centerY, CaptureCenterChunkZ = centerZ,
                    ActiveCoordinateSha256 = HashCoordinates(chunks),
                    GameInputSha256 = RuntimeInputHasher.HashGameInputs(),
                    BlockRegistrySha256 = RuntimeInputHasher.HashBlockRegistry(),
                    Faces = all.Complete(), Chunks = chunkManifests
                };
            });
            return manifest ?? throw new InvalidOperationException("Native manifest capture did not complete.");
        }

        private static void AppendSlab(
            CanonicalRenderFaceHasher.CanonicalFaceDigestAccumulator accumulator,
            List<CanonicalRenderFace> faces)
        {
            CanonicalRenderFaceHasher.Sort(faces);
            accumulator.AppendSorted(faces);
            faces.Clear();
        }

        private static void CapturePass(ref NativeGtrtSessionView view, NativeReferenceFaceGenerator reference, int index, int faceCount,
            ReadOnlySpan<uint> rectangles, CanonicalRenderPass pass,
            List<CanonicalRenderFace> destination, Dictionary<int, uint> expectedTiles)
        {
            if (PackedFaceRectangle.CountLogicalFaces(rectangles) != faceCount)
                throw new InvalidDataException("Native packet face count does not match its rectangles.");
            NativeChunkRecord chunk = view.Chunks[index];
            var reader = new PackedFaceRectangleReader(rectangles);
            while (reader.MoveNext())
            {
                int x = reader.X, y = reader.Y, z = reader.Z;
                if ((uint)x >= (uint)view.ChunkSizeX || (uint)y >= (uint)view.ChunkSizeY ||
                    (uint)z >= (uint)view.ChunkSizeZ)
                    throw new InvalidDataException("Native packet face is outside its chunk.");
                ushort source = reference.GetBlock(ref view, index, x, y, z);
                (int dx, int dy, int dz) = NativeReferenceFaceGenerator.Normal(reader.Direction);
                ushort neighbor = reference.GetBlock(ref view, index, x + dx, y + dy, z + dz);
                if ((pass == CanonicalRenderPass.Opaque) != reference.IsOpaque(source) ||
                    !reference.Visible(source, neighbor))
                    throw new InvalidDataException("Native packet contains a hidden face or an incorrect render pass.");
                if (reader.TileIndex != GetExpectedTileIndex(source, reader.Direction, expectedTiles))
                    throw new InvalidDataException("Native packet texture differs from the loaded runtime texture.");
                destination.Add(new CanonicalRenderFace(
                    checked(chunk.ChunkX * view.ChunkSizeX + x),
                    checked(chunk.ChunkY * view.ChunkSizeY + y),
                    checked(chunk.ChunkZ * view.ChunkSizeZ + z), reader.Direction, pass, source, neighbor));
            }
        }

        private static uint GetExpectedTileIndex(ushort blockId, byte direction,
            Dictionary<int, uint> expectedTiles)
        {
            int key = (blockId << 3) | direction;
            if (expectedTiles.TryGetValue(key, out uint cached))
                return cached;
            var atlas = ChunkRender.terrainTextureAtlas ??
                throw new InvalidOperationException("Runtime texture atlas is not initialized.");
            var coordinates = atlas.GetBlockUVs(blockId, (Faces)direction);
            if (coordinates.Count != 4)
                throw new InvalidDataException("A runtime texture face must have four atlas coordinates.");
            byte minX = byte.MaxValue, minY = byte.MaxValue;
            foreach (var coordinate in coordinates)
            {
                minX = Math.Min(minX, coordinate.x);
                minY = Math.Min(minY, coordinate.y);
            }
            uint tile = checked((uint)(minY * atlas.tilesX + minX));
            expectedTiles.Add(key, tile);
            return tile;
        }

        private static string HashCoordinates(IEnumerable<ManifestChunk> chunks)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            CanonicalRenderFaceHasher.AppendString(hash, "MVoxelEngine1.ActiveRenderCoordinates.v1");
            Span<byte> encoded = stackalloc byte[12];
            foreach (ManifestChunk chunk in chunks)
            {
                BinaryPrimitives.WriteInt32LittleEndian(encoded, chunk.X);
                BinaryPrimitives.WriteInt32LittleEndian(encoded[4..], chunk.Y);
                BinaryPrimitives.WriteInt32LittleEndian(encoded[8..], chunk.Z);
                hash.AppendData(encoded);
            }
            return CanonicalRenderFaceHasher.GetHex(hash);
        }
    }
}

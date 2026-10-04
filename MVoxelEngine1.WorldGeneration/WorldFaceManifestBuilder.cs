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
    public static class WorldFaceManifestBuilder
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private readonly record struct ManifestChunk(int Index, int X, int Y, int Z);
        public static WorldFaceManifest Capture(NativeWorld world, string game, int seed, FaceGenerationMode faceGenerationMode)
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
                    if (comparison == 0)
                        comparison = a.Y.CompareTo(b.Y);
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
                        faces = new List<CanonicalRenderFace>(checked(packet.Record.OpaqueFaceCount + packet.Record.TransparentFaceCount));
                        CapturePass(ref view, reference, chunk.Index, packet.Record.OpaqueFaceCount, packet.OpaqueWords, CanonicalRenderPass.Opaque, faces, expectedTiles);
                        CapturePass(ref view, reference, chunk.Index, packet.Record.TransparentFaceCount, packet.TransparentWords, CanonicalRenderPass.Transparent, faces, expectedTiles);
                    }

                    CanonicalRenderFaceHasher.Sort(faces);
                    CanonicalFaceSetDigest digest = CanonicalRenderFaceHasher.HashSorted(faces);
                    chunkManifests[i] = new ChunkFaceManifest
                    {
                        ChunkX = chunk.X,
                        ChunkY = chunk.Y,
                        ChunkZ = chunk.Z,
                        FullyOccluded = digest.FaceCount == 0,
                        Faces = digest
                    };
                    slab.AddRange(faces);
                }

                AppendSlab(all, slab);
                manifest = new WorldFaceManifest
                {
                    SchemaVersion = 1,
                    CanonicalEncoding = CanonicalRenderFaceHasher.Encoding,
                    Game = game,
                    Seed = seed,
                    FaceGenerationMode = faceGenerationMode,
                    ChunkSizeX = view.ChunkSizeX,
                    ChunkSizeY = view.ChunkSizeY,
                    ChunkSizeZ = view.ChunkSizeZ,
                    Lod1Radius = GameManager.settings.lod1RenderDistance,
                    ActiveChunkCount = chunks.Count,
                    CaptureCenterChunkX = centerX,
                    CaptureCenterChunkY = centerY,
                    CaptureCenterChunkZ = centerZ,
                    ActiveCoordinateSha256 = HashCoordinates(chunks),
                    GameInputSha256 = RuntimeInputHasher.HashGameInputs(),
                    BlockRegistrySha256 = RuntimeInputHasher.HashBlockRegistry(),
                    Faces = all.Complete(),
                    Chunks = chunkManifests
                };
            });
            return manifest ?? throw new InvalidOperationException("Native manifest capture did not complete.");
        }

        private static void AppendSlab(CanonicalRenderFaceHasher.CanonicalFaceDigestAccumulator accumulator, List<CanonicalRenderFace> faces)
        {
            CanonicalRenderFaceHasher.Sort(faces);
            accumulator.AppendSorted(faces);
            faces.Clear();
        }

        private static void CapturePass(ref NativeGtrtSessionView view, NativeReferenceFaceGenerator reference, int index, int faceCount, ReadOnlySpan<uint> rectangles, CanonicalRenderPass pass, List<CanonicalRenderFace> destination, Dictionary<int, uint> expectedTiles)
        {
            if (PackedFaceRectangle.CountLogicalFaces(rectangles) != faceCount)
                throw new InvalidDataException("Native packet face count does not match its rectangles.");
            NativeChunkRecord chunk = view.Chunks[index];
            var reader = new PackedFaceRectangleReader(rectangles);
            while (reader.MoveNext())
            {
                int x = reader.X, y = reader.Y, z = reader.Z;
                if ((uint)x >= (uint)view.ChunkSizeX || (uint)y >= (uint)view.ChunkSizeY || (uint)z >= (uint)view.ChunkSizeZ)
                    throw new InvalidDataException("Native packet face is outside its chunk.");
                ushort source = reference.GetBlock(ref view, index, x, y, z);
                (int dx, int dy, int dz) = NativeReferenceFaceGenerator.Normal(reader.Direction);
                ushort neighbor = reference.GetBlock(ref view, index, x + dx, y + dy, z + dz);
                if ((pass == CanonicalRenderPass.Opaque) != reference.IsOpaque(source) || !reference.Visible(source, neighbor))
                    throw new InvalidDataException("Native packet contains a hidden face or an incorrect render pass.");
                if (reader.TileIndex != GetExpectedTileIndex(source, reader.Direction, expectedTiles))
                    throw new InvalidDataException("Native packet texture differs from the loaded runtime texture.");
                destination.Add(new CanonicalRenderFace(checked(chunk.ChunkX * view.ChunkSizeX + x), checked(chunk.ChunkY * view.ChunkSizeY + y), checked(chunk.ChunkZ * view.ChunkSizeZ + z), reader.Direction, pass, source, neighbor));
            }
        }

        private static uint GetExpectedTileIndex(ushort blockId, byte direction, Dictionary<int, uint> expectedTiles)
        {
            int key = (blockId << 3) | direction;
            if (expectedTiles.TryGetValue(key, out uint cached))
                return cached;
            var atlas = ChunkRender.terrainTextureAtlas ?? throw new InvalidOperationException("Runtime texture atlas is not initialized.");
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

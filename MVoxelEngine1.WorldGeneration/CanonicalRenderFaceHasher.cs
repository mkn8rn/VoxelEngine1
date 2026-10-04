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
    public static class CanonicalRenderFaceHasher
    {
        public const string Encoding = "chunk-major; face=(worldX:i32le,worldY:i32le,worldZ:i32le," + "direction:u8,pass:u8,blockId:u16le,neighborBlockId:u16le); sha256";
        private static readonly IComparer<CanonicalRenderFace> Comparer = Comparer<CanonicalRenderFace>.Create(CompareFaces);
        public static CanonicalFaceSetDigest Hash(IEnumerable<CanonicalRenderFace> source)
        {
            ArgumentNullException.ThrowIfNull(source);
            CanonicalRenderFace[] faces = source.ToArray();
            Array.Sort(faces, Comparer);
            using var accumulator = new CanonicalFaceDigestAccumulator();
            accumulator.AppendSorted(faces);
            return accumulator.Complete();
        }

        public static CanonicalFaceSetDigest HashOrderedBatches(IEnumerable<IEnumerable<CanonicalRenderFace>> batches)
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

        internal static CanonicalFaceSetDigest HashSorted(IReadOnlyList<CanonicalRenderFace> faces)
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
                            throw new InvalidOperationException("Canonical face batches are not in coordinate order.");
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
                    OpaqueDirections = FinishDirections(opaqueDirections, opaqueCounts),
                    TransparentDirections = FinishDirections(transparentDirections, transparentCounts)
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

        private static IReadOnlyList<FaceDirectionDigest> FinishDirections(IncrementalHash[] hashers, long[] counts)
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

        private static void Encode(CanonicalRenderFace face, Span<byte> destination)
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

        private static int CompareFaces(CanonicalRenderFace left, CanonicalRenderFace right)
        {
            int comparison = left.WorldX.CompareTo(right.WorldX);
            if (comparison != 0)
                return comparison;
            comparison = left.WorldY.CompareTo(right.WorldY);
            if (comparison != 0)
                return comparison;
            comparison = left.WorldZ.CompareTo(right.WorldZ);
            if (comparison != 0)
                return comparison;
            comparison = left.Direction.CompareTo(right.Direction);
            if (comparison != 0)
                return comparison;
            comparison = left.RenderPass.CompareTo(right.RenderPass);
            if (comparison != 0)
                return comparison;
            comparison = left.BlockId.CompareTo(right.BlockId);
            if (comparison != 0)
                return comparison;
            return left.NeighborBlockId.CompareTo(right.NeighborBlockId);
        }

        internal static void AppendString(IncrementalHash hash, string value)
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
}

using System.Runtime.CompilerServices;

namespace MVoxelEngine1.Graphics.Terrain
{
    public static class PackedFaceRectangle
    {
        public const int WordsPerRectangle = 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackPosition(
            int x,
            int y,
            int z,
            byte direction)
        {
            if ((uint)x > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(y));
            if ((uint)z > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(z));
            if (direction >= 6)
                throw new ArgumentOutOfRangeException(nameof(direction));

            return (uint)x |
                   ((uint)y << 8) |
                   ((uint)z << 16) |
                   ((uint)direction << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackAttributes(
            int extentU,
            int extentV,
            uint tileIndex)
        {
            if ((uint)(extentU - 1) > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(extentU));
            if ((uint)(extentV - 1) > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(extentV));
            // The runtime atlas uses byte UV coordinates, so its linear tile identity fits in 16 bits.
            if (tileIndex > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(tileIndex));

            return (uint)(extentU - 1) |
                   ((uint)(extentV - 1) << 8) |
                   (tileIndex << 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(
            Span<uint> destination,
            int wordIndex,
            int x,
            int y,
            int z,
            byte direction,
            int extentU,
            int extentV,
            uint tileIndex)
        {
            destination[wordIndex] = PackPosition(x, y, z, direction);
            destination[wordIndex + 1] = PackAttributes(
                extentU,
                extentV,
                tileIndex);
        }

        public static int GetRectangleCount(ReadOnlySpan<uint> words)
        {
            ValidateWordCount(words.Length);
            return words.Length / WordsPerRectangle;
        }

        public static long CountLogicalFaces(ReadOnlySpan<uint> words)
        {
            ValidateWordCount(words.Length);
            long count = 0;
            for (int index = 0; index < words.Length; index += WordsPerRectangle)
            {
                uint position = words[index];
                if ((position & 0xF8000000u) != 0 ||
                    ((position >> 24) & 0x07) >= 6)
                {
                    throw new InvalidDataException(
                        "Packed face rectangle position is invalid.");
                }
                uint attributes = words[index + 1];
                int extentU = (int)(attributes & byte.MaxValue) + 1;
                int extentV = (int)((attributes >> 8) & byte.MaxValue) + 1;
                count = checked(count + (long)extentU * extentV);
            }

            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DecodePosition(
            uint packed,
            out int x,
            out int y,
            out int z,
            out byte direction)
        {
            if ((packed & 0xF8000000u) != 0)
            {
                throw new InvalidDataException(
                    "Packed face position contains reserved bits.");
            }
            x = (byte)packed;
            y = (byte)(packed >> 8);
            z = (byte)(packed >> 16);
            direction = (byte)((packed >> 24) & 0x07);
            if (direction >= 6)
                throw new InvalidDataException(
                    $"Packed face direction {direction} is invalid.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DecodeAttributes(
            uint packed,
            out int extentU,
            out int extentV,
            out uint tileIndex)
        {
            extentU = (int)(packed & byte.MaxValue) + 1;
            extentV = (int)((packed >> 8) & byte.MaxValue) + 1;
            tileIndex = packed >> 16;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void GetVoxel(
            int anchorX,
            int anchorY,
            int anchorZ,
            byte direction,
            int u,
            int v,
            out int x,
            out int y,
            out int z)
        {
            switch (direction)
            {
                case 0:
                    x = anchorX;
                    y = anchorY + v;
                    z = anchorZ + u;
                    return;
                case 1:
                    x = anchorX;
                    y = anchorY + v;
                    z = anchorZ - u;
                    return;
                case 2:
                    x = anchorX + u;
                    y = anchorY;
                    z = anchorZ + v;
                    return;
                case 3:
                    x = anchorX + u;
                    y = anchorY;
                    z = anchorZ - v;
                    return;
                case 4:
                    x = anchorX - u;
                    y = anchorY + v;
                    z = anchorZ;
                    return;
                case 5:
                    x = anchorX + u;
                    y = anchorY + v;
                    z = anchorZ;
                    return;
                default:
                    throw new InvalidDataException(
                        $"Packed face direction {direction} is invalid.");
            }
        }

        private static void ValidateWordCount(int wordCount)
        {
            if (wordCount % WordsPerRectangle != 0)
            {
                throw new InvalidDataException(
                    "Packed face rectangle data has an invalid word count.");
            }
        }
    }

    public ref struct PackedFaceRectangleReader
    {
        private readonly ReadOnlySpan<uint> words;
        private int nextWordIndex;
        private int anchorX;
        private int anchorY;
        private int anchorZ;
        private int extentU;
        private int extentV;
        private int u;
        private int v;
        private bool hasRectangle;

        public PackedFaceRectangleReader(ReadOnlySpan<uint> words)
        {
            PackedFaceRectangle.GetRectangleCount(words);
            this.words = words;
            nextWordIndex = 0;
            anchorX = 0;
            anchorY = 0;
            anchorZ = 0;
            extentU = 0;
            extentV = 0;
            u = 0;
            v = 0;
            hasRectangle = false;
            X = 0;
            Y = 0;
            Z = 0;
            Direction = 0;
            TileIndex = 0;
        }

        public int X { get; private set; }

        public int Y { get; private set; }

        public int Z { get; private set; }

        public byte Direction { get; private set; }

        public uint TileIndex { get; private set; }

        public bool MoveNext()
        {
            if (hasRectangle)
            {
                u++;
                if (u >= extentU)
                {
                    u = 0;
                    v++;
                }

                if (v < extentV)
                {
                    SetCurrentVoxel();
                    return true;
                }
            }

            if (nextWordIndex >= words.Length)
                return false;

            PackedFaceRectangle.DecodePosition(
                words[nextWordIndex],
                out anchorX,
                out anchorY,
                out anchorZ,
                out byte direction);
            PackedFaceRectangle.DecodeAttributes(
                words[nextWordIndex + 1],
                out extentU,
                out extentV,
                out uint tileIndex);
            Direction = direction;
            TileIndex = tileIndex;
            nextWordIndex += PackedFaceRectangle.WordsPerRectangle;
            u = 0;
            v = 0;
            hasRectangle = true;
            SetCurrentVoxel();
            return true;
        }

        private void SetCurrentVoxel()
        {
            PackedFaceRectangle.GetVoxel(
                anchorX,
                anchorY,
                anchorZ,
                Direction,
                u,
                v,
                out int x,
                out int y,
                out int z);
            X = x;
            Y = y;
            Z = z;
        }
    }

    internal sealed class FaceRectangleMeshData
    {
        internal FaceRectangleMeshData(
            int opaqueFaceCount,
            uint[] opaqueRectangles,
            int transparentFaceCount,
            uint[] transparentRectangles)
        {
            OpaqueFaceCount = opaqueFaceCount;
            OpaqueRectangles = opaqueRectangles;
            TransparentFaceCount = transparentFaceCount;
            TransparentRectangles = transparentRectangles;
        }

        internal int OpaqueFaceCount { get; }

        internal uint[] OpaqueRectangles { get; }

        internal int OpaqueRectangleCount =>
            OpaqueRectangles.Length / PackedFaceRectangle.WordsPerRectangle;

        internal int TransparentFaceCount { get; }

        internal uint[] TransparentRectangles { get; }

        internal int TransparentRectangleCount =>
            TransparentRectangles.Length / PackedFaceRectangle.WordsPerRectangle;

        internal static FaceRectangleMeshData FromFaces(
            int opaqueFaceCount,
            byte[] opaqueOffsets,
            uint[] opaqueTileIndices,
            byte[] opaqueDirections,
            int transparentFaceCount,
            byte[] transparentOffsets,
            uint[] transparentTileIndices,
            byte[] transparentDirections)
        {
            return new FaceRectangleMeshData(
                opaqueFaceCount,
                PackFaces(
                    opaqueFaceCount,
                    opaqueOffsets,
                    opaqueTileIndices,
                    opaqueDirections),
                transparentFaceCount,
                PackFaces(
                    transparentFaceCount,
                    transparentOffsets,
                    transparentTileIndices,
                    transparentDirections));
        }

        private static uint[] PackFaces(
            int faceCount,
            byte[] offsets,
            uint[] tileIndices,
            byte[] directions)
        {
            if (offsets.Length != checked(faceCount * 3) ||
                tileIndices.Length != faceCount ||
                directions.Length != faceCount)
            {
                throw new InvalidDataException(
                    "Face arrays have inconsistent lengths.");
            }

            var result = new uint[checked(faceCount * PackedFaceRectangle.WordsPerRectangle)];
            for (int index = 0; index < faceCount; index++)
            {
                int offsetIndex = index * 3;
                PackedFaceRectangle.Write(
                    result,
                    index * PackedFaceRectangle.WordsPerRectangle,
                    offsets[offsetIndex],
                    offsets[offsetIndex + 1],
                    offsets[offsetIndex + 2],
                    directions[index],
                    1,
                    1,
                    tileIndices[index]);
            }

            return result;
        }
    }
}

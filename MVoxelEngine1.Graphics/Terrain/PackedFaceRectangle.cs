using System.Runtime.CompilerServices;

namespace MVoxelEngine1.Graphics.Terrain
{
    public static class PackedFaceRectangle
    {
        public const int WordsPerRectangle = 2;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackPosition(int x, int y, int z, byte direction)
        {
            if ((uint)x > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(y));
            if ((uint)z > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(z));
            if (direction >= 6)
                throw new ArgumentOutOfRangeException(nameof(direction));
            return (uint)x | ((uint)y << 8) | ((uint)z << 16) | ((uint)direction << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackAttributes(int extentU, int extentV, uint tileIndex)
        {
            if ((uint)(extentU - 1) > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(extentU));
            if ((uint)(extentV - 1) > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(extentV));
            // The runtime atlas uses byte UV coordinates, so its linear tile identity fits in 16 bits.
            if (tileIndex > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(tileIndex));
            return (uint)(extentU - 1) | ((uint)(extentV - 1) << 8) | (tileIndex << 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(Span<uint> destination, int wordIndex, int x, int y, int z, byte direction, int extentU, int extentV, uint tileIndex)
        {
            destination[wordIndex] = PackPosition(x, y, z, direction);
            destination[wordIndex + 1] = PackAttributes(extentU, extentV, tileIndex);
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
                if ((position & 0xF8000000u) != 0 || ((position >> 24) & 0x07) >= 6)
                {
                    throw new InvalidDataException("Packed face rectangle position is invalid.");
                }

                uint attributes = words[index + 1];
                int extentU = (int)(attributes & byte.MaxValue) + 1;
                int extentV = (int)((attributes >> 8) & byte.MaxValue) + 1;
                count = checked(count + (long)extentU * extentV);
            }

            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DecodePosition(uint packed, out int x, out int y, out int z, out byte direction)
        {
            if ((packed & 0xF8000000u) != 0)
            {
                throw new InvalidDataException("Packed face position contains reserved bits.");
            }

            x = (byte)packed;
            y = (byte)(packed >> 8);
            z = (byte)(packed >> 16);
            direction = (byte)((packed >> 24) & 0x07);
            if (direction >= 6)
                throw new InvalidDataException($"Packed face direction {direction} is invalid.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DecodeAttributes(uint packed, out int extentU, out int extentV, out uint tileIndex)
        {
            extentU = (int)(packed & byte.MaxValue) + 1;
            extentV = (int)((packed >> 8) & byte.MaxValue) + 1;
            tileIndex = packed >> 16;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void GetVoxel(int anchorX, int anchorY, int anchorZ, byte direction, int u, int v, out int x, out int y, out int z)
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
                    throw new InvalidDataException($"Packed face direction {direction} is invalid.");
            }
        }

        private static void ValidateWordCount(int wordCount)
        {
            if (wordCount % WordsPerRectangle != 0)
            {
                throw new InvalidDataException("Packed face rectangle data has an invalid word count.");
            }
        }
    }
}

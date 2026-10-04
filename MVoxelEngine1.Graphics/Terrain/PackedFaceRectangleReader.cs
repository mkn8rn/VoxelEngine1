using System.Runtime.CompilerServices;

namespace MVoxelEngine1.Graphics.Terrain
{
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
            PackedFaceRectangle.DecodePosition(words[nextWordIndex], out anchorX, out anchorY, out anchorZ, out byte direction);
            PackedFaceRectangle.DecodeAttributes(words[nextWordIndex + 1], out extentU, out extentV, out uint tileIndex);
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
            PackedFaceRectangle.GetVoxel(anchorX, anchorY, anchorZ, Direction, u, v, out int x, out int y, out int z);
            X = x;
            Y = y;
            Z = z;
        }
    }
}

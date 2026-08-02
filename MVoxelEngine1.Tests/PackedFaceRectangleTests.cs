using MVoxelEngine1.Graphics.Terrain;

namespace MVoxelEngine1.Tests
{
    public class PackedFaceRectangleTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void ReaderExpandsEachShaderOrientation(byte direction)
        {
            var words = new uint[PackedFaceRectangle.WordsPerRectangle];
            PackedFaceRectangle.Write(
                words,
                0,
                10,
                20,
                30,
                direction,
                3,
                2,
                47);

            var reader = new PackedFaceRectangleReader(words);
            var actual = new List<(int x, int y, int z, byte direction, uint tile)>();
            while (reader.MoveNext())
            {
                actual.Add((
                    reader.X,
                    reader.Y,
                    reader.Z,
                    reader.Direction,
                    reader.TileIndex));
            }

            Assert.Equal(6, actual.Count);
            int index = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int u = 0; u < 3; u++)
                {
                    (int x, int y, int z) = GetExpectedVoxel(
                        direction,
                        u,
                        v);
                    Assert.Equal(
                        (x, y, z, direction, 47u),
                        actual[index++]);
                }
            }
        }

        [Fact]
        public void LogicalCountUsesBothRectangleExtents()
        {
            var words = new uint[4];
            PackedFaceRectangle.Write(
                words,
                0,
                1,
                2,
                3,
                5,
                4,
                7,
                11);
            PackedFaceRectangle.Write(
                words,
                2,
                4,
                5,
                6,
                2,
                2,
                3,
                12);

            Assert.Equal(2, PackedFaceRectangle.GetRectangleCount(words));
            Assert.Equal(34, PackedFaceRectangle.CountLogicalFaces(words));
        }

        [Fact]
        public void InvalidPackedValuesFailClosed()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PackedFaceRectangle.PackPosition(256, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PackedFaceRectangle.PackPosition(0, 0, 0, 6));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PackedFaceRectangle.PackAttributes(0, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PackedFaceRectangle.PackAttributes(1, 257, 0));
            Assert.Throws<InvalidDataException>(() =>
                PackedFaceRectangle.GetRectangleCount(new uint[1]));
            Assert.Throws<InvalidDataException>(() =>
                PackedFaceRectangle.CountLogicalFaces(
                    new uint[] { 0x06000000, 0 }));
            Assert.Throws<InvalidDataException>(() =>
                PackedFaceRectangle.CountLogicalFaces(
                    new uint[] { 0x08000000, 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PackedFaceRectangle.PackAttributes(1, 1, 65_536));
        }

        [Fact]
        public void PackedAttributesPreserveMaximumEncodingValues()
        {
            var words = new uint[PackedFaceRectangle.WordsPerRectangle];
            PackedFaceRectangle.Write(
                words,
                0,
                0,
                0,
                255,
                5,
                256,
                256,
                65_535);

            Assert.Equal(0xFFFF_FFFFu, words[1]);
            var reader = new PackedFaceRectangleReader(words);
            Assert.True(reader.MoveNext());
            Assert.Equal(0, reader.X);
            Assert.Equal(0, reader.Y);
            Assert.Equal(255, reader.Z);
            Assert.Equal((byte)5, reader.Direction);
            Assert.Equal(65_535u, reader.TileIndex);
            int expandedFaceCount = 1;
            while (reader.MoveNext())
                expandedFaceCount++;
            Assert.Equal(65_536, expandedFaceCount);
            Assert.Equal(255, reader.X);
            Assert.Equal(255, reader.Y);
            Assert.Equal(255, reader.Z);
            Assert.Equal(65_536, PackedFaceRectangle.CountLogicalFaces(words));
        }

        private static (int x, int y, int z) GetExpectedVoxel(
            byte direction,
            int u,
            int v) => direction switch
            {
                0 => (10, 20 + v, 30 + u),
                1 => (10, 20 + v, 30 - u),
                2 => (10 + u, 20, 30 + v),
                3 => (10 + u, 20, 30 - v),
                4 => (10 - u, 20 + v, 30),
                5 => (10 + u, 20 + v, 30),
                _ => throw new ArgumentOutOfRangeException(nameof(direction))
            };
    }
}

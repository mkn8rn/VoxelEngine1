using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Flags;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Tests
{
    public class ReferenceFaceGeneratorTests
    {
        private const ushort Air = 0;
        private const ushort Stone = 1;
        private const ushort Water = 2;
        private const ushort Glass = 3;

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)1)]
        [InlineData((byte)2)]
        [InlineData((byte)3)]
        [InlineData((byte)4)]
        [InlineData((byte)5)]
        public void EqualWaterAcrossChunkBoundaryHasNoSharedFace(byte direction)
        {
            ReferenceFaceGenerationResult first = GenerateSingle(
                Water,
                CreateSingleNeighbor(direction, Water));
            byte opposite = (byte)(direction ^ 1);
            ReferenceFaceGenerationResult second = GenerateSingle(
                Water,
                CreateSingleNeighbor(opposite, Water));

            Assert.Equal(10, first.TransparentFaceCount + second.TransparentFaceCount);
            Assert.DoesNotContain(direction, first.TransparentDirections);
            Assert.DoesNotContain(opposite, second.TransparentDirections);
            Assert.Empty(first.OpaqueDirections);
            Assert.Empty(second.OpaqueDirections);
        }

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)1)]
        [InlineData((byte)2)]
        [InlineData((byte)3)]
        [InlineData((byte)4)]
        [InlineData((byte)5)]
        public void ReadsExactNeighborCoordinateForEachDirection(byte direction)
        {
            const int maxX = 2;
            const int maxY = 3;
            const int maxZ = 4;
            int x = direction == 0 ? 0 : maxX - 1;
            int y = direction == 2 ? 0 : direction == 3 ? maxY - 1 : 1;
            int z = direction == 4 ? 0 : direction == 5 ? maxZ - 1 : 2;
            ReferenceNeighborBlockPlanes neighbors = CreateCoordinateNeighbor(
                direction,
                x,
                y,
                z,
                maxX,
                maxY,
                maxZ);

            ReferenceFaceGenerationResult result = ReferenceFaceGenerator.Generate(
                maxX,
                maxY,
                maxZ,
                (localX, localY, localZ) =>
                    localX == x && localY == y && localZ == z ? Water : Air,
                neighbors,
                IsOpaque);

            Assert.Equal(5, result.TransparentFaceCount);
            Assert.DoesNotContain(direction, result.TransparentDirections);
            for (int index = 0; index < result.TransparentFaceCount; index++)
            {
                Assert.Equal(x, result.TransparentOffsets[index * 3]);
                Assert.Equal(y, result.TransparentOffsets[index * 3 + 1]);
                Assert.Equal(z, result.TransparentOffsets[index * 3 + 2]);
            }
        }

        [Fact]
        public void OpaqueVoxelWithSixOpaqueNeighborsHasNoFaces()
        {
            var neighbors = new ReferenceNeighborBlockPlanes(
                new[] { Stone },
                new[] { Stone },
                new[] { Stone },
                new[] { Stone },
                new[] { Stone },
                new[] { Stone });

            ReferenceFaceGenerationResult result = GenerateSingle(Stone, neighbors);

            Assert.Empty(result.OpaqueDirections);
            Assert.Empty(result.TransparentDirections);
        }

        [Theory]
        [InlineData(Stone, Water, 6, 0)]
        [InlineData(Water, Stone, 0, 5)]
        [InlineData(Water, Water, 0, 5)]
        [InlineData(Water, Glass, 0, 6)]
        [InlineData(Water, Air, 0, 6)]
        public void AppliesOpaqueAndTransparentVisibilityRules(
            ushort source,
            ushort neighbor,
            int expectedOpaqueFaces,
            int expectedTransparentFaces)
        {
            ReferenceFaceGenerationResult result = GenerateSingle(
                source,
                CreateSingleNeighbor(0, neighbor));

            Assert.Equal(expectedOpaqueFaces, result.OpaqueFaceCount);
            Assert.Equal(expectedTransparentFaces, result.TransparentFaceCount);
        }

        [Fact]
        public void UniformShortcutMatchesDirectVoxelScan()
        {
            const int maxX = 2;
            const int maxY = 3;
            const int maxZ = 2;
            var neighbors = new ReferenceNeighborBlockPlanes(
                Fill(maxY * maxZ, Stone),
                Fill(maxY * maxZ, Water),
                Fill(maxX * maxZ, Air),
                Fill(maxX * maxZ, Stone),
                Fill(maxX * maxY, Glass),
                Fill(maxX * maxY, Stone));

            ReferenceFaceGenerationResult direct = ReferenceFaceGenerator.Generate(
                maxX,
                maxY,
                maxZ,
                static (_, _, _) => Stone,
                neighbors,
                IsOpaque);
            ReferenceFaceGenerationResult uniform = ReferenceFaceGenerator.GenerateUniform(
                maxX,
                maxY,
                maxZ,
                Stone,
                neighbors,
                IsOpaque);

            Assert.Equal(GetOpaqueRecords(direct), GetOpaqueRecords(uniform));
            Assert.Empty(direct.TransparentDirections);
            Assert.Empty(uniform.TransparentDirections);
        }

        [Fact]
        public void UniformWaterCullsEqualWaterChunkSide()
        {
            const int size = 2;
            var neighbors = new ReferenceNeighborBlockPlanes(
                positiveX: Fill(size * size, Water));

            ReferenceFaceGenerationResult result = ReferenceFaceGenerator.GenerateUniform(
                size,
                size,
                size,
                Water,
                neighbors,
                IsOpaque);

            Assert.Equal(20, result.TransparentFaceCount);
            Assert.DoesNotContain((byte)1, result.TransparentDirections);
            Assert.Empty(result.OpaqueDirections);
        }

        [Fact]
        public void UniformOpaqueChunkWithSixOpaqueNeighborsHasNoFaces()
        {
            const int size = 2;
            var neighbors = new ReferenceNeighborBlockPlanes(
                Fill(size * size, Stone),
                Fill(size * size, Stone),
                Fill(size * size, Stone),
                Fill(size * size, Stone),
                Fill(size * size, Stone),
                Fill(size * size, Stone));

            ReferenceFaceGenerationResult result = ReferenceFaceGenerator.GenerateUniform(
                size,
                size,
                size,
                Stone,
                neighbors,
                IsOpaque);

            Assert.Empty(result.OpaqueDirections);
            Assert.Empty(result.TransparentDirections);
        }

        [Fact]
        public void RejectsNeighborPlaneWithWrongLength()
        {
            var neighbors = new ReferenceNeighborBlockPlanes(negativeX: new ushort[2]);

            Assert.Throws<ArgumentException>(() => ReferenceFaceGenerator.Generate(
                1,
                1,
                1,
                static (_, _, _) => Stone,
                neighbors,
                IsOpaque));
        }

        [Fact]
        public void ParsesReferenceFaceGenerationMode()
        {
            ConsoleFlags.Parse(new[] { "--faceGenerationMode", "Reference" });

            Assert.Equal(
                FaceGenerationMode.Reference,
                ConsoleFlags.consoleFlags.faceGenerationMode);
        }

        private static ReferenceFaceGenerationResult GenerateSingle(
            ushort source,
            ReferenceNeighborBlockPlanes neighbors)
        {
            return ReferenceFaceGenerator.Generate(
                1,
                1,
                1,
                (_, _, _) => source,
                neighbors,
                IsOpaque);
        }

        private static ReferenceNeighborBlockPlanes CreateSingleNeighbor(
            byte direction,
            ushort blockId)
        {
            ushort[]? negativeX = null;
            ushort[]? positiveX = null;
            ushort[]? negativeY = null;
            ushort[]? positiveY = null;
            ushort[]? negativeZ = null;
            ushort[]? positiveZ = null;
            ushort[] plane = { blockId };

            switch (direction)
            {
                case 0: negativeX = plane; break;
                case 1: positiveX = plane; break;
                case 2: negativeY = plane; break;
                case 3: positiveY = plane; break;
                case 4: negativeZ = plane; break;
                case 5: positiveZ = plane; break;
                default: throw new ArgumentOutOfRangeException(nameof(direction));
            }

            return new ReferenceNeighborBlockPlanes(
                negativeX,
                positiveX,
                negativeY,
                positiveY,
                negativeZ,
                positiveZ);
        }

        private static ReferenceNeighborBlockPlanes CreateCoordinateNeighbor(
            byte direction,
            int x,
            int y,
            int z,
            int maxX,
            int maxY,
            int maxZ)
        {
            ushort[]? negativeX = null;
            ushort[]? positiveX = null;
            ushort[]? negativeY = null;
            ushort[]? positiveY = null;
            ushort[]? negativeZ = null;
            ushort[]? positiveZ = null;

            switch (direction)
            {
                case 0:
                    negativeX = new ushort[maxY * maxZ];
                    negativeX[z * maxY + y] = Water;
                    break;
                case 1:
                    positiveX = new ushort[maxY * maxZ];
                    positiveX[z * maxY + y] = Water;
                    break;
                case 2:
                    negativeY = new ushort[maxX * maxZ];
                    negativeY[x * maxZ + z] = Water;
                    break;
                case 3:
                    positiveY = new ushort[maxX * maxZ];
                    positiveY[x * maxZ + z] = Water;
                    break;
                case 4:
                    negativeZ = new ushort[maxX * maxY];
                    negativeZ[x * maxY + y] = Water;
                    break;
                case 5:
                    positiveZ = new ushort[maxX * maxY];
                    positiveZ[x * maxY + y] = Water;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction));
            }

            return new ReferenceNeighborBlockPlanes(
                negativeX,
                positiveX,
                negativeY,
                positiveY,
                negativeZ,
                positiveZ);
        }

        private static bool IsOpaque(ushort blockId) => blockId == Stone;

        private static ushort[] Fill(int length, ushort blockId)
        {
            var result = new ushort[length];
            Array.Fill(result, blockId);
            return result;
        }

        private static string[] GetOpaqueRecords(ReferenceFaceGenerationResult result)
        {
            var records = new string[result.OpaqueFaceCount];
            for (int index = 0; index < records.Length; index++)
            {
                records[index] = string.Join(
                    ',',
                    result.OpaqueOffsets[index * 3],
                    result.OpaqueOffsets[index * 3 + 1],
                    result.OpaqueOffsets[index * 3 + 2],
                    result.OpaqueBlockIds[index],
                    result.OpaqueDirections[index]);
            }

            Array.Sort(records, StringComparer.Ordinal);
            return records;
        }
    }
}

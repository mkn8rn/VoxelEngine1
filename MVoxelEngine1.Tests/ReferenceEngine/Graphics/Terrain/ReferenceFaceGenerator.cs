using System;
using System.Collections.Generic;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.Graphics.Terrain
{
    internal static class ReferenceFaceGenerator
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
        public static ReferenceFaceGenerationResult Empty() => new(Array.Empty<byte>(), Array.Empty<ushort>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<ushort>(), Array.Empty<byte>());
        public static ReferenceFaceGenerationResult Generate(int maxX, int maxY, int maxZ, Func<int, int, int, ushort> getLocalBlock, ReferenceNeighborBlockPlanes neighbors, Func<ushort, bool> isOpaque)
        {
            ValidateInputs(maxX, maxY, maxZ, getLocalBlock, neighbors, isOpaque);
            var output = new FaceAccumulator();
            for (int x = 0; x < maxX; x++)
            {
                for (int y = 0; y < maxY; y++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        ushort blockId = getLocalBlock(x, y, z);
                        if (blockId == 0)
                            continue;
                        for (byte direction = 0; direction < FaceNormals.Length; direction++)
                        {
                            (int dx, int dy, int dz) = FaceNormals[direction];
                            int neighborX = x + dx;
                            int neighborY = y + dy;
                            int neighborZ = z + dz;
                            ushort neighborId = IsInside(neighborX, neighborY, neighborZ, maxX, maxY, maxZ) ? getLocalBlock(neighborX, neighborY, neighborZ) : neighbors.GetBlock(direction, x, y, z, maxY, maxZ);
                            output.EmitIfVisible(blockId, neighborId, direction, x, y, z, isOpaque);
                        }
                    }
                }
            }

            return output.ToResult();
        }

        public static ReferenceFaceGenerationResult GenerateUniform(int maxX, int maxY, int maxZ, ushort blockId, ReferenceNeighborBlockPlanes neighbors, Func<ushort, bool> isOpaque)
        {
            ValidateInputs(maxX, maxY, maxZ, static (_, _, _) => 0, neighbors, isOpaque);
            if (blockId == 0)
                throw new ArgumentOutOfRangeException(nameof(blockId));
            var output = new FaceAccumulator();
            for (int y = 0; y < maxY; y++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    output.EmitBoundary(blockId, 0, 0, y, z, neighbors, maxY, maxZ, isOpaque);
                    output.EmitBoundary(blockId, 1, maxX - 1, y, z, neighbors, maxY, maxZ, isOpaque);
                }
            }

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    output.EmitBoundary(blockId, 2, x, 0, z, neighbors, maxY, maxZ, isOpaque);
                    output.EmitBoundary(blockId, 3, x, maxY - 1, z, neighbors, maxY, maxZ, isOpaque);
                }
            }

            for (int x = 0; x < maxX; x++)
            {
                for (int y = 0; y < maxY; y++)
                {
                    output.EmitBoundary(blockId, 4, x, y, 0, neighbors, maxY, maxZ, isOpaque);
                    output.EmitBoundary(blockId, 5, x, y, maxZ - 1, neighbors, maxY, maxZ, isOpaque);
                }
            }

            return output.ToResult();
        }

        public static ReferenceFaceGenerationResult GenerateSections(int maxX, int maxY, int maxZ, Func<int, int, int, ushort> getLocalBlock, ReferenceNeighborBlockPlanes neighbors, Func<ushort, bool> isOpaque, IReadOnlyList<SectionPrerenderDesc> sections)
        {
            ValidateInputs(maxX, maxY, maxZ, getLocalBlock, neighbors, isOpaque);
            ArgumentNullException.ThrowIfNull(sections);
            var output = new FaceAccumulator();
            foreach (SectionPrerenderDesc section in sections)
            {
                var kind = (Section.RepresentationKind)section.Kind;
                if (kind == Section.RepresentationKind.Empty)
                    continue;
                ValidateSectionBounds(section, maxX, maxY, maxZ);
                if (kind == Section.RepresentationKind.Uniform)
                {
                    if (section.UniformBlockId != Section.AIR)
                    {
                        GenerateUniformSection(section, getLocalBlock, neighbors, isOpaque, maxX, maxY, maxZ, output);
                    }

                    continue;
                }

                GenerateVoxelSection(section, getLocalBlock, neighbors, isOpaque, maxX, maxY, maxZ, output);
            }

            return output.ToResult();
        }

        private static void GenerateUniformSection(SectionPrerenderDesc section, Func<int, int, int, ushort> getLocalBlock, ReferenceNeighborBlockPlanes neighbors, Func<ushort, bool> isOpaque, int maxX, int maxY, int maxZ, FaceAccumulator output)
        {
            int startX = section.SectionBaseX;
            int startY = section.SectionBaseY;
            int startZ = section.SectionBaseZ;
            int endX = startX + Section.SECTION_SIZE - 1;
            int endY = startY + Section.SECTION_SIZE - 1;
            int endZ = startZ + Section.SECTION_SIZE - 1;
            ushort blockId = section.UniformBlockId;
            for (int y = startY; y <= endY; y++)
            {
                for (int z = startZ; z <= endZ; z++)
                {
                    EmitFace(blockId, 0, startX, y, z);
                    EmitFace(blockId, 1, endX, y, z);
                }
            }

            for (int x = startX; x <= endX; x++)
            {
                for (int z = startZ; z <= endZ; z++)
                {
                    EmitFace(blockId, 2, x, startY, z);
                    EmitFace(blockId, 3, x, endY, z);
                }
            }

            for (int x = startX; x <= endX; x++)
            {
                for (int y = startY; y <= endY; y++)
                {
                    EmitFace(blockId, 4, x, y, startZ);
                    EmitFace(blockId, 5, x, y, endZ);
                }
            }

            void EmitFace(ushort source, byte direction, int x, int y, int z)
            {
                ushort neighbor = GetNeighborBlock(direction, x, y, z, maxX, maxY, maxZ, getLocalBlock, neighbors);
                output.EmitIfVisible(source, neighbor, direction, x, y, z, isOpaque);
            }
        }

        private static void GenerateVoxelSection(SectionPrerenderDesc section, Func<int, int, int, ushort> getLocalBlock, ReferenceNeighborBlockPlanes neighbors, Func<ushort, bool> isOpaque, int maxX, int maxY, int maxZ, FaceAccumulator output)
        {
            int endX = section.SectionBaseX + Section.SECTION_SIZE;
            int endY = section.SectionBaseY + Section.SECTION_SIZE;
            int endZ = section.SectionBaseZ + Section.SECTION_SIZE;
            for (int x = section.SectionBaseX; x < endX; x++)
            {
                for (int y = section.SectionBaseY; y < endY; y++)
                {
                    for (int z = section.SectionBaseZ; z < endZ; z++)
                    {
                        ushort blockId = getLocalBlock(x, y, z);
                        if (blockId == Section.AIR)
                            continue;
                        for (byte direction = 0; direction < FaceNormals.Length; direction++)
                        {
                            ushort neighbor = GetNeighborBlock(direction, x, y, z, maxX, maxY, maxZ, getLocalBlock, neighbors);
                            output.EmitIfVisible(blockId, neighbor, direction, x, y, z, isOpaque);
                        }
                    }
                }
            }
        }

        private static ushort GetNeighborBlock(byte direction, int x, int y, int z, int maxX, int maxY, int maxZ, Func<int, int, int, ushort> getLocalBlock, ReferenceNeighborBlockPlanes neighbors)
        {
            (int dx, int dy, int dz) = FaceNormals[direction];
            int neighborX = x + dx;
            int neighborY = y + dy;
            int neighborZ = z + dz;
            return IsInside(neighborX, neighborY, neighborZ, maxX, maxY, maxZ) ? getLocalBlock(neighborX, neighborY, neighborZ) : neighbors.GetBlock(direction, x, y, z, maxY, maxZ);
        }

        private static void ValidateSectionBounds(SectionPrerenderDesc section, int maxX, int maxY, int maxZ)
        {
            int size = Section.SECTION_SIZE;
            if (section.SectionBaseX < 0 || section.SectionBaseY < 0 || section.SectionBaseZ < 0 || section.SectionBaseX + size > maxX || section.SectionBaseY + size > maxY || section.SectionBaseZ + size > maxZ || section.SectionBaseX % size != 0 || section.SectionBaseY % size != 0 || section.SectionBaseZ % size != 0)
            {
                throw new ArgumentException("A Reference section is outside the chunk.", nameof(section));
            }
        }

        private static bool IsInside(int x, int y, int z, int maxX, int maxY, int maxZ)
        {
            return (uint)x < (uint)maxX && (uint)y < (uint)maxY && (uint)z < (uint)maxZ;
        }

        private static void ValidateInputs(int maxX, int maxY, int maxZ, Func<int, int, int, ushort> getLocalBlock, ReferenceNeighborBlockPlanes neighbors, Func<ushort, bool> isOpaque)
        {
            if (maxX <= 0 || maxX > 256)
                throw new ArgumentOutOfRangeException(nameof(maxX));
            if (maxY <= 0 || maxY > 256)
                throw new ArgumentOutOfRangeException(nameof(maxY));
            if (maxZ <= 0 || maxZ > 256)
                throw new ArgumentOutOfRangeException(nameof(maxZ));
            ArgumentNullException.ThrowIfNull(getLocalBlock);
            ArgumentNullException.ThrowIfNull(neighbors);
            ArgumentNullException.ThrowIfNull(isOpaque);
            neighbors.Validate(maxX, maxY, maxZ);
        }

        private sealed class FaceAccumulator
        {
            private readonly List<byte> opaqueOffsets = new();
            private readonly List<ushort> opaqueBlockIds = new();
            private readonly List<byte> opaqueDirections = new();
            private readonly List<byte> transparentOffsets = new();
            private readonly List<ushort> transparentBlockIds = new();
            private readonly List<byte> transparentDirections = new();
            public void EmitBoundary(ushort blockId, byte direction, int x, int y, int z, ReferenceNeighborBlockPlanes neighbors, int maxY, int maxZ, Func<ushort, bool> isOpaque)
            {
                ushort neighborId = neighbors.GetBlock(direction, x, y, z, maxY, maxZ);
                EmitIfVisible(blockId, neighborId, direction, x, y, z, isOpaque);
            }

            public void EmitIfVisible(ushort blockId, ushort neighborId, byte direction, int x, int y, int z, Func<ushort, bool> isOpaque)
            {
                bool opaque = isOpaque(blockId);
                bool visible = opaque ? !isOpaque(neighborId) : neighborId == 0 || (!isOpaque(neighborId) && neighborId != blockId);
                if (!visible)
                    return;
                List<byte> offsets = opaque ? opaqueOffsets : transparentOffsets;
                List<ushort> blockIds = opaque ? opaqueBlockIds : transparentBlockIds;
                List<byte> directions = opaque ? opaqueDirections : transparentDirections;
                offsets.Add((byte)x);
                offsets.Add((byte)y);
                offsets.Add((byte)z);
                blockIds.Add(blockId);
                directions.Add(direction);
            }

            public ReferenceFaceGenerationResult ToResult()
            {
                return new ReferenceFaceGenerationResult(opaqueOffsets.ToArray(), opaqueBlockIds.ToArray(), opaqueDirections.ToArray(), transparentOffsets.ToArray(), transparentBlockIds.ToArray(), transparentDirections.ToArray());
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace MVoxelEngine1.Graphics.Terrain
{
    public sealed class ReferenceNeighborBlockPlanes
    {
        public ReferenceNeighborBlockPlanes(
            ushort[]? negativeX = null,
            ushort[]? positiveX = null,
            ushort[]? negativeY = null,
            ushort[]? positiveY = null,
            ushort[]? negativeZ = null,
            ushort[]? positiveZ = null)
        {
            NegativeX = negativeX;
            PositiveX = positiveX;
            NegativeY = negativeY;
            PositiveY = positiveY;
            NegativeZ = negativeZ;
            PositiveZ = positiveZ;
        }

        public ushort[]? NegativeX { get; }

        public ushort[]? PositiveX { get; }

        public ushort[]? NegativeY { get; }

        public ushort[]? PositiveY { get; }

        public ushort[]? NegativeZ { get; }

        public ushort[]? PositiveZ { get; }

        internal void Validate(int maxX, int maxY, int maxZ)
        {
            ValidateLength(NegativeX, checked(maxY * maxZ), nameof(NegativeX));
            ValidateLength(PositiveX, checked(maxY * maxZ), nameof(PositiveX));
            ValidateLength(NegativeY, checked(maxX * maxZ), nameof(NegativeY));
            ValidateLength(PositiveY, checked(maxX * maxZ), nameof(PositiveY));
            ValidateLength(NegativeZ, checked(maxX * maxY), nameof(NegativeZ));
            ValidateLength(PositiveZ, checked(maxX * maxY), nameof(PositiveZ));
        }

        internal ushort GetBlock(
            byte direction,
            int x,
            int y,
            int z,
            int maxY,
            int maxZ)
        {
            ushort[]? plane;
            int index;

            switch (direction)
            {
                case 0:
                    plane = NegativeX;
                    index = z * maxY + y;
                    break;
                case 1:
                    plane = PositiveX;
                    index = z * maxY + y;
                    break;
                case 2:
                    plane = NegativeY;
                    index = x * maxZ + z;
                    break;
                case 3:
                    plane = PositiveY;
                    index = x * maxZ + z;
                    break;
                case 4:
                    plane = NegativeZ;
                    index = x * maxY + y;
                    break;
                case 5:
                    plane = PositiveZ;
                    index = x * maxY + y;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction));
            }

            return plane is null ? (ushort)0 : plane[index];
        }

        private static void ValidateLength(
            ushort[]? plane,
            int expectedLength,
            string parameterName)
        {
            if (plane is not null && plane.Length != expectedLength)
            {
                throw new ArgumentException(
                    $"{parameterName} must contain {expectedLength} block identifiers.",
                    parameterName);
            }
        }
    }

    public sealed class ReferenceFaceGenerationResult
    {
        internal ReferenceFaceGenerationResult(
            byte[] opaqueOffsets,
            ushort[] opaqueBlockIds,
            byte[] opaqueDirections,
            byte[] transparentOffsets,
            ushort[] transparentBlockIds,
            byte[] transparentDirections)
        {
            OpaqueOffsets = opaqueOffsets;
            OpaqueBlockIds = opaqueBlockIds;
            OpaqueDirections = opaqueDirections;
            TransparentOffsets = transparentOffsets;
            TransparentBlockIds = transparentBlockIds;
            TransparentDirections = transparentDirections;
        }

        public int OpaqueFaceCount => OpaqueDirections.Length;

        public byte[] OpaqueOffsets { get; }

        public ushort[] OpaqueBlockIds { get; }

        public byte[] OpaqueDirections { get; }

        public int TransparentFaceCount => TransparentDirections.Length;

        public byte[] TransparentOffsets { get; }

        public ushort[] TransparentBlockIds { get; }

        public byte[] TransparentDirections { get; }
    }

    public static class ReferenceFaceGenerator
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

        public static ReferenceFaceGenerationResult Generate(
            int maxX,
            int maxY,
            int maxZ,
            Func<int, int, int, ushort> getLocalBlock,
            ReferenceNeighborBlockPlanes neighbors,
            Func<ushort, bool> isOpaque)
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
                            ushort neighborId = IsInside(
                                neighborX,
                                neighborY,
                                neighborZ,
                                maxX,
                                maxY,
                                maxZ)
                                    ? getLocalBlock(neighborX, neighborY, neighborZ)
                                    : neighbors.GetBlock(direction, x, y, z, maxY, maxZ);

                            output.EmitIfVisible(
                                blockId,
                                neighborId,
                                direction,
                                x,
                                y,
                                z,
                                isOpaque);
                        }
                    }
                }
            }

            return output.ToResult();
        }

        public static ReferenceFaceGenerationResult GenerateUniform(
            int maxX,
            int maxY,
            int maxZ,
            ushort blockId,
            ReferenceNeighborBlockPlanes neighbors,
            Func<ushort, bool> isOpaque)
        {
            ValidateInputs(
                maxX,
                maxY,
                maxZ,
                static (_, _, _) => 0,
                neighbors,
                isOpaque);
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

        private static bool IsInside(
            int x,
            int y,
            int z,
            int maxX,
            int maxY,
            int maxZ)
        {
            return (uint)x < (uint)maxX &&
                   (uint)y < (uint)maxY &&
                   (uint)z < (uint)maxZ;
        }

        private static void ValidateInputs(
            int maxX,
            int maxY,
            int maxZ,
            Func<int, int, int, ushort> getLocalBlock,
            ReferenceNeighborBlockPlanes neighbors,
            Func<ushort, bool> isOpaque)
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

            public void EmitBoundary(
                ushort blockId,
                byte direction,
                int x,
                int y,
                int z,
                ReferenceNeighborBlockPlanes neighbors,
                int maxY,
                int maxZ,
                Func<ushort, bool> isOpaque)
            {
                ushort neighborId = neighbors.GetBlock(direction, x, y, z, maxY, maxZ);
                EmitIfVisible(blockId, neighborId, direction, x, y, z, isOpaque);
            }

            public void EmitIfVisible(
                ushort blockId,
                ushort neighborId,
                byte direction,
                int x,
                int y,
                int z,
                Func<ushort, bool> isOpaque)
            {
                bool opaque = isOpaque(blockId);
                bool visible = opaque
                    ? !isOpaque(neighborId)
                    : neighborId == 0 || (!isOpaque(neighborId) && neighborId != blockId);
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
                return new ReferenceFaceGenerationResult(
                    opaqueOffsets.ToArray(),
                    opaqueBlockIds.ToArray(),
                    opaqueDirections.ToArray(),
                    transparentOffsets.ToArray(),
                    transparentBlockIds.ToArray(),
                    transparentDirections.ToArray());
            }
        }
    }
}

using System;
using System.Collections.Generic;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.Graphics.Terrain
{
    internal sealed class ReferenceNeighborBlockPlanes
    {
        private readonly ReferenceBlockPlane negativeXPlane;
        private readonly ReferenceBlockPlane positiveXPlane;
        private readonly ReferenceBlockPlane negativeYPlane;
        private readonly ReferenceBlockPlane positiveYPlane;
        private readonly ReferenceBlockPlane negativeZPlane;
        private readonly ReferenceBlockPlane positiveZPlane;
        public ReferenceNeighborBlockPlanes(ushort[]? negativeX = null, ushort[]? positiveX = null, ushort[]? negativeY = null, ushort[]? positiveY = null, ushort[]? negativeZ = null, ushort[]? positiveZ = null)
        {
            negativeXPlane = ToPlane(negativeX);
            positiveXPlane = ToPlane(positiveX);
            negativeYPlane = ToPlane(negativeY);
            positiveYPlane = ToPlane(positiveY);
            negativeZPlane = ToPlane(negativeZ);
            positiveZPlane = ToPlane(positiveZ);
        }

        public ReferenceNeighborBlockPlanes(ReferenceBlockPlane negativeX, ReferenceBlockPlane positiveX, ReferenceBlockPlane negativeY, ReferenceBlockPlane positiveY, ReferenceBlockPlane negativeZ, ReferenceBlockPlane positiveZ)
        {
            negativeXPlane = negativeX ?? throw new ArgumentNullException(nameof(negativeX));
            positiveXPlane = positiveX ?? throw new ArgumentNullException(nameof(positiveX));
            negativeYPlane = negativeY ?? throw new ArgumentNullException(nameof(negativeY));
            positiveYPlane = positiveY ?? throw new ArgumentNullException(nameof(positiveY));
            negativeZPlane = negativeZ ?? throw new ArgumentNullException(nameof(negativeZ));
            positiveZPlane = positiveZ ?? throw new ArgumentNullException(nameof(positiveZ));
        }

        internal void Validate(int maxX, int maxY, int maxZ)
        {
            negativeXPlane.Validate(checked(maxY * maxZ), "negativeX");
            positiveXPlane.Validate(checked(maxY * maxZ), "positiveX");
            negativeYPlane.Validate(checked(maxX * maxZ), "negativeY");
            positiveYPlane.Validate(checked(maxX * maxZ), "positiveY");
            negativeZPlane.Validate(checked(maxX * maxY), "negativeZ");
            positiveZPlane.Validate(checked(maxX * maxY), "positiveZ");
        }

        internal ushort GetBlock(byte direction, int x, int y, int z, int maxY, int maxZ)
        {
            ReferenceBlockPlane plane;
            int index;
            switch (direction)
            {
                case 0:
                    plane = negativeXPlane;
                    index = z * maxY + y;
                    break;
                case 1:
                    plane = positiveXPlane;
                    index = z * maxY + y;
                    break;
                case 2:
                    plane = negativeYPlane;
                    index = x * maxZ + z;
                    break;
                case 3:
                    plane = positiveYPlane;
                    index = x * maxZ + z;
                    break;
                case 4:
                    plane = negativeZPlane;
                    index = x * maxY + y;
                    break;
                case 5:
                    plane = positiveZPlane;
                    index = x * maxY + y;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction));
            }

            return plane.GetBlock(index);
        }

        private static ReferenceBlockPlane ToPlane(ushort[]? blocks) => blocks is null ? ReferenceBlockPlane.Uniform(0) : ReferenceBlockPlane.FromBlocks(blocks);
    }
}

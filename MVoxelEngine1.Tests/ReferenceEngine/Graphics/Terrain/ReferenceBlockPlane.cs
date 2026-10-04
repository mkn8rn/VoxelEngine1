using System;
using System.Collections.Generic;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.Graphics.Terrain
{
    public sealed class ReferenceBlockPlane
    {
        private ReferenceBlockPlane(ushort uniformBlockId, ushort[]? blocks, bool uniform)
        {
            UniformBlockId = uniformBlockId;
            Blocks = blocks;
            IsUniform = uniform;
        }

        public bool IsUniform { get; }
        public ushort UniformBlockId { get; }
        public ushort[]? Blocks { get; }

        public static ReferenceBlockPlane Uniform(ushort blockId) => new(blockId, null, uniform: true);
        public static ReferenceBlockPlane FromBlocks(ushort[] blocks)
        {
            ArgumentNullException.ThrowIfNull(blocks);
            return new ReferenceBlockPlane(0, blocks, uniform: false);
        }

        internal ushort GetBlock(int index) => IsUniform ? UniformBlockId : Blocks![index];
        internal void Validate(int expectedLength, string parameterName)
        {
            if (!IsUniform && Blocks!.Length != expectedLength)
            {
                throw new ArgumentException($"{parameterName} must contain {expectedLength} block identifiers.", parameterName);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.Graphics.Terrain
{
    internal sealed class ReferenceFaceGenerationResult
    {
        internal ReferenceFaceGenerationResult(byte[] opaqueOffsets, ushort[] opaqueBlockIds, byte[] opaqueDirections, byte[] transparentOffsets, ushort[] transparentBlockIds, byte[] transparentDirections)
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
}

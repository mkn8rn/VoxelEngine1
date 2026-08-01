namespace MVoxelEngine1.Graphics.Terrain
{
    public sealed class ChunkRenderUploadData
    {
        internal ChunkRenderUploadData(
            long renderDataId,
            float chunkWorldX,
            float chunkWorldY,
            float chunkWorldZ,
            bool fullyOccluded,
            int opaqueFaceCount,
            byte[]? opaqueOffsets,
            uint[]? opaqueTileIndices,
            byte[]? opaqueFaceDirections,
            int transparentFaceCount,
            byte[]? transparentOffsets,
            uint[]? transparentTileIndices,
            byte[]? transparentFaceDirections)
        {
            RenderDataId = renderDataId;
            ChunkWorldX = chunkWorldX;
            ChunkWorldY = chunkWorldY;
            ChunkWorldZ = chunkWorldZ;
            FullyOccluded = fullyOccluded;
            OpaqueFaceCount = opaqueFaceCount;
            OpaqueOffsets = opaqueOffsets ?? Array.Empty<byte>();
            OpaqueTileIndices = opaqueTileIndices ?? Array.Empty<uint>();
            OpaqueFaceDirections = opaqueFaceDirections ?? Array.Empty<byte>();
            TransparentFaceCount = transparentFaceCount;
            TransparentOffsets = transparentOffsets ?? Array.Empty<byte>();
            TransparentTileIndices = transparentTileIndices ?? Array.Empty<uint>();
            TransparentFaceDirections = transparentFaceDirections ?? Array.Empty<byte>();
        }

        public long RenderDataId { get; }

        public float ChunkWorldX { get; }

        public float ChunkWorldY { get; }

        public float ChunkWorldZ { get; }

        public bool FullyOccluded { get; }

        public int OpaqueFaceCount { get; }

        public ReadOnlyMemory<byte> OpaqueOffsets { get; }

        public ReadOnlyMemory<uint> OpaqueTileIndices { get; }

        public ReadOnlyMemory<byte> OpaqueFaceDirections { get; }

        public int TransparentFaceCount { get; }

        public ReadOnlyMemory<byte> TransparentOffsets { get; }

        public ReadOnlyMemory<uint> TransparentTileIndices { get; }

        public ReadOnlyMemory<byte> TransparentFaceDirections { get; }
    }
}

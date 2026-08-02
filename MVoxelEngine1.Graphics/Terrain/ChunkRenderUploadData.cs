using MVoxelEngine1.Infrastructure.Models;

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
            FaceGenerationMode faceGenerationMode,
            int opaqueFaceCount,
            uint[]? opaqueRectangles,
            int transparentFaceCount,
            uint[]? transparentRectangles)
        {
            RenderDataId = renderDataId;
            ChunkWorldX = chunkWorldX;
            ChunkWorldY = chunkWorldY;
            ChunkWorldZ = chunkWorldZ;
            FullyOccluded = fullyOccluded;
            FaceGenerationMode = faceGenerationMode;
            OpaqueFaceCount = opaqueFaceCount;
            OpaqueRectangles = opaqueRectangles ?? Array.Empty<uint>();
            OpaqueRectangleCount = PackedFaceRectangle.GetRectangleCount(
                OpaqueRectangles.Span);
            TransparentFaceCount = transparentFaceCount;
            TransparentRectangles = transparentRectangles ?? Array.Empty<uint>();
            TransparentRectangleCount = PackedFaceRectangle.GetRectangleCount(
                TransparentRectangles.Span);
            if (PackedFaceRectangle.CountLogicalFaces(OpaqueRectangles.Span) !=
                    opaqueFaceCount ||
                PackedFaceRectangle.CountLogicalFaces(
                    TransparentRectangles.Span) != transparentFaceCount)
            {
                throw new InvalidDataException(
                    "Packed face counts do not match their logical face counts.");
            }
        }

        public long RenderDataId { get; }

        public float ChunkWorldX { get; }

        public float ChunkWorldY { get; }

        public float ChunkWorldZ { get; }

        public bool FullyOccluded { get; }

        public FaceGenerationMode FaceGenerationMode { get; }

        public int OpaqueFaceCount { get; }

        public int OpaqueRectangleCount { get; }

        public ReadOnlyMemory<uint> OpaqueRectangles { get; }

        public int TransparentFaceCount { get; }

        public int TransparentRectangleCount { get; }

        public ReadOnlyMemory<uint> TransparentRectangles { get; }
    }
}

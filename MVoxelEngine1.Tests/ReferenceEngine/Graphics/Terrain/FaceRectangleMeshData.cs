using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Graphics.Terrain
{
    internal sealed class FaceRectangleMeshData : IDisposable
    {
        private readonly uint[]? managedOpaqueRectangles;
        private readonly uint[]? managedTransparentRectangles;
        private NativeTransfer<uint>? nativeOpaqueRectangles;
        private NativeTransfer<uint>? nativeTransparentRectangles;
        private int disposed;

        internal FaceRectangleMeshData(
            int opaqueFaceCount,
            uint[] opaqueRectangles,
            int transparentFaceCount,
            uint[] transparentRectangles)
        {
            OpaqueFaceCount = opaqueFaceCount;
            managedOpaqueRectangles = opaqueRectangles;
            OpaqueWordCount = opaqueRectangles.Length;
            TransparentFaceCount = transparentFaceCount;
            managedTransparentRectangles = transparentRectangles;
            TransparentWordCount = transparentRectangles.Length;
        }

        internal FaceRectangleMeshData(
            int opaqueFaceCount,
            NativeTransfer<uint>? opaqueRectangles,
            int transparentFaceCount,
            NativeTransfer<uint>? transparentRectangles)
        {
            try
            {
                nativeOpaqueRectangles = NativeTransfer<uint>.Move(
                    ref opaqueRectangles);
                OpaqueWordCount = nativeOpaqueRectangles.Length;
                nativeTransparentRectangles = NativeTransfer<uint>.Move(
                    ref transparentRectangles);
                TransparentWordCount = nativeTransparentRectangles.Length;
                OpaqueFaceCount = opaqueFaceCount;
                TransparentFaceCount = transparentFaceCount;
            }
            catch
            {
                try
                {
                    nativeOpaqueRectangles?.Dispose();
                    nativeOpaqueRectangles = null;
                }
                finally
                {
                    nativeTransparentRectangles?.Dispose();
                    nativeTransparentRectangles = null;
                }

                throw;
            }
            finally
            {
                opaqueRectangles?.Dispose();
                transparentRectangles?.Dispose();
            }
        }

        internal int OpaqueFaceCount { get; }

        internal int OpaqueWordCount { get; }

        internal int OpaqueRectangleCount =>
            OpaqueWordCount / PackedFaceRectangle.WordsPerRectangle;

        internal int TransparentFaceCount { get; }

        internal int TransparentWordCount { get; }

        internal int TransparentRectangleCount =>
            TransparentWordCount / PackedFaceRectangle.WordsPerRectangle;

        internal TResult ReadOpaque<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

            return nativeOpaqueRectangles is not null
                ? nativeOpaqueRectangles.Read(nativeReader)
                : managedReader(managedOpaqueRectangles ?? Array.Empty<uint>());
        }

        internal TResult ReadTransparent<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

            return nativeTransparentRectangles is not null
                ? nativeTransparentRectangles.Read(nativeReader)
                : managedReader(
                    managedTransparentRectangles ?? Array.Empty<uint>());
        }

        internal static FaceRectangleMeshData FromFaces(
            int opaqueFaceCount,
            byte[] opaqueOffsets,
            uint[] opaqueTileIndices,
            byte[] opaqueDirections,
            int transparentFaceCount,
            byte[] transparentOffsets,
            uint[] transparentTileIndices,
            byte[] transparentDirections)
        {
            return new FaceRectangleMeshData(
                opaqueFaceCount,
                PackFaces(
                    opaqueFaceCount,
                    opaqueOffsets,
                    opaqueTileIndices,
                    opaqueDirections),
                transparentFaceCount,
                PackFaces(
                    transparentFaceCount,
                    transparentOffsets,
                    transparentTileIndices,
                    transparentDirections));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            try
            {
                nativeOpaqueRectangles?.Dispose();
                nativeOpaqueRectangles = null;
            }
            finally
            {
                nativeTransparentRectangles?.Dispose();
                nativeTransparentRectangles = null;
            }
        }

        private static uint[] PackFaces(
            int faceCount,
            byte[] offsets,
            uint[] tileIndices,
            byte[] directions)
        {
            if (offsets.Length != checked(faceCount * 3) ||
                tileIndices.Length != faceCount ||
                directions.Length != faceCount)
            {
                throw new InvalidDataException(
                    "Face arrays have inconsistent lengths.");
            }

            var result = new uint[checked(
                faceCount * PackedFaceRectangle.WordsPerRectangle)];
            for (int index = 0; index < faceCount; index++)
            {
                int offsetIndex = index * 3;
                PackedFaceRectangle.Write(
                    result,
                    index * PackedFaceRectangle.WordsPerRectangle,
                    offsets[offsetIndex],
                    offsets[offsetIndex + 1],
                    offsets[offsetIndex + 2],
                    directions[index],
                    1,
                    1,
                    tileIndices[index]);
            }

            return result;
        }
    }
}

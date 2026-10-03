using MVoxelEngine1.Infrastructure.Models;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Graphics.Terrain
{
    public delegate TResult PackedFaceReader<TResult>(
        ReadOnlySpan<uint> rectangles);

    public sealed class ChunkRenderUploadRetention : IDisposable
    {
        private readonly object gate = new();
        private ChunkRenderUploadData? owner;

        internal ChunkRenderUploadRetention(ChunkRenderUploadData owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            ChunkRenderUploadData? current;
            lock (gate)
            {
                current = owner;
                owner = null;
            }
            current?.ReleaseRetention();
        }

        public TResult ReadOpaque<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader)
        {
            lock (gate)
            {
                ChunkRenderUploadData current = owner ??
                    throw new ObjectDisposedException(
                        nameof(ChunkRenderUploadRetention));
                return current.ReadOpaqueRetained(
                    nativeReader,
                    managedReader);
            }
        }

        public TResult ReadTransparent<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader)
        {
            lock (gate)
            {
                ChunkRenderUploadData current = owner ??
                    throw new ObjectDisposedException(
                        nameof(ChunkRenderUploadRetention));
                return current.ReadTransparentRetained(
                    nativeReader,
                    managedReader);
            }
        }
    }

    public sealed class ChunkRenderUploadData : IDisposable
    {
        private FaceRectangleMeshData? meshData;
        private int retainCount = 1;
        private int rootDisposed;

        internal ChunkRenderUploadData(
            long renderDataId,
            float chunkWorldX,
            float chunkWorldY,
            float chunkWorldZ,
            bool fullyOccluded,
            FaceGenerationMode faceGenerationMode,
            FaceRectangleMeshData meshData)
        {
            ArgumentNullException.ThrowIfNull(meshData);
            this.meshData = meshData;
            RenderDataId = renderDataId;
            ChunkWorldX = chunkWorldX;
            ChunkWorldY = chunkWorldY;
            ChunkWorldZ = chunkWorldZ;
            FullyOccluded = fullyOccluded;
            FaceGenerationMode = faceGenerationMode;
            OpaqueFaceCount = meshData.OpaqueFaceCount;
            OpaqueWordCount = meshData.OpaqueWordCount;
            OpaqueRectangleCount = meshData.OpaqueRectangleCount;
            TransparentFaceCount = meshData.TransparentFaceCount;
            TransparentWordCount = meshData.TransparentWordCount;
            TransparentRectangleCount = meshData.TransparentRectangleCount;
            if (ReadOpaque(static view =>
                    PackedFaceRectangle.CountLogicalFaces(view.AsSpan()),
                    PackedFaceRectangle.CountLogicalFaces) !=
                    OpaqueFaceCount ||
                ReadTransparent(static view =>
                    PackedFaceRectangle.CountLogicalFaces(view.AsSpan()),
                    PackedFaceRectangle.CountLogicalFaces) !=
                    TransparentFaceCount)
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

        public int OpaqueWordCount { get; }

        public int TransparentFaceCount { get; }

        public int TransparentRectangleCount { get; }

        public int TransparentWordCount { get; }

        public TResult ReadOpaque<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader)
        {
            ArgumentNullException.ThrowIfNull(nativeReader);
            ArgumentNullException.ThrowIfNull(managedReader);
            using ChunkRenderUploadRetention retention = Retain();
            return retention.ReadOpaque(nativeReader, managedReader);
        }

        public TResult ReadTransparent<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader)
        {
            ArgumentNullException.ThrowIfNull(nativeReader);
            ArgumentNullException.ThrowIfNull(managedReader);
            using ChunkRenderUploadRetention retention = Retain();
            return retention.ReadTransparent(
                nativeReader,
                managedReader);
        }

        public ChunkRenderUploadRetention Retain()
        {
            while (true)
            {
                int current = Volatile.Read(ref retainCount);
                if (current <= 0 || Volatile.Read(ref rootDisposed) != 0)
                    throw new ObjectDisposedException(
                        nameof(ChunkRenderUploadData));
                if (current == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The render upload retention count reached its limit.");
                }

                if (Interlocked.CompareExchange(
                        ref retainCount,
                        current + 1,
                        current) == current)
                {
                    return new ChunkRenderUploadRetention(this);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref rootDisposed, 1) != 0)
                return;

            ReleaseReference();
        }

        internal int RetainCountForTest => Volatile.Read(ref retainCount);

        internal bool IsDisposedForTest => Volatile.Read(ref rootDisposed) != 0;

        internal void ReleaseRetention() => ReleaseReference();

        internal TResult ReadOpaqueRetained<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader) =>
            GetMeshData().ReadOpaque(nativeReader, managedReader);

        internal TResult ReadTransparentRetained<TResult>(
            NativeLeaseFunc<uint, TResult> nativeReader,
            PackedFaceReader<TResult> managedReader) =>
            GetMeshData().ReadTransparent(nativeReader, managedReader);

        private FaceRectangleMeshData GetMeshData() =>
            Volatile.Read(ref meshData) ??
            throw new ObjectDisposedException(nameof(ChunkRenderUploadData));

        private void ReleaseReference()
        {
            int remaining = Interlocked.Decrement(ref retainCount);
            if (remaining < 0)
            {
                throw new InvalidOperationException(
                    "The render upload retention count became negative.");
            }

            if (remaining != 0)
                return;

            FaceRectangleMeshData? current = Interlocked.Exchange(
                ref meshData,
                null);
            current?.Dispose();
        }
    }
}

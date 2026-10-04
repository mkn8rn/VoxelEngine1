using MVoxelEngine1.Infrastructure.Models;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Graphics.Terrain
{
    internal sealed class ChunkRenderUploadRetention : IDisposable
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

        public TResult ReadOpaque<TResult>(NativeLeaseFunc<uint, TResult> nativeReader, PackedFaceReader<TResult> managedReader)
        {
            lock (gate)
            {
                ChunkRenderUploadData current = owner ?? throw new ObjectDisposedException(nameof(ChunkRenderUploadRetention));
                return current.ReadOpaqueRetained(nativeReader, managedReader);
            }
        }

        public TResult ReadTransparent<TResult>(NativeLeaseFunc<uint, TResult> nativeReader, PackedFaceReader<TResult> managedReader)
        {
            lock (gate)
            {
                ChunkRenderUploadData current = owner ?? throw new ObjectDisposedException(nameof(ChunkRenderUploadRetention));
                return current.ReadTransparentRetained(nativeReader, managedReader);
            }
        }
    }
}

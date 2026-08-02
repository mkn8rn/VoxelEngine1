using Supprocom.NativeAllocationManagement;
using MVoxelEngine1.Graphics.Terrain.Sections;

namespace MVoxelEngine1.Graphics.Terrain
{
    public sealed class PackedFaceNativePool : IDisposable
    {
        private readonly NativePool<uint> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        private readonly PackedFaceStagingWorkspace stagingWorkspace = new();

        internal FaceRectangleMeshData Build(SectionRender renderer) =>
            renderer.Build(pool, stagingWorkspace);

        public void Dispose()
        {
            stagingWorkspace.Dispose();
            pool.Dispose();
        }
    }

    internal sealed class PackedFaceStagingWorkspace : IDisposable
    {
        private const int InitialOpaqueWordCapacity = 65_536;
        private const int InitialTransparentWordCapacity = 2_048;

        private uint[] opaqueWords = new uint[InitialOpaqueWordCapacity];
        private uint[] transparentWords =
            new uint[InitialTransparentWordCapacity];

        internal uint[] OpaqueBuffer
        {
            get
            {
                ObjectDisposedException.ThrowIf(
                    opaqueWords.Length == 0,
                    this);
                return opaqueWords;
            }
        }

        internal uint[] TransparentBuffer
        {
            get
            {
                ObjectDisposedException.ThrowIf(
                    transparentWords.Length == 0,
                    this);
                return transparentWords;
            }
        }

        internal void Adopt(uint[] opaque, uint[] transparent)
        {
            ArgumentNullException.ThrowIfNull(opaque);
            ArgumentNullException.ThrowIfNull(transparent);
            ObjectDisposedException.ThrowIf(opaqueWords.Length == 0, this);
            opaqueWords = opaque;
            transparentWords = transparent;
        }

        public void Dispose()
        {
            opaqueWords = Array.Empty<uint>();
            transparentWords = Array.Empty<uint>();
        }
    }
}

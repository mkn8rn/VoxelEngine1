using MVoxelEngine1.Graphics.Terrain.Sections;

namespace MVoxelEngine1.Graphics.Terrain
{
    public sealed class PackedFaceNativePool : IDisposable
    {
        private readonly PackedFaceStagingWorkspace stagingWorkspace = new();

        internal FaceRectangleMeshData Build(SectionRender renderer) =>
            renderer.Build(stagingWorkspace);

        public void Dispose()
        {
            stagingWorkspace.Dispose();
        }
    }

    internal sealed class PackedFaceStagingWorkspace : IDisposable
    {
        private const int InitialOpaqueBatchWordCapacity = 65_536;
        private const int InitialTransparentWordCapacity = 2_048;

        private uint[] opaqueBatchWords =
            new uint[InitialOpaqueBatchWordCapacity];
        private uint[] transparentWords =
            new uint[InitialTransparentWordCapacity];

        internal uint[] GetOpaqueBatchBuffer(int minimumWordCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minimumWordCount);
            ObjectDisposedException.ThrowIf(
                opaqueBatchWords.Length == 0,
                this);
            if (opaqueBatchWords.Length < minimumWordCount)
            {
                int nextLength = Math.Max(
                    minimumWordCount,
                    checked(opaqueBatchWords.Length * 2));
                Array.Resize(ref opaqueBatchWords, nextLength);
            }

            return opaqueBatchWords;
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

        internal void AdoptTransparent(uint[] transparent)
        {
            ArgumentNullException.ThrowIfNull(transparent);
            ObjectDisposedException.ThrowIf(
                opaqueBatchWords.Length == 0,
                this);
            transparentWords = transparent;
        }

        public void Dispose()
        {
            opaqueBatchWords = Array.Empty<uint>();
            transparentWords = Array.Empty<uint>();
        }
    }
}

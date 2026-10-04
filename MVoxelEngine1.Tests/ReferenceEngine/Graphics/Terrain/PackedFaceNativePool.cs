using MVoxelEngine1.Graphics.Terrain.Sections;

namespace MVoxelEngine1.Graphics.Terrain
{
    internal sealed class PackedFaceNativePool : IDisposable
    {
        private readonly PackedFaceStagingWorkspace stagingWorkspace = new();
        internal FaceRectangleMeshData Build(SectionRender renderer) => renderer.Build(stagingWorkspace);
        public void Dispose()
        {
            stagingWorkspace.Dispose();
        }
    }
}

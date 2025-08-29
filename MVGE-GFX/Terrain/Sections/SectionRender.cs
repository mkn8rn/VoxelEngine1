using MVGE_INF.Models.Generation;
using MVGE_GFX.Textures;
using System;
using System.Buffers;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        private readonly ChunkPrerenderData data;
        private readonly BlockTextureAtlas atlas;
        public SectionRender(ChunkPrerenderData data, BlockTextureAtlas atlas)
        {
            this.data = data; this.atlas = atlas;
        }

        // Placeholder implementation: returns empty buffers (no meshing yet)
        public void Build(out byte[] verts, out byte[] uvs, out ushort[] idxU16, out uint[] idxU32, out bool useUShort, out int vertBytes, out int uvBytes, out int indices)
        {
            verts = Array.Empty<byte>();
            uvs = Array.Empty<byte>();
            idxU16 = Array.Empty<ushort>();
            idxU32 = Array.Empty<uint>();
            useUShort = true;
            vertBytes = 0; uvBytes = 0; indices = 0;
        }
    }
}

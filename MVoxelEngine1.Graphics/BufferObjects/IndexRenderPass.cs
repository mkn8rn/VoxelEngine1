using OpenTK.Graphics.OpenGL4;
using System.Collections.Generic;

namespace MVoxelEngine1.Graphics.BufferObjects
{
    // Render passes supported by the renderer. Opaque = default geometry, Transparent = alpha blended geometry.
    public enum IndexRenderPass : byte
    {
        Opaque = 0,
        Transparent = 1
    }
}

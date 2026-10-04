using OpenTK.Graphics.OpenGL4;
using System.Collections.Generic;

namespace MVoxelEngine1.Graphics.BufferObjects
{
    // Render passes supported by the renderer. Opaque = default geometry, Transparent = alpha blended geometry.
#pragma warning disable CA1028 // Retain the existing one-byte enum contract used by block, face and render-pass data; changing the underlying type would alter that contract.
    public enum IndexRenderPass : byte
#pragma warning restore CA1028
    {
        Opaque = 0,
        Transparent = 1
    }
}

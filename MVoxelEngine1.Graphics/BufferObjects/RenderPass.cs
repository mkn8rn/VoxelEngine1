using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace MVoxelEngine1.Graphics.BufferObjects
{
    // Render passes supported by the renderer. Opaque = default geometry, Transparent = alpha blended geometry.
#pragma warning disable CA1028 // Retain the existing one-byte enum contract used by block, face and render-pass data; changing the underlying type would alter that contract.
    public enum RenderPass : byte
#pragma warning restore CA1028
    {
        Opaque = 0,
        Transparent = 1
    }
}

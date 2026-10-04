using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Graphics.Models
{
#pragma warning disable CA1028 // Retain the existing one-byte enum contract used by block, face and render-pass data; changing the underlying type would alter that contract.
    public enum Faces : byte
#pragma warning restore CA1028
    {
        LEFT = 0, // -X
        RIGHT = 1, // +X
        BOTTOM = 2, // -Y
        TOP = 3, // +Y
        BACK = 4, // -Z
        FRONT = 5  // +Z
    }
}

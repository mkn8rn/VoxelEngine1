using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.Models
{
    public enum Faces : byte
    {
        LEFT = 0, // -X
        RIGHT = 1, // +X
        BOTTOM = 2, // -Y
        TOP = 3, // +Y
        BACK = 4, // -Z
        FRONT = 5  // +Z
    }
}

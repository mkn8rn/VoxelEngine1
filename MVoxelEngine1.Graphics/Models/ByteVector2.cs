using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Graphics.Models
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public record struct ByteVector2
    {
        public byte x { readonly get; set; }
        public byte y { readonly get; set; }
    }
}

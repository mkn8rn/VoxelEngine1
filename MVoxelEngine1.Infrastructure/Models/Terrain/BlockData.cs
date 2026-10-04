using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Terrain
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public record struct BlockData
    {
        public ushort blockType { readonly get; set; }
    }
}

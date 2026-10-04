using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Terrain
{
#pragma warning disable CA1028 // Retain the existing one-byte enum contract used by block, face and render-pass data; changing the underlying type would alter that contract.
    public enum BaseBlockType : byte
#pragma warning restore CA1028
    {
        Empty = 0,
        Gas = 1,
        Mist = 2,
        Mineral = 3,
        Metal = 4,
        Soil = 5,
        Stone = 6,
        Wood = 7,
        Fungus = 8,
        Flesh = 9,
        Glass = 10,
        Water = 11,
    }
}

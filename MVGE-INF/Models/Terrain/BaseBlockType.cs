using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Terrain
{
    public enum BaseBlockType : byte
    {
        Empty = 0,
        Gas = 1,
        // empty = 2
        Mineral = 3,
        // empty = 4
        Soil = 5,
        Stone = 6,
        Wood = 7,
        Fungus = 8,
        // empty = 9
        Glass = 10,
        Water = 11,
    }
}

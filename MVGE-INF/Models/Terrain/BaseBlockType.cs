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
        Living = 2,
        Mineral = 3,
        Liquid = 4,
        Soil = 5,
        Stone = 6,
        Water = 7,
        Wood = 8,
        Glass = 9
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Terrain
{
    public enum BaseBlockType : byte
    {
        Empty,
        Air,
        Living,
        Mineral,
        Liquid,
        Soil,
        Stone,
        Water,
        Wood
    }
}

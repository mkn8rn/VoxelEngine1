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
        Gas,
        Living,
        Mineral,
        Liquid,
        Soil,
        Stone,
        Water,
        Wood
    }
}

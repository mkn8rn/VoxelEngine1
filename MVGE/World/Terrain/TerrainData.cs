using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE.World.Terrain
{
    public enum WorldDirection : byte
    {
        North,
        NorthEast,
        East,
        SouthEast,
        South,
        SouthWest,
        West,
        NorthWest
    }
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

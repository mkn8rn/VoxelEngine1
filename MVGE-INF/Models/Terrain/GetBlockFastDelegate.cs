using System.Collections.Generic;

namespace MVGE_INF.Models.Terrain
{
    public delegate bool GetBlockFastDelegate(int wx, int wy, int wz, out ushort block);
}

using System.Collections.Generic;

namespace MVoxelEngine1.Infrastructure.Models.Terrain
{
    public delegate bool GetBlockFastDelegate(int wx, int wy, int wz, out ushort block);
}

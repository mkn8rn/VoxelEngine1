namespace MVoxelEngine1.Infrastructure.Models.Terrain
{
    public interface IBlockSource
    {
        ushort GetBlock(int wx, int wy, int wz);
        bool TryGetBlockFast(int wx, int wy, int wz, out ushort block);
    }
}

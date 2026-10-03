namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeSavePartition
{
    internal const int Width = 16;
    internal static (int bx, int bz) GetBatchIndices(int chunkX, int chunkZ) =>
        ((int)Math.Floor((double)chunkX / Width), (int)Math.Floor((double)chunkZ / Width));
}

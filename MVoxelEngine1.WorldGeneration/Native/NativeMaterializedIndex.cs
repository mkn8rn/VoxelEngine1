using System.Numerics;

namespace MVoxelEngine1.WorldGeneration.Native;

internal ref partial struct NativeGtrtSessionView
{
    private Span<int> MaterializedIndex => ReadRange<int>(header.MaterializedIndexOffset, header.MaterializedIndexCapacity);

    internal int FindMaterializedChunkIndex(int chunkX, int chunkY, int chunkZ)
    {
        int count = State.MaterializedChunkCount;
        if ((uint)count > (uint)MaterializedChunks.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return -1;
        }
        if (count == 0)
            return -1;
        Span<int> table = MaterializedIndex;
        int slot = GetMaterializedSlot(chunkX, chunkY, chunkZ, table.Length);
        for (int probe = 0; probe < table.Length; probe++)
        {
            int entry = table[slot];
            if (entry == 0)
                return -1;
            if (entry > 0)
            {
                int index = entry - 1;
                if ((uint)index >= (uint)count)
                {
                    Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                    return -1;
                }
                ref NativeMaterializedChunkRecord chunk = ref MaterializedChunks[index];
                if (chunk.State == 1 && chunk.ChunkX == chunkX && chunk.ChunkY == chunkY && chunk.ChunkZ == chunkZ)
                    return index;
            }
            slot = (slot + 1) & (table.Length - 1);
        }
        return -1;
    }

    internal void IndexMaterializedChunk(int index)
    {
        NativeMaterializedChunkRecord chunk = MaterializedChunks[index];
        Span<int> table = MaterializedIndex;
        int slot = GetMaterializedSlot(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, table.Length);
        int deletedSlot = -1;
        for (int probe = 0; probe < table.Length; probe++)
        {
            int entry = table[slot];
            if (entry == index + 1)
                return;
            if (entry < 0 && deletedSlot < 0)
                deletedSlot = slot;
            if (entry == 0)
            {
                table[deletedSlot < 0 ? slot : deletedSlot] = index + 1;
                return;
            }
            slot = (slot + 1) & (table.Length - 1);
        }
        if (deletedSlot >= 0)
        {
            table[deletedSlot] = index + 1;
            return;
        }
        throw new InvalidOperationException("The native materialized coordinate index is exhausted.");
    }

    internal void RemoveMaterializedChunkIndex(int index)
    {
        NativeMaterializedChunkRecord chunk = MaterializedChunks[index];
        Span<int> table = MaterializedIndex;
        int slot = GetMaterializedSlot(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, table.Length);
        for (int probe = 0; probe < table.Length; probe++)
        {
            if (table[slot] == 0)
                return;
            if (table[slot] == index + 1)
            {
                table[slot] = -1;
                return;
            }
            slot = (slot + 1) & (table.Length - 1);
        }
    }

    internal static int GetMaterializedSlot(int x, int y, int z, int capacity)
    {
        uint hash = unchecked((uint)x * 0x9e3779b9u ^ BitOperations.RotateLeft((uint)y, 11) ^
            BitOperations.RotateLeft((uint)z, 22));
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x7feb352du);
        hash ^= hash >> 15;
        hash = unchecked(hash * 0x846ca68bu);
        hash ^= hash >> 16;
        return (int)(hash & (uint)(capacity - 1));
    }
}

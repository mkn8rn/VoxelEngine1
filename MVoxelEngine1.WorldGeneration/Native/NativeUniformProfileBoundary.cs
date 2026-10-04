using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeUniformProfileBoundary
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool TryFill(scoped ref NativeGtrtSessionView session, NativeChunkRecord source,
        byte direction, int normal, int uSize, int vSize, ushort sourceId, bool sourceOpaque, Span<int> faces)
    {
        int axis = direction / 2;
        int delta = (direction & 1) == 0 ? -1 : 1;
        int neighborX = source.ChunkX + (axis == 0 ? delta : 0);
        int neighborY = source.ChunkY + (axis == 1 ? delta : 0);
        int neighborZ = source.ChunkZ + (axis == 2 ? delta : 0);
        if (!TryGetProfileColumn(ref session, neighborX, neighborY, neighborZ, out int columnIndex))
            return false;
        NativeColumnRecord column = session.Columns[columnIndex];
        NativeTerrainMaterialSet materials = column.ReplacementMode == 1 ? column.ResolvedMaterials : session.Materials;
        if (!session.TryGetBlockDescriptor(materials.Stone.Id, out NativeBlockDescriptor stone) ||
            !session.TryGetBlockDescriptor(materials.Soil.Id, out NativeBlockDescriptor soil) ||
            !session.TryGetBlockDescriptor(materials.Water.Id, out NativeBlockDescriptor water))
            return false;
        bool stoneOpaque = stone.HasFlag(NativeBlockFlags.Opaque);
        bool soilOpaque = soil.HasFlag(NativeBlockFlags.Opaque);
        bool waterOpaque = water.HasFlag(NativeBlockFlags.Opaque);
        ReadOnlySpan<BlockColumnProfile> profiles = session.GetColumnProfiles(columnIndex);
        int depth = session.ChunkSizeZ;
        int edgeX = (direction & 1) == 0 ? session.ChunkSizeX - 1 : 0;
        int edgeZ = (direction & 1) == 0 ? depth - 1 : 0;
        int bottom = unchecked(source.ChunkY * session.ChunkSizeY);
        for (int v = 0; v < vSize; v++)
        for (int u = 0; u < uSize; u++)
        {
            int x = axis == 0 ? edgeX : u;
            int z = axis == 2 ? edgeZ : axis == 0 ? u : v;
            int worldY = unchecked(bottom + (axis == 1 ? normal + delta : v));
            ushort neighbor = materials.GetBlockWorld(in profiles[x * depth + z], worldY);
            bool neighborOpaque = neighbor != 0 && (neighbor == stone.Id ? stoneOpaque :
                neighbor == soil.Id ? soilOpaque : waterOpaque);
            faces[v * uSize + u] = NativeGeneratedTerrain.FaceVisible(sourceOpaque, sourceId,
                neighborOpaque, neighbor) ? sourceId : 0;
        }
        return true;
    }

    private static bool TryGetProfileColumn(scoped ref NativeGtrtSessionView session,
        int x, int y, int z, out int columnIndex)
    {
        columnIndex = session.GetColumnIndex(x, z);
        if (columnIndex < 0)
            return false;
        NativeColumnRecord column = session.Columns[columnIndex];
        if (column.State != NativeColumnState.Generated || column.GenerationEpoch != session.State.SessionEpoch ||
            column.ReplacementMode == 2)
            return false;
        int chunkIndex = session.GetChunkIndex(x, y, z);
        if (chunkIndex >= 0)
            return session.Chunks[chunkIndex].StorageKind == NativeChunkStorageKind.GeneratedProfile;
        return session.FindMaterializedChunkIndex(x, y, z) < 0 && session.State.FailureCode == 0;
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;
internal static class NativeGeneratedTerrain
{
    internal static NativeTerrainMaterialSet GetMaterials(scoped ref NativeGtrtSessionView session, int chunkIndex)
    {
        NativeColumnRecord column = session.Columns[session.Chunks[chunkIndex].ColumnIndex];
        return column.ReplacementMode == 1 ? column.ResolvedMaterials : session.Materials;
    }

    internal static bool TryGetBlock(scoped ref NativeGtrtSessionView session, int chunkIndex, int localX, int localY, int localZ, out ushort blockId)
    {
        if ((uint)chunkIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            blockId = 0;
            return false;
        }

        NativeChunkRecord source = session.Chunks[chunkIndex];
        int chunkOffsetX = FloorDiv(localX, session.ChunkSizeX);
        int chunkOffsetY = FloorDiv(localY, session.ChunkSizeY);
        int chunkOffsetZ = FloorDiv(localZ, session.ChunkSizeZ);
        int normalizedX = localX - chunkOffsetX * session.ChunkSizeX;
        int normalizedY = localY - chunkOffsetY * session.ChunkSizeY;
        int normalizedZ = localZ - chunkOffsetZ * session.ChunkSizeZ;
        int targetChunkX = unchecked(source.ChunkX + chunkOffsetX);
        int targetChunkY = unchecked(source.ChunkY + chunkOffsetY);
        int targetChunkZ = unchecked(source.ChunkZ + chunkOffsetZ);
        int targetChunkIndex = session.GetChunkIndex(targetChunkX, targetChunkY, targetChunkZ);
        bool handled;
        bool materializedRead = targetChunkIndex >= 0 ? NativeMaterializedTerrain.TryGetBlock(ref session, targetChunkIndex, normalizedX, normalizedY, normalizedZ, out blockId, out handled) : NativeMaterializedTerrain.TryGetBlockAtWorldChunk(ref session, targetChunkX, targetChunkY, targetChunkZ, normalizedX, normalizedY, normalizedZ, out blockId, out handled);
        if (!materializedRead)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            blockId = 0;
            return false;
        }

        return handled || TryGetGeneratedBlockAtWorldChunk(ref session, targetChunkX, targetChunkY, targetChunkZ, normalizedX, normalizedY, normalizedZ, out blockId);
    }

    internal static bool TryGetGeneratedBlock(scoped ref NativeGtrtSessionView session, int chunkIndex, int localX, int localY, int localZ, out ushort blockId)
    {
        if ((uint)chunkIndex >= (uint)session.ChunkCount || (uint)localX >= (uint)session.ChunkSizeX || (uint)localY >= (uint)session.ChunkSizeY || (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            blockId = 0;
            return false;
        }

        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        return TryGetGeneratedBlockAtWorldChunk(ref session, chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, localX, localY, localZ, out blockId);
    }

    private static bool TryGetGeneratedBlockAtWorldChunk(scoped ref NativeGtrtSessionView session, int chunkX, int chunkY, int chunkZ, int localX, int localY, int localZ, out ushort blockId)
    {
        int columnIndex = session.GetColumnIndex(chunkX, chunkZ);
        if (columnIndex < 0)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            blockId = 0;
            return false;
        }

        ref NativeColumnRecord column = ref session.Columns[columnIndex];
        ref int columnState = ref Unsafe.As<NativeColumnState, int>(ref column.State);
        if (Volatile.Read(ref columnState) != (int)NativeColumnState.Generated || column.GenerationEpoch != session.State.SessionEpoch)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            blockId = 0;
            return false;
        }

        int profileIndex = checked(column.ProfileOffset + localX * session.ChunkSizeZ + localZ);
        BlockColumnProfile profile = session.Profiles[profileIndex];
        int worldY = unchecked(chunkY * session.ChunkSizeY + localY);
        NativeTerrainMaterialSet materials = column.ReplacementMode == 1 ? column.ResolvedMaterials : session.Materials;
        blockId = materials.GetBlockWorld(in profile, worldY);
        if (column.ReplacementMode == 2)
        {
            var game = session.GameSnapshot;
            blockId = NativeReplacementRules.Apply(ref game, in game.Biomes[column.BiomeIndex], blockId, worldY, Math.Max(profile.StoneEnd, profile.SoilEnd));
        }

        return true;
    }

    internal static bool TryGetProfile(scoped ref NativeGtrtSessionView session, int chunkIndex, int localX, int localZ, out BlockColumnProfile profile)
    {
        if ((uint)chunkIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            profile = default;
            return false;
        }

        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        int chunkOffsetX = FloorDiv(localX, session.ChunkSizeX);
        int chunkOffsetZ = FloorDiv(localZ, session.ChunkSizeZ);
        int normalizedX = localX - chunkOffsetX * session.ChunkSizeX;
        int normalizedZ = localZ - chunkOffsetZ * session.ChunkSizeZ;
        int targetChunkX = unchecked(chunk.ChunkX + chunkOffsetX);
        int targetChunkZ = unchecked(chunk.ChunkZ + chunkOffsetZ);
        int columnIndex = session.GetColumnIndex(targetChunkX, targetChunkZ);
        if (columnIndex < 0)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            profile = default;
            return false;
        }

        ref NativeColumnRecord column = ref session.Columns[columnIndex];
        ref int columnState = ref Unsafe.As<NativeColumnState, int>(ref column.State);
        if (Volatile.Read(ref columnState) != (int)NativeColumnState.Generated || column.GenerationEpoch != session.State.SessionEpoch)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            profile = default;
            return false;
        }

        int profileIndex = checked(column.ProfileOffset + normalizedX * session.ChunkSizeZ + normalizedZ);
        profile = session.Profiles[profileIndex];
        return true;
    }

    internal static bool TryIsFaceVisible(scoped ref NativeGtrtSessionView session, int chunkIndex, int localX, int localY, int localZ, byte direction, out bool visible)
    {
        if (!TryGetBlock(ref session, chunkIndex, localX, localY, localZ, out ushort sourceId))
        {
            visible = false;
            return false;
        }

        if (sourceId == 0)
        {
            visible = false;
            return true;
        }

        int neighborX = localX;
        int neighborY = localY;
        int neighborZ = localZ;
        switch (direction)
        {
            case 0:
                neighborX--;
                break;
            case 1:
                neighborX++;
                break;
            case 2:
                neighborY--;
                break;
            case 3:
                neighborY++;
                break;
            case 4:
                neighborZ--;
                break;
            case 5:
                neighborZ++;
                break;
            default:
                session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
                visible = false;
                return false;
        }

        if (!TryGetBlock(ref session, chunkIndex, neighborX, neighborY, neighborZ, out ushort neighborId) || !session.TryIsBlockOpaque(sourceId, out bool sourceOpaque) || !session.TryIsBlockOpaque(neighborId, out bool neighborOpaque))
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            visible = false;
            return false;
        }

        visible = FaceVisible(sourceOpaque, sourceId, neighborOpaque, neighborId);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool FaceVisible(bool sourceOpaque, ushort sourceBlockId, bool neighborOpaque, ushort neighborBlockId)
    {
        if (sourceOpaque)
            return !neighborOpaque;
        if (neighborOpaque)
            return false;
        return neighborBlockId == 0 || neighborBlockId != sourceBlockId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        if (value % divisor < 0)
            quotient--;
        return quotient;
    }
}

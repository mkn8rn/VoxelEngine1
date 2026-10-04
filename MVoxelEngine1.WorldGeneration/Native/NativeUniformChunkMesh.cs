namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeUniformChunkMesh
{
    internal static bool TryGetUniformBlock(scoped ref NativeGtrtSessionView session,
        int chunkX, int chunkY, int chunkZ, out ushort blockId)
    {
        blockId = 0;
        int materialized = session.FindMaterializedChunkIndex(chunkX, chunkY, chunkZ);
        if (materialized >= 0)
        {
            NativeMaterializedChunkRecord saved = session.MaterializedChunks[materialized];
            if (saved.StorageKind != NativeChunkStorageKind.UniformSections)
                return false;
            blockId = saved.UniformBlockId;
            return true;
        }
        int index = session.GetColumnIndex(chunkX, chunkZ);
        if (index < 0)
            return false;
        NativeColumnRecord column = session.Columns[index];
        if (column.State != NativeColumnState.Generated || column.GenerationEpoch != session.State.SessionEpoch ||
            column.SummaryComputed == 0 || column.ReplacementMode == 2)
            return false;
        NativeColumnSummary summary = session.ColumnSummaries[index];
        int bottom = checked(chunkY * session.ChunkSizeY);
        int top = checked(bottom + session.ChunkSizeY - 1);
        if (summary.HasMaterial == 0 || top < summary.MinimumMaterialStart || bottom > summary.MaximumMaterialEnd)
            return true;
        NativeTerrainMaterialSet materials = column.ReplacementMode == 1 ? column.ResolvedMaterials : session.Materials;
        if (summary.AllColumnsHaveStone != 0 && bottom >= summary.StoneStartMaximum && top <= summary.StoneEndMinimum)
            blockId = materials.Stone.Id;
        else if (summary.AllColumnsHaveSoil != 0 && bottom >= summary.SoilStartMaximum && top <= summary.SoilEndMinimum)
            blockId = materials.Soil.Id;
        else if (summary.AllColumnsHaveWater != 0 && bottom >= summary.WaterStartMaximum && top <= summary.WaterEndMinimum)
            blockId = materials.Water.Id;
        else
            return false;
        return true;
    }

    internal static bool TryEmit(scoped ref NativeGtrtSessionView session, int chunkIndex,
        Span<int> scratch, scoped ref NativeGeneratedFaceWriter writer, out bool handled)
    {
        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        handled = TryGetUniformBlock(ref session, chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, out ushort source);
        if (!handled || source == 0)
            return true;
        if (!session.TryGetBlockDescriptor(source, out NativeBlockDescriptor descriptor))
            return false;
        bool opaque = descriptor.HasFlag(NativeBlockFlags.Opaque);
        for (byte direction = 0; direction < 6; direction++)
        {
            int axis = direction / 2;
            int normal = (direction & 1) == 0 ? 0 : axis switch
            {
                0 => session.ChunkSizeX - 1, 1 => session.ChunkSizeY - 1, _ => session.ChunkSizeZ - 1
            };
            int uSize = axis == 0 ? session.ChunkSizeZ : session.ChunkSizeX;
            int vSize = axis == 1 ? session.ChunkSizeZ : session.ChunkSizeY;
            int delta = (direction & 1) == 0 ? -1 : 1;
            bool uniformNeighbor = TryGetUniformBlock(ref session,
                chunk.ChunkX + (axis == 0 ? delta : 0),
                chunk.ChunkY + (axis == 1 ? delta : 0),
                chunk.ChunkZ + (axis == 2 ? delta : 0), out ushort neighbor);
            if (uniformNeighbor)
            {
                if (!session.TryIsBlockOpaque(neighbor, out bool neighborOpaque))
                    return false;
                if (!NativeGeneratedTerrain.FaceVisible(opaque, source, neighborOpaque, neighbor))
                    continue;
                NativeVoxelMesh.GetAnchor(direction, normal, 0, 0, uSize, vSize, out int x, out int y, out int z);
                writer.EmitBlockRectangle(in descriptor, direction, x, y, z, uSize, vSize);
                continue;
            }
            Span<int> faces = scratch.Slice(0, checked(uSize * vSize));
            for (int v = 0; v < vSize; v++)
            for (int u = 0; u < uSize; u++)
            {
                int x = axis == 0 ? normal + delta : u;
                int y = axis == 1 ? normal + delta : v;
                int z = axis == 2 ? normal + delta : axis == 0 ? u : v;
                if (!NativeGeneratedTerrain.TryGetBlock(ref session, chunkIndex, x, y, z, out neighbor) ||
                    !session.TryIsBlockOpaque(neighbor, out bool neighborOpaque))
                    return false;
                faces[v * uSize + u] = NativeGeneratedTerrain.FaceVisible(opaque, source, neighborOpaque, neighbor)
                    ? source : 0;
            }
            if (!NativeVoxelMesh.TryEmitMask(ref session, faces, uSize, vSize, direction, normal, ref writer))
                return false;
        }
        return writer.Valid;
    }
}

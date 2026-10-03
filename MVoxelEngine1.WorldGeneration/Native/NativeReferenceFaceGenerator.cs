using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;

// Diagnostic authority used after GTRT. It reads terrain directly and never
// uses optimized mesh output to decide which voxel faces exist.
internal static class NativeReferenceFaceGenerator
{
    internal static (int X, int Y, int Z) Normal(byte direction) => direction switch
    {
        0 => (-1, 0, 0),
        1 => (1, 0, 0),
        2 => (0, -1, 0),
        3 => (0, 1, 0),
        4 => (0, 0, -1),
        5 => (0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    internal static bool IsOpaque(ref NativeGtrtSessionView view, ushort id) =>
        id != 0 && view.TryGetBlockDescriptor(id, out NativeBlockDescriptor block) &&
        (block.Flags & NativeBlockFlags.Opaque) != 0;

    internal static bool Visible(ref NativeGtrtSessionView view, ushort source, ushort neighbor) =>
        source != 0 && (IsOpaque(ref view, source)
            ? !IsOpaque(ref view, neighbor)
            : source != neighbor && !IsOpaque(ref view, neighbor));

    internal static ushort GetBlock(
        ref NativeGtrtSessionView view, int chunkIndex, int x, int y, int z)
    {
        if (!NativeGeneratedTerrain.TryGetBlock(ref view, chunkIndex, x, y, z, out ushort block))
            throw new InvalidDataException("Native terrain is incomplete for face validation.");
        return block;
    }

    internal static List<CanonicalRenderFace> Generate(ref NativeGtrtSessionView view, int index)
    {
        NativeChunkRecord chunk = view.Chunks[index];
        var faces = new List<CanonicalRenderFace>();
        if (TryUniform(ref view, chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, out ushort uniform))
        {
            if (uniform == 0)
                return faces;
            for (byte direction = 0; direction < 6; direction++)
            {
                (int nx, int ny, int nz) = Normal(direction);
                if (TryUniform(ref view, chunk.ChunkX + nx, chunk.ChunkY + ny,
                        chunk.ChunkZ + nz, out ushort neighbor) &&
                    !Visible(ref view, uniform, neighbor))
                    continue;
                int width = direction < 2 ? view.ChunkSizeZ : view.ChunkSizeX;
                int height = direction is 2 or 3 ? view.ChunkSizeZ : view.ChunkSizeY;
                for (int u = 0; u < width; u++)
                for (int v = 0; v < height; v++)
                {
                    int x = direction switch { 0 => 0, 1 => view.ChunkSizeX - 1, _ => u };
                    int y = direction switch { 2 => 0, 3 => view.ChunkSizeY - 1, _ => v };
                    int z = direction switch { 4 => 0, 5 => view.ChunkSizeZ - 1,
                        0 or 1 => u, _ => v };
                    Emit(ref view, index, x, y, z, direction, uniform, faces);
                }
            }
            return faces;
        }

        for (int x = 0; x < view.ChunkSizeX; x++)
        for (int y = 0; y < view.ChunkSizeY; y++)
        for (int z = 0; z < view.ChunkSizeZ; z++)
        {
            ushort source = GetBlock(ref view, index, x, y, z);
            if (source == 0)
                continue;
            for (byte direction = 0; direction < 6; direction++)
                Emit(ref view, index, x, y, z, direction, source, faces);
        }
        return faces;
    }

    private static void Emit(ref NativeGtrtSessionView view, int index, int x, int y,
        int z, byte direction, ushort source, List<CanonicalRenderFace> faces)
    {
        (int dx, int dy, int dz) = Normal(direction);
        ushort neighbor = GetBlock(ref view, index, x + dx, y + dy, z + dz);
        if (!Visible(ref view, source, neighbor))
            return;
        NativeChunkRecord chunk = view.Chunks[index];
        faces.Add(new CanonicalRenderFace(
            checked(chunk.ChunkX * view.ChunkSizeX + x),
            checked(chunk.ChunkY * view.ChunkSizeY + y),
            checked(chunk.ChunkZ * view.ChunkSizeZ + z),
            direction, IsOpaque(ref view, source) ? CanonicalRenderPass.Opaque : CanonicalRenderPass.Transparent,
            source, neighbor));
    }

    private static bool TryUniform(ref NativeGtrtSessionView view, int x, int y, int z, out ushort id)
    {
        id = 0;
        int materialized = view.FindMaterializedChunkIndex(x, y, z);
        if (materialized >= 0)
        {
            NativeMaterializedChunkRecord chunk = view.MaterializedChunks[materialized];
            if (chunk.StorageKind != NativeChunkStorageKind.UniformSections)
                return false;
            id = chunk.UniformBlockId;
            return true;
        }
        int column = view.GetColumnIndex(x, z);
        if (column < 0)
            return false;
        NativeColumnRecord generated = view.Columns[column];
        if (generated.ReplacementMode == 2)
            return false;
        NativeTerrainMaterialSet materials = generated.ReplacementMode == 1
            ? generated.ResolvedMaterials : view.Materials;
        NativeColumnSummary summary = view.ColumnSummaries[column];
        int bottom = checked(y * view.ChunkSizeY);
        int top = checked(bottom + view.ChunkSizeY - 1);
        if (summary.HasMaterial == 0 || top < summary.MinimumMaterialStart || bottom > summary.MaximumMaterialEnd)
            return true;
        if (summary.AllColumnsHaveStone != 0 && bottom >= summary.StoneStartMaximum && top <= summary.StoneEndMinimum)
            id = materials.Stone.Id;
        else if (summary.AllColumnsHaveSoil != 0 && bottom >= summary.SoilStartMaximum && top <= summary.SoilEndMinimum)
            id = materials.Soil.Id;
        else if (summary.AllColumnsHaveWater != 0 && bottom >= summary.WaterStartMaximum && top <= summary.WaterEndMinimum)
            id = materials.Water.Id;
        else
            return false;
        return true;
    }
}

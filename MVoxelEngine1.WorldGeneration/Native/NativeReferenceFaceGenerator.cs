using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;

// Diagnostic authority used after GTRT. It reads terrain directly and never
// uses optimized mesh output to decide which voxel faces exist.
internal sealed class NativeReferenceFaceGenerator
{
    private readonly ReferenceColumn[] columns;
    private readonly bool[] opaque;

    internal NativeReferenceFaceGenerator(ref NativeGtrtSessionView view)
    {
        var game = view.GameSnapshot;
        opaque = new bool[game.Blocks.Length];
        for (int i = 0; i < opaque.Length; i++)
            opaque[i] = (game.Blocks[i].Flags & NativeBlockFlags.Opaque) != 0;
        columns = new ReferenceColumn[view.Columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            NativeColumnRecord column = view.Columns[i];
            if (column.State != NativeColumnState.Generated || column.GenerationEpoch != view.State.SessionEpoch)
                continue;
            NativeBiomeDescriptor biome = game.Biomes[column.BiomeIndex];
            bool constant = true;
            foreach (NativeReplacementRule rule in game.ReplacementRules.Slice(biome.ReplacementRuleOffset, biome.ReplacementRuleCount))
                constant &= rule.MinY == int.MinValue && rule.MaxY == int.MaxValue &&
                    rule.RelativeMinDepth == int.MinValue && rule.RelativeMaxDepth == int.MaxValue;
            constant &= ApplyRules(ref game, in biome, 0, 0, 0) == 0;
            int minimum = int.MaxValue, maximum = int.MinValue;
            foreach (BlockColumnProfile profile in view.GetColumnProfiles(i))
            {
                Include(profile.StoneStart, profile.StoneEnd, ref minimum, ref maximum);
                Include(profile.SoilStart, profile.SoilEnd, ref minimum, ref maximum);
                Include(profile.WaterStart, profile.WaterEnd, ref minimum, ref maximum);
            }
            columns[i] = new ReferenceColumn(true, constant, biome,
                ApplyRules(ref game, in biome, view.Materials.Stone.Id, 0, 0),
                ApplyRules(ref game, in biome, view.Materials.Soil.Id, 0, 0),
                ApplyRules(ref game, in biome, view.Materials.Water.Id, 0, 0), minimum, maximum);
        }
    }

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

    internal bool IsOpaque(ushort id) => id != 0 && opaque[id];

    internal bool Visible(ushort source, ushort neighbor) =>
        source != 0 && source != neighbor && !IsOpaque(neighbor);

    internal ushort GetBlock(
        ref NativeGtrtSessionView view, int chunkIndex, int x, int y, int z)
    {
        NativeChunkRecord source = view.Chunks[chunkIndex];
        int chunkX = checked(source.ChunkX + Normalize(ref x, view.ChunkSizeX));
        int chunkY = checked(source.ChunkY + Normalize(ref y, view.ChunkSizeY));
        int chunkZ = checked(source.ChunkZ + Normalize(ref z, view.ChunkSizeZ));
        if (view.State.MaterializedChunkCount != 0)
        {
            if (!NativeMaterializedTerrain.TryGetBlockAtWorldChunk(ref view, chunkX, chunkY, chunkZ,
                    x, y, z, out ushort saved, out bool handled))
                throw new InvalidDataException("Native saved terrain is incomplete for face validation.");
            if (handled)
                return saved;
        }
        int columnIndex = view.GetColumnIndex(chunkX, chunkZ);
        if (columnIndex < 0 || !columns[columnIndex].Generated)
            throw new InvalidDataException("Native terrain is incomplete for face validation.");
        ReferenceColumn column = columns[columnIndex];
        BlockColumnProfile profile = view.GetColumnProfiles(columnIndex)[x * view.ChunkSizeZ + z];
        int worldY = checked(chunkY * view.ChunkSizeY + y);
        if (column.Constant)
            return ProfileBlock(in profile, worldY, in column);
        ReferenceColumn original = new(true, true, default, view.Materials.Stone.Id,
            view.Materials.Soil.Id, view.Materials.Water.Id, 0, 0);
        ushort block = ProfileBlock(in profile, worldY, in original);
        var game = view.GameSnapshot;
        return ApplyRules(ref game, column.Biome, block, worldY, Math.Max(profile.StoneEnd, profile.SoilEnd));
    }

    internal List<CanonicalRenderFace> Generate(ref NativeGtrtSessionView view, int index)
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
                    !Visible(uniform, neighbor))
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

        if (CanReadProfiles(ref view, in chunk))
        {
            GenerateFromProfiles(ref view, index, faces);
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

    private void Emit(ref NativeGtrtSessionView view, int index, int x, int y,
        int z, byte direction, ushort source, List<CanonicalRenderFace> faces)
    {
        (int dx, int dy, int dz) = Normal(direction);
        ushort neighbor = GetBlock(ref view, index, x + dx, y + dy, z + dz);
        EmitKnown(ref view, index, x, y, z, direction, source, neighbor, faces);
    }

    private void EmitKnown(ref NativeGtrtSessionView view, int index, int x, int y,
        int z, byte direction, ushort source, ushort neighbor, List<CanonicalRenderFace> faces)
    {
        if (!Visible(source, neighbor))
            return;
        NativeChunkRecord chunk = view.Chunks[index];
        faces.Add(new CanonicalRenderFace(
            checked(chunk.ChunkX * view.ChunkSizeX + x),
            checked(chunk.ChunkY * view.ChunkSizeY + y),
            checked(chunk.ChunkZ * view.ChunkSizeZ + z),
            direction, IsOpaque(source) ? CanonicalRenderPass.Opaque : CanonicalRenderPass.Transparent,
            source, neighbor));
    }

    private bool TryUniform(ref NativeGtrtSessionView view, int x, int y, int z, out ushort id)
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
        ReferenceColumn generated = columns[column];
        if (!generated.Generated || !generated.Constant)
            return false;
        int bottom = checked(y * view.ChunkSizeY);
        int top = checked(bottom + view.ChunkSizeY - 1);
        if (top < generated.MinimumStart || bottom > generated.MaximumEnd)
            return true;
        bool first = true;
        foreach (BlockColumnProfile profile in view.GetColumnProfiles(column))
        {
            if (!UniformProfile(in profile, in generated, bottom, top, out ushort candidate))
                return false;
            if (!first && candidate != id)
                return false;
            id = candidate;
            first = false;
        }
        return true;
    }

    private bool CanReadProfiles(ref NativeGtrtSessionView view, ref readonly NativeChunkRecord chunk)
    {
        for (int direction = -1; direction < 6; direction++)
        {
            (int x, int y, int z) = direction < 0 ? (0, 0, 0) : Normal((byte)direction);
            if (view.FindMaterializedChunkIndex(chunk.ChunkX + x, chunk.ChunkY + y, chunk.ChunkZ + z) >= 0)
                return false;
            int column = view.GetColumnIndex(chunk.ChunkX + x, chunk.ChunkZ + z);
            if (column < 0 || !columns[column].Generated || !columns[column].Constant)
                return false;
        }
        return true;
    }

    private void GenerateFromProfiles(ref NativeGtrtSessionView view, int index, List<CanonicalRenderFace> faces)
    {
        NativeChunkRecord chunk = view.Chunks[index];
        ReferenceColumn own = columns[chunk.ColumnIndex];
        ReadOnlySpan<BlockColumnProfile> profiles = view.GetColumnProfiles(chunk.ColumnIndex);
        int bottom = checked(chunk.ChunkY * view.ChunkSizeY);
        int top = checked(bottom + view.ChunkSizeY - 1);
        for (int x = 0; x < view.ChunkSizeX; x++)
        for (int z = 0; z < view.ChunkSizeZ; z++)
        {
            BlockColumnProfile profile = profiles[x * view.ChunkSizeZ + z];
            int minimum = int.MaxValue, maximum = int.MinValue;
            Include(profile.StoneStart, profile.StoneEnd, ref minimum, ref maximum);
            Include(profile.SoilStart, profile.SoilEnd, ref minimum, ref maximum);
            Include(profile.WaterStart, profile.WaterEnd, ref minimum, ref maximum);
            int start = Math.Max(bottom, minimum), end = Math.Min(top, maximum);
            if (start > end)
                continue;
            GetAdjacentProfile(ref view, in chunk, x - 1, z, out BlockColumnProfile left, out ReferenceColumn leftColumn);
            GetAdjacentProfile(ref view, in chunk, x + 1, z, out BlockColumnProfile right, out ReferenceColumn rightColumn);
            GetAdjacentProfile(ref view, in chunk, x, z - 1, out BlockColumnProfile back, out ReferenceColumn backColumn);
            GetAdjacentProfile(ref view, in chunk, x, z + 1, out BlockColumnProfile front, out ReferenceColumn frontColumn);
            for (int worldY = start; worldY <= end; worldY++)
            {
                ushort source = ProfileBlock(in profile, worldY, in own);
                if (source == 0)
                    continue;
                int y = worldY - bottom;
                EmitKnown(ref view, index, x, y, z, 0, source, ProfileBlock(in left, worldY, in leftColumn), faces);
                EmitKnown(ref view, index, x, y, z, 1, source, ProfileBlock(in right, worldY, in rightColumn), faces);
                EmitKnown(ref view, index, x, y, z, 2, source, ProfileBlock(in profile, worldY - 1, in own), faces);
                EmitKnown(ref view, index, x, y, z, 3, source, ProfileBlock(in profile, worldY + 1, in own), faces);
                EmitKnown(ref view, index, x, y, z, 4, source, ProfileBlock(in back, worldY, in backColumn), faces);
                EmitKnown(ref view, index, x, y, z, 5, source, ProfileBlock(in front, worldY, in frontColumn), faces);
            }
        }
    }

    private void GetAdjacentProfile(ref NativeGtrtSessionView view, ref readonly NativeChunkRecord chunk,
        int x, int z, out BlockColumnProfile profile, out ReferenceColumn column)
    {
        int chunkX = chunk.ChunkX + Normalize(ref x, view.ChunkSizeX);
        int chunkZ = chunk.ChunkZ + Normalize(ref z, view.ChunkSizeZ);
        int columnIndex = view.GetColumnIndex(chunkX, chunkZ);
        column = columns[columnIndex];
        profile = view.GetColumnProfiles(columnIndex)[x * view.ChunkSizeZ + z];
    }

    private static ushort ProfileBlock(ref readonly BlockColumnProfile profile, int worldY, in ReferenceColumn column)
    {
        if (Contains(profile.StoneStart, profile.StoneEnd, worldY))
            return column.StoneId;
        if (Contains(profile.SoilStart, profile.SoilEnd, worldY))
            return column.SoilId;
        return Contains(profile.WaterStart, profile.WaterEnd, worldY) ? column.WaterId : (ushort)0;
    }

    private static bool UniformProfile(ref readonly BlockColumnProfile profile, in ReferenceColumn column,
        int bottom, int top, out ushort id)
    {
        id = 0;
        bool stone = Overlaps(profile.StoneStart, profile.StoneEnd, bottom, top);
        bool soil = Overlaps(profile.SoilStart, profile.SoilEnd, bottom, top);
        bool water = Overlaps(profile.WaterStart, profile.WaterEnd, bottom, top);
        if (Contains(profile.StoneStart, profile.StoneEnd, bottom) && Contains(profile.StoneStart, profile.StoneEnd, top))
            id = column.StoneId;
        else if (!stone && Contains(profile.SoilStart, profile.SoilEnd, bottom) && Contains(profile.SoilStart, profile.SoilEnd, top))
            id = column.SoilId;
        else if (!stone && !soil && Contains(profile.WaterStart, profile.WaterEnd, bottom) && Contains(profile.WaterStart, profile.WaterEnd, top))
            id = column.WaterId;
        else if (stone || soil || water)
            return false;
        return true;
    }

    private static bool Contains(int start, int end, int y) => start >= 0 && end >= start && start <= y && y <= end;

    private static bool Overlaps(int start, int end, int bottom, int top) =>
        start >= 0 && end >= start && start <= top && end >= bottom;

    private static void Include(int start, int end, ref int minimum, ref int maximum)
    {
        if (start < 0 || end < start)
            return;
        minimum = Math.Min(minimum, start);
        maximum = Math.Max(maximum, end);
    }

    private static int Normalize(ref int coordinate, int size)
    {
        if ((uint)coordinate < (uint)size)
            return 0;
        int offset = Math.DivRem(coordinate, size, out int remainder);
        if (remainder < 0)
        {
            remainder += size;
            offset--;
        }
        coordinate = remainder;
        return offset;
    }

    private static ushort ApplyRules(ref NativeGameSnapshotView game, in NativeBiomeDescriptor biome,
        ushort original, int y, int surface)
    {
        ushort result = original;
        foreach (NativeReplacementRule rule in game.ReplacementRules.Slice(biome.ReplacementRuleOffset, biome.ReplacementRuleCount))
        {
            long depth = (long)surface - y;
            if (y < rule.MinY || y > rule.MaxY ||
                (rule.RelativeMinDepth != int.MinValue && depth < rule.RelativeMinDepth) ||
                (rule.RelativeMaxDepth != int.MaxValue && depth > rule.RelativeMaxDepth))
                continue;
            ushort candidate = rule.GenerationType == GenerationType.InlineReplacement ? original : result;
            bool matches = (rule.BaseTypeBitMask & (1u << game.Blocks[candidate].BaseType)) != 0;
            foreach (ushort id in game.SpecificBlockIds.Slice(rule.SpecificIdOffset, rule.SpecificIdCount))
                matches |= id == candidate;
            if (matches)
                result = rule.ReplacementId;
        }
        return result;
    }

    private readonly record struct ReferenceColumn(bool Generated, bool Constant, NativeBiomeDescriptor Biome,
        ushort StoneId, ushort SoilId, ushort WaterId, int MinimumStart, int MaximumEnd);
}

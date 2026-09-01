using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct NativeTerrainMaterialSet
{
    internal NativeTerrainMaterialSet(
        NativeBlockDescriptor stone,
        NativeBlockDescriptor soil,
        NativeBlockDescriptor water)
    {
        Validate(in stone, BaseBlockType.Stone);
        Validate(in soil, BaseBlockType.Soil);
        Validate(in water, BaseBlockType.Water);
        Stone = stone;
        Soil = soil;
        Water = water;
    }

    internal NativeBlockDescriptor Stone { get; }

    internal NativeBlockDescriptor Soil { get; }

    internal NativeBlockDescriptor Water { get; }

    internal static NativeTerrainMaterialSet Create(
        ReadOnlySpan<NativeBlockDescriptor> blocks) =>
        new(
            GetRequired(blocks, BaseBlockType.Stone),
            GetRequired(blocks, BaseBlockType.Soil),
            GetRequired(blocks, BaseBlockType.Water));

    internal static NativeTerrainMaterialSet CreateConventional() =>
        new(
            CreateConventionalDescriptor(
                BaseBlockType.Stone,
                BlockStateOfMatter.Solid,
                NativeBlockFlags.Opaque),
            CreateConventionalDescriptor(
                BaseBlockType.Soil,
                BlockStateOfMatter.Solid,
                NativeBlockFlags.Opaque),
            CreateConventionalDescriptor(
                BaseBlockType.Water,
                BlockStateOfMatter.Liquid,
                NativeBlockFlags.Transparent | NativeBlockFlags.Liquid));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort GetBlockWorld(
        scoped in BlockColumnProfile profile,
        int worldY)
    {
        if (profile.StoneStart >= 0 &&
            profile.StoneEnd >= profile.StoneStart &&
            worldY >= profile.StoneStart &&
            worldY <= profile.StoneEnd)
        {
            return Stone.Id;
        }

        if (profile.SoilStart >= 0 &&
            profile.SoilEnd >= profile.SoilStart &&
            worldY >= profile.SoilStart &&
            worldY <= profile.SoilEnd)
        {
            return Soil.Id;
        }

        if (profile.WaterStart >= 0 &&
            profile.WaterEnd >= profile.WaterStart &&
            worldY >= profile.WaterStart &&
            worldY <= profile.WaterEnd)
        {
            return Water.Id;
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryIsOpaque(ushort blockId, out bool opaque)
    {
        if (blockId == 0)
        {
            opaque = false;
            return true;
        }

        if (blockId == Stone.Id)
        {
            opaque = IsOpaque(Stone);
            return true;
        }

        if (blockId == Soil.Id)
        {
            opaque = IsOpaque(Soil);
            return true;
        }

        if (blockId == Water.Id)
        {
            opaque = IsOpaque(Water);
            return true;
        }

        opaque = false;
        return false;
    }

    private static NativeBlockDescriptor GetRequired(
        ReadOnlySpan<NativeBlockDescriptor> blocks,
        BaseBlockType baseType)
    {
        int blockId = (byte)baseType;
        if ((uint)blockId >= (uint)blocks.Length)
        {
            throw new InvalidDataException(
                $"The runtime game has no required {baseType} block.");
        }

        NativeBlockDescriptor descriptor = blocks[blockId];
        Validate(in descriptor, baseType);
        return descriptor;
    }

    private static void Validate(
        scoped in NativeBlockDescriptor descriptor,
        BaseBlockType baseType)
    {
        ushort requiredId = (byte)baseType;
        if (descriptor.Id != requiredId ||
            descriptor.BaseType != requiredId ||
            (descriptor.Flags & NativeBlockFlags.Defined) == 0)
        {
            throw new InvalidDataException(
                $"The runtime {baseType} block does not match id {requiredId}.");
        }
    }

    private static NativeBlockDescriptor CreateConventionalDescriptor(
        BaseBlockType baseType,
        BlockStateOfMatter state,
        NativeBlockFlags flags) =>
        new(
            (byte)baseType,
            baseType,
            state,
            NativeBlockFlags.Defined | flags,
            0,
            0,
            0,
            0,
            0,
            0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOpaque(NativeBlockDescriptor descriptor) =>
        (descriptor.Flags & NativeBlockFlags.Opaque) != 0;
}

internal static class NativeGeneratedTerrain
{
    internal static bool TryGetBlock(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        out ushort blockId)
    {
        if ((uint)chunkIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            blockId = 0;
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
            blockId = 0;
            return false;
        }

        ref NativeColumnRecord column = ref session.Columns[columnIndex];
        ref int columnState = ref Unsafe.As<NativeColumnState, int>(
            ref column.State);
        if (Volatile.Read(ref columnState) !=
                (int)NativeColumnState.Generated ||
            column.GenerationEpoch != session.State.SessionEpoch)
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            blockId = 0;
            return false;
        }

        int profileIndex = checked(
            column.ProfileOffset +
            normalizedX * session.ChunkSizeZ +
            normalizedZ);
        int worldY = unchecked(chunk.ChunkY * session.ChunkSizeY + localY);
        ref readonly BlockColumnProfile profile =
            ref session.Profiles[profileIndex];
        blockId = session.Materials.GetBlockWorld(in profile, worldY);
        return true;
    }

    internal static bool TryIsFaceVisible(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        byte direction,
        out bool visible)
    {
        if (!TryGetBlock(
                ref session,
                chunkIndex,
                localX,
                localY,
                localZ,
                out ushort sourceId))
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
            case 0: neighborX--; break;
            case 1: neighborX++; break;
            case 2: neighborY--; break;
            case 3: neighborY++; break;
            case 4: neighborZ--; break;
            case 5: neighborZ++; break;
            default:
                session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
                visible = false;
                return false;
        }

        if (!TryGetBlock(
                ref session,
                chunkIndex,
                neighborX,
                neighborY,
                neighborZ,
                out ushort neighborId) ||
            !session.Materials.TryIsOpaque(sourceId, out bool sourceOpaque) ||
            !session.Materials.TryIsOpaque(neighborId, out bool neighborOpaque))
        {
            session.Fail(NativeGtrtFailureCode.InvalidTerrainQuery);
            visible = false;
            return false;
        }

        visible = FaceVisible(
            sourceOpaque,
            sourceId,
            neighborOpaque,
            neighborId);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool FaceVisible(
        bool sourceOpaque,
        ushort sourceBlockId,
        bool neighborOpaque,
        ushort neighborBlockId)
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

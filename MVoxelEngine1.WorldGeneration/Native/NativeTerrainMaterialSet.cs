using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct NativeTerrainMaterialSet
{
    internal NativeTerrainMaterialSet(NativeBlockDescriptor stone, NativeBlockDescriptor soil, NativeBlockDescriptor water, bool resolved = false)
    {
        if (!resolved)
        {
            Validate(in stone, BaseBlockType.Stone);
            Validate(in soil, BaseBlockType.Soil);
            Validate(in water, BaseBlockType.Water);
        }

        Stone = stone;
        Soil = soil;
        Water = water;
    }

    internal NativeBlockDescriptor Stone { get; }
    internal NativeBlockDescriptor Soil { get; }
    internal NativeBlockDescriptor Water { get; }

    internal static NativeTerrainMaterialSet Create(ReadOnlySpan<NativeBlockDescriptor> blocks) => new(GetRequired(blocks, BaseBlockType.Stone), GetRequired(blocks, BaseBlockType.Soil), GetRequired(blocks, BaseBlockType.Water));
    internal static NativeTerrainMaterialSet CreateConventional() => new(CreateConventionalDescriptor(BaseBlockType.Stone, BlockStateOfMatter.Solid, NativeBlockFlags.Opaque), CreateConventionalDescriptor(BaseBlockType.Soil, BlockStateOfMatter.Solid, NativeBlockFlags.Opaque), CreateConventionalDescriptor(BaseBlockType.Water, BlockStateOfMatter.Liquid, NativeBlockFlags.Transparent | NativeBlockFlags.Liquid));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort GetBlockWorld(scoped ref readonly BlockColumnProfile profile, int worldY)
    {
        if (profile.StoneStart >= 0 && profile.StoneEnd >= profile.StoneStart && worldY >= profile.StoneStart && worldY <= profile.StoneEnd)
        {
            return Stone.Id;
        }

        if (profile.SoilStart >= 0 && profile.SoilEnd >= profile.SoilStart && worldY >= profile.SoilStart && worldY <= profile.SoilEnd)
        {
            return Soil.Id;
        }

        if (profile.WaterStart >= 0 && profile.WaterEnd >= profile.WaterStart && worldY >= profile.WaterStart && worldY <= profile.WaterEnd)
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

    private static NativeBlockDescriptor GetRequired(ReadOnlySpan<NativeBlockDescriptor> blocks, BaseBlockType baseType)
    {
        int blockId = (byte)baseType;
        if ((uint)blockId >= (uint)blocks.Length)
        {
            throw new InvalidDataException($"The runtime game has no required {baseType} block.");
        }

        NativeBlockDescriptor descriptor = blocks[blockId];
        Validate(in descriptor, baseType);
        return descriptor;
    }

    private static void Validate(scoped in NativeBlockDescriptor descriptor, BaseBlockType baseType)
    {
        ushort requiredId = (byte)baseType;
        if (descriptor.Id != requiredId || descriptor.BaseType != requiredId || !descriptor.HasFlag(NativeBlockFlags.Defined))
        {
            throw new InvalidDataException($"The runtime {baseType} block does not match id {requiredId}.");
        }
    }

    private static NativeBlockDescriptor CreateConventionalDescriptor(BaseBlockType baseType, BlockStateOfMatter state, NativeBlockFlags flags) => new((byte)baseType, baseType, state, NativeBlockFlags.Defined | flags, 0, 0, 0, 0, 0, 0);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOpaque(NativeBlockDescriptor descriptor) => descriptor.HasFlag(NativeBlockFlags.Opaque);
}

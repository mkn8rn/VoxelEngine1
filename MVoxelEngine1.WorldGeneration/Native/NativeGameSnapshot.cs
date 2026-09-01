using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

[Flags]
internal enum NativeBlockFlags : byte
{
    None = 0,
    Defined = 1 << 0,
    Opaque = 1 << 1,
    Transparent = 1 << 2,
    Liquid = 1 << 3
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct NativeBlockDescriptor
{
    internal NativeBlockDescriptor(
        ushort id,
        BaseBlockType baseType,
        BlockStateOfMatter stateOfMatter,
        NativeBlockFlags flags,
        ushort leftTile,
        ushort rightTile,
        ushort bottomTile,
        ushort topTile,
        ushort backTile,
        ushort frontTile)
    {
        Id = id;
        BaseType = (ushort)baseType;
        StateOfMatter = (byte)stateOfMatter;
        Flags = flags;
        LeftTile = leftTile;
        RightTile = rightTile;
        BottomTile = bottomTile;
        TopTile = topTile;
        BackTile = backTile;
        FrontTile = frontTile;
    }

    internal ushort Id { get; }

    internal ushort BaseType { get; }

    internal byte StateOfMatter { get; }

    internal NativeBlockFlags Flags { get; }

    internal ushort LeftTile { get; }

    internal ushort RightTile { get; }

    internal ushort BottomTile { get; }

    internal ushort TopTile { get; }

    internal ushort BackTile { get; }

    internal ushort FrontTile { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort GetTile(byte direction) => direction switch
    {
        0 => LeftTile,
        1 => RightTile,
        2 => BottomTile,
        3 => TopTile,
        4 => BackTile,
        5 => FrontTile,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeBiomeDescriptor
{
    internal NativeBiomeDescriptor(
        Biome source,
        int replacementRuleOffset,
        int replacementRuleCount)
    {
        Id = source.id;
        StoneMinY = source.stoneMinYLevel;
        StoneMaxY = source.stoneMaxYLevel;
        StoneMinDepth = source.stoneMinDepth;
        StoneMaxDepth = source.stoneMaxDepth;
        SoilMinY = source.soilMinYLevel;
        SoilMaxY = source.soilMaxYLevel;
        SoilMinDepth = source.soilMinDepth;
        SoilMaxDepth = source.soilMaxDepth;
        WaterLevel = source.waterLevel;
        ReplacementRuleOffset = replacementRuleOffset;
        ReplacementRuleCount = replacementRuleCount;
    }

    internal int Id { get; }

    internal int StoneMinY { get; }

    internal int StoneMaxY { get; }

    internal int StoneMinDepth { get; }

    internal int StoneMaxDepth { get; }

    internal int SoilMinY { get; }

    internal int SoilMaxY { get; }

    internal int SoilMinDepth { get; }

    internal int SoilMaxDepth { get; }

    internal int WaterLevel { get; }

    internal int ReplacementRuleOffset { get; }

    internal int ReplacementRuleCount { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeReplacementRule
{
    internal NativeReplacementRule(
        CompiledSimpleReplacementRule source,
        int specificIdOffset)
    {
        ReplacementId = source.ReplacementId;
        SpecificIdCount = checked((ushort)source.SpecificIdsSorted.Length);
        SpecificIdOffset = specificIdOffset;
        BaseTypeBitMask = source.BaseTypeBitMask;
        MinY = source.MinY;
        MaxY = source.MaxY;
        MicroBiomeId = source.MicroBiomeId ?? -1;
        Priority = source.Priority;
    }

    internal ushort ReplacementId { get; }

    internal ushort SpecificIdCount { get; }

    internal int SpecificIdOffset { get; }

    internal uint BaseTypeBitMask { get; }

    internal int MinY { get; }

    internal int MaxY { get; }

    internal int MicroBiomeId { get; }

    internal int Priority { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NativeGameSnapshotHeader
{
    internal const uint ExpectedMagic = 0x4D41474E;
    internal const int ExpectedVersion = 1;

    internal NativeGameSnapshotHeader(
        int totalByteCount,
        int blockOffset,
        int blockCount,
        int biomeOffset,
        int biomeCount,
        int replacementRuleOffset,
        int replacementRuleCount,
        int specificIdOffset,
        int specificIdCount)
    {
        Magic = ExpectedMagic;
        Version = ExpectedVersion;
        TotalByteCount = totalByteCount;
        BlockOffset = blockOffset;
        BlockCount = blockCount;
        BiomeOffset = biomeOffset;
        BiomeCount = biomeCount;
        ReplacementRuleOffset = replacementRuleOffset;
        ReplacementRuleCount = replacementRuleCount;
        SpecificIdOffset = specificIdOffset;
        SpecificIdCount = specificIdCount;
    }

    internal uint Magic { get; }

    internal int Version { get; }

    internal int TotalByteCount { get; }

    internal int BlockOffset { get; }

    internal int BlockCount { get; }

    internal int BiomeOffset { get; }

    internal int BiomeCount { get; }

    internal int ReplacementRuleOffset { get; }

    internal int ReplacementRuleCount { get; }

    internal int SpecificIdOffset { get; }

    internal int SpecificIdCount { get; }
}

internal readonly ref struct NativeGameSnapshotView
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly NativeGameSnapshotHeader header;

    internal NativeGameSnapshotView(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Unsafe.SizeOf<NativeGameSnapshotHeader>())
            throw new InvalidDataException("The native game snapshot header is incomplete.");

        header = MemoryMarshal.Read<NativeGameSnapshotHeader>(bytes);
        if (header.Magic != NativeGameSnapshotHeader.ExpectedMagic ||
            header.Version != NativeGameSnapshotHeader.ExpectedVersion ||
            header.TotalByteCount != bytes.Length)
        {
            throw new InvalidDataException("The native game snapshot header is invalid.");
        }

        this.bytes = bytes;
        ValidateRange<NativeBlockDescriptor>(header.BlockOffset, header.BlockCount);
        ValidateRange<NativeBiomeDescriptor>(header.BiomeOffset, header.BiomeCount);
        ValidateRange<NativeReplacementRule>(
            header.ReplacementRuleOffset,
            header.ReplacementRuleCount);
        ValidateRange<ushort>(header.SpecificIdOffset, header.SpecificIdCount);
    }

    internal ReadOnlySpan<NativeBlockDescriptor> Blocks =>
        ReadRange<NativeBlockDescriptor>(header.BlockOffset, header.BlockCount);

    internal ReadOnlySpan<NativeBiomeDescriptor> Biomes =>
        ReadRange<NativeBiomeDescriptor>(header.BiomeOffset, header.BiomeCount);

    internal ReadOnlySpan<NativeReplacementRule> ReplacementRules =>
        ReadRange<NativeReplacementRule>(
            header.ReplacementRuleOffset,
            header.ReplacementRuleCount);

    internal ReadOnlySpan<ushort> SpecificBlockIds =>
        ReadRange<ushort>(header.SpecificIdOffset, header.SpecificIdCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int SelectBiomeIndex(long seed, int chunkX, int chunkZ)
    {
        int count = header.BiomeCount;
        if (count == 0)
            throw new InvalidDataException("The native game snapshot has no biome.");
        if (count == 1)
            return 0;

        ulong x = (uint)chunkX;
        ulong z = (uint)chunkZ;
        ulong hash = (ulong)seed;
        hash ^= x + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
        hash ^= z + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
        return (int)(hash % (ulong)count);
    }

    private ReadOnlySpan<T> ReadRange<T>(int offset, int count)
        where T : unmanaged
    {
        int byteCount = checked(count * Unsafe.SizeOf<T>());
        return MemoryMarshal.Cast<byte, T>(bytes.Slice(offset, byteCount));
    }

    private void ValidateRange<T>(int offset, int count)
        where T : unmanaged
    {
        if (offset < 0 || count < 0)
            throw new InvalidDataException("A native game snapshot range is negative.");

        int end = checked(offset + checked(count * Unsafe.SizeOf<T>()));
        if (end > bytes.Length)
            throw new InvalidDataException("A native game snapshot range is outside its owner.");
    }
}

internal sealed class NativeGameSnapshot : IDisposable
{
    private NativeTransfer<byte>? storage;

    private NativeGameSnapshot(NativeTransfer<byte>? source)
    {
        try
        {
            storage = NativeTransfer<byte>.Move(ref source);
        }
        finally
        {
            source?.Dispose();
        }
    }

    internal static NativeGameSnapshot Create(BlockTextureAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        if (atlas.tilesX <= 0)
            throw new InvalidOperationException("The texture atlas is not initialized.");

        NativeBlockDescriptor[] blocks = BuildBlocks(atlas);
        BuildBiomes(
            out NativeBiomeDescriptor[] biomes,
            out NativeReplacementRule[] replacementRules,
            out ushort[] specificIds);

        int headerSize = Unsafe.SizeOf<NativeGameSnapshotHeader>();
        int blockOffset = Align(headerSize, 4);
        int biomeOffset = Align(
            checked(blockOffset + blocks.Length * Unsafe.SizeOf<NativeBlockDescriptor>()),
            4);
        int replacementRuleOffset = Align(
            checked(biomeOffset + biomes.Length * Unsafe.SizeOf<NativeBiomeDescriptor>()),
            4);
        int specificIdOffset = Align(
            checked(replacementRuleOffset +
                replacementRules.Length * Unsafe.SizeOf<NativeReplacementRule>()),
            2);
        int totalByteCount = checked(specificIdOffset + specificIds.Length * sizeof(ushort));
        var header = new NativeGameSnapshotHeader(
            totalByteCount,
            blockOffset,
            blocks.Length,
            biomeOffset,
            biomes.Length,
            replacementRuleOffset,
            replacementRules.Length,
            specificIdOffset,
            specificIds.Length);

        byte[] staging = new byte[totalByteCount];
        Span<byte> destination = staging;
        MemoryMarshal.Write(destination, in header);
        WriteRange(destination, blockOffset, blocks);
        WriteRange(destination, biomeOffset, biomes);
        WriteRange(destination, replacementRuleOffset, replacementRules);
        WriteRange(destination, specificIdOffset, specificIds);

        using NativeBuilder<byte> builder = new(preLease: totalByteCount);
        builder.Append(staging);
        NativeTransfer<byte>? transfer = null;
        try
        {
            transfer = builder.Complete();
            return new NativeGameSnapshot(
                NativeTransfer<byte>.Move(ref transfer));
        }
        finally
        {
            transfer?.Dispose();
        }
    }

    internal void Access(NativeLeaseAction<byte> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (storage is null)
            throw new ObjectDisposedException(nameof(NativeGameSnapshot));

        storage.Access(action);
    }

    public void Dispose()
    {
        if (storage is null)
            return;

        storage.Dispose();
        storage = null;
    }

    private static NativeBlockDescriptor[] BuildBlocks(BlockTextureAtlas atlas)
    {
        var result = new NativeBlockDescriptor[ushort.MaxValue + 1];
        foreach (BlockType block in TerrainLoader.allBlockTypeObjects)
        {
            if (!BlockTextureAtlas.blockTypeUVCoordinates.TryGetValue(
                    block.ID,
                    out Dictionary<Faces, ByteVector2>? faces))
            {
                throw new InvalidDataException(
                    $"The texture atlas has no entry for block {block.ID}.");
            }

            NativeBlockFlags flags = NativeBlockFlags.Defined;
            if (TerrainLoader.IsOpaque(block.ID))
                flags |= NativeBlockFlags.Opaque;
            if (block.IsTransparent)
                flags |= NativeBlockFlags.Transparent;
            if (TerrainLoader.IsLiquid(block.ID))
                flags |= NativeBlockFlags.Liquid;

            result[block.ID] = new NativeBlockDescriptor(
                block.ID,
                block.BaseType,
                block.StateOfMatter,
                flags,
                GetTile(atlas, faces, Faces.LEFT),
                GetTile(atlas, faces, Faces.RIGHT),
                GetTile(atlas, faces, Faces.BOTTOM),
                GetTile(atlas, faces, Faces.TOP),
                GetTile(atlas, faces, Faces.BACK),
                GetTile(atlas, faces, Faces.FRONT));
        }

        return result;
    }

    private static void BuildBiomes(
        out NativeBiomeDescriptor[] biomes,
        out NativeReplacementRule[] replacementRules,
        out ushort[] specificIds)
    {
        KeyValuePair<string, Biome>[] orderedBiomes = BiomeManager.Biomes
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nativeBiomes = new NativeBiomeDescriptor[orderedBiomes.Length];
        var nativeRules = new List<NativeReplacementRule>();
        var nativeSpecificIds = new List<ushort>();

        for (int biomeIndex = 0; biomeIndex < orderedBiomes.Length; biomeIndex++)
        {
            Biome biome = orderedBiomes[biomeIndex].Value;
            int firstRule = nativeRules.Count;
            foreach (CompiledSimpleReplacementRule rule in
                     biome.compiledSimpleReplacementRules)
            {
                int firstSpecificId = nativeSpecificIds.Count;
                nativeSpecificIds.AddRange(rule.SpecificIdsSorted);
                nativeRules.Add(new NativeReplacementRule(rule, firstSpecificId));
            }

            nativeBiomes[biomeIndex] = new NativeBiomeDescriptor(
                biome,
                firstRule,
                nativeRules.Count - firstRule);
        }

        biomes = nativeBiomes;
        replacementRules = nativeRules.ToArray();
        specificIds = nativeSpecificIds.ToArray();
    }

    private static ushort GetTile(
        BlockTextureAtlas atlas,
        IReadOnlyDictionary<Faces, ByteVector2> faces,
        Faces face)
    {
        if (!faces.TryGetValue(face, out ByteVector2 coordinates))
            throw new InvalidDataException($"The texture atlas has no {face} face.");

        uint tile = checked((uint)(coordinates.y * atlas.tilesX + coordinates.x));
        if (tile > ushort.MaxValue)
            throw new InvalidDataException("The texture tile index exceeds 16 bits.");
        return (ushort)tile;
    }

    private static int Align(int value, int alignment)
    {
        int mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }

    private static void WriteRange<T>(
        Span<byte> destination,
        int offset,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        int byteCount = checked(values.Length * Unsafe.SizeOf<T>());
        values.CopyTo(MemoryMarshal.Cast<byte, T>(
            destination.Slice(offset, byteCount)));
    }
}

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
internal sealed class NativeGameSnapshot : IDisposable
{
    private static readonly NativeLeaseFunc<byte, NativeTerrainMaterialSet> GeneratedMaterialsReader = ReadGeneratedMaterials;
    private static readonly NativeLeaseFunc<byte, byte[]> SnapshotCopyReader = CopySnapshot;
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
        BuildBiomes(blocks, out NativeBiomeDescriptor[] biomes, out NativeReplacementRule[] replacementRules, out ushort[] specificIds);
        int headerSize = Unsafe.SizeOf<NativeGameSnapshotHeader>();
        int blockOffset = Align(headerSize, 4);
        int biomeOffset = Align(checked(blockOffset + blocks.Length * Unsafe.SizeOf<NativeBlockDescriptor>()), 4);
        int replacementRuleOffset = Align(checked(biomeOffset + biomes.Length * Unsafe.SizeOf<NativeBiomeDescriptor>()), 4);
        int specificIdOffset = Align(checked(replacementRuleOffset + replacementRules.Length * Unsafe.SizeOf<NativeReplacementRule>()), 2);
        int totalByteCount = checked(specificIdOffset + specificIds.Length * sizeof(ushort));
        var header = new NativeGameSnapshotHeader(totalByteCount, blockOffset, blocks.Length, biomeOffset, biomes.Length, replacementRuleOffset, replacementRules.Length, specificIdOffset, specificIds.Length);
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
            return new NativeGameSnapshot(NativeTransfer<byte>.Move(ref transfer));
        }
        finally
        {
            transfer?.Dispose();
        }
    }

    internal void Access(NativeLeaseAction<byte> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(storage is null, this);
        storage.Access(action);
    }

    internal NativeTerrainMaterialSet GetGeneratedMaterials()
    {
        ObjectDisposedException.ThrowIf(storage is null, this);
        return storage.Read(GeneratedMaterialsReader);
    }

    internal byte[] CopyBytes()
    {
        ObjectDisposedException.ThrowIf(storage is null, this);
        return storage.Read(SnapshotCopyReader);
    }

    public void Dispose()
    {
        if (storage is null)
            return;
        storage.Dispose();
        storage = null;
    }

    private static NativeTerrainMaterialSet ReadGeneratedMaterials(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGameSnapshotView(owner.AsSpan());
        return view.GetGeneratedMaterials();
    }

    private static byte[] CopySnapshot(scoped NativeLeaseView<byte> owner) => owner.AsSpan().ToArray();
    private static NativeBlockDescriptor[] BuildBlocks(BlockTextureAtlas atlas)
    {
        var result = new NativeBlockDescriptor[ushort.MaxValue + 1];
        foreach (BlockType block in TerrainLoader.allBlockTypeObjects)
        {
            if (!BlockTextureAtlas.blockTypeUVCoordinates.TryGetValue(block.ID, out IDictionary<Faces, ByteVector2>? faces))
            {
                throw new InvalidDataException($"The texture atlas has no entry for block {block.ID}.");
            }

            NativeBlockFlags flags = NativeBlockFlags.Defined;
            if (TerrainLoader.IsOpaque(block.ID))
                flags |= NativeBlockFlags.Opaque;
            if (block.IsTransparent)
                flags |= NativeBlockFlags.Transparent;
            if (TerrainLoader.IsLiquid(block.ID))
                flags |= NativeBlockFlags.Liquid;
            result[block.ID] = new NativeBlockDescriptor(block.ID, block.BaseType, block.StateOfMatter, flags, GetTile(atlas, faces, Faces.LEFT), GetTile(atlas, faces, Faces.RIGHT), GetTile(atlas, faces, Faces.BOTTOM), GetTile(atlas, faces, Faces.TOP), GetTile(atlas, faces, Faces.BACK), GetTile(atlas, faces, Faces.FRONT));
        }

        return result;
    }

    private static void BuildBiomes(NativeBlockDescriptor[] blocks, out NativeBiomeDescriptor[] biomes, out NativeReplacementRule[] replacementRules, out ushort[] specificIds)
    {
        KeyValuePair<string, Biome>[] orderedBiomes = BiomeManager.Biomes.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var nativeBiomes = new NativeBiomeDescriptor[orderedBiomes.Length];
        var nativeRules = new List<NativeReplacementRule>();
        var nativeSpecificIds = new List<ushort>();
        for (int biomeIndex = 0; biomeIndex < orderedBiomes.Length; biomeIndex++)
        {
            Biome biome = orderedBiomes[biomeIndex].Value;
            int firstRule = nativeRules.Count;
            foreach (CompiledSimpleReplacementRule rule in biome.compiledSimpleReplacementRules)
            {
                if (rule.GenerationType is not (GenerationType.InlineReplacement or GenerationType.SimpleReplacement) || rule.MicroBiomeId is not null || rule.MinY > rule.MaxY || rule.RelativeMinDepth > rule.RelativeMaxDepth)
                    throw new InvalidDataException($"Biome '{biome.name}' has an unsupported replacement rule.");
                if (!blocks[rule.ReplacementId].HasFlag(NativeBlockFlags.Defined))
                    throw new InvalidDataException($"Replacement block '{rule.ReplacementId}' is undefined.");
                foreach (ref readonly ushort id in rule.SpecificIdsSorted)
                    if (!blocks[id].HasFlag(NativeBlockFlags.Defined))
                        throw new InvalidDataException($"Replacement source '{id}' is undefined.");
                int firstSpecificId = nativeSpecificIds.Count;
                nativeSpecificIds.AddRange(rule.SpecificIdsSorted);
                nativeRules.Add(new NativeReplacementRule(rule, firstSpecificId));
            }

            nativeBiomes[biomeIndex] = new NativeBiomeDescriptor(biome, firstRule, nativeRules.Count - firstRule);
        }

        biomes = nativeBiomes;
        replacementRules = nativeRules.ToArray();
        specificIds = nativeSpecificIds.ToArray();
    }

    private static ushort GetTile(BlockTextureAtlas atlas, IDictionary<Faces, ByteVector2> faces, Faces face)
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

    private static void WriteRange<T>(Span<byte> destination, int offset, ReadOnlySpan<T> values)
        where T : unmanaged
    {
        int byteCount = checked(values.Length * Unsafe.SizeOf<T>());
        values.CopyTo(MemoryMarshal.Cast<byte, T>(destination.Slice(offset, byteCount)));
    }
}

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
internal readonly ref struct NativeGameSnapshotView
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly NativeGameSnapshotHeader header;
    internal NativeGameSnapshotView(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Unsafe.SizeOf<NativeGameSnapshotHeader>())
            throw new InvalidDataException("The native game snapshot header is incomplete.");
        header = MemoryMarshal.Read<NativeGameSnapshotHeader>(bytes);
        if (header.Magic != NativeGameSnapshotHeader.ExpectedMagic || header.Version != NativeGameSnapshotHeader.ExpectedVersion || header.TotalByteCount != bytes.Length)
        {
            throw new InvalidDataException("The native game snapshot header is invalid.");
        }

        this.bytes = bytes;
        ValidateRange<NativeBlockDescriptor>(header.BlockOffset, header.BlockCount);
        ValidateRange<NativeBiomeDescriptor>(header.BiomeOffset, header.BiomeCount);
        ValidateRange<NativeReplacementRule>(header.ReplacementRuleOffset, header.ReplacementRuleCount);
        ValidateRange<ushort>(header.SpecificIdOffset, header.SpecificIdCount);
    }

    internal ReadOnlySpan<NativeBlockDescriptor> Blocks => ReadRange<NativeBlockDescriptor>(header.BlockOffset, header.BlockCount);
    internal ReadOnlySpan<NativeBiomeDescriptor> Biomes => ReadRange<NativeBiomeDescriptor>(header.BiomeOffset, header.BiomeCount);
    internal ReadOnlySpan<NativeReplacementRule> ReplacementRules => ReadRange<NativeReplacementRule>(header.ReplacementRuleOffset, header.ReplacementRuleCount);
    internal ReadOnlySpan<ushort> SpecificBlockIds => ReadRange<ushort>(header.SpecificIdOffset, header.SpecificIdCount);

    internal NativeTerrainMaterialSet GetGeneratedMaterials() => NativeTerrainMaterialSet.Create(Blocks);
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

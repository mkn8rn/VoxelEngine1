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
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct NativeBlockDescriptor
{
    internal NativeBlockDescriptor(ushort id, BaseBlockType baseType, BlockStateOfMatter stateOfMatter, NativeBlockFlags flags, ushort leftTile, ushort rightTile, ushort bottomTile, ushort topTile, ushort backTile, ushort frontTile)
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

    // .NET 10 Tier0 boxes both Enum.HasFlag operands. Compile this small
    // predicate optimized on its first call to preserve the cold GTRT boundary.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal bool HasFlag(NativeBlockFlags flag) => Flags.HasFlag(flag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort GetTile(byte direction) => direction switch
    {
        0 => LeftTile,
        1 => RightTile,
        2 => BottomTile,
        3 => TopTile,
        4 => BackTile,
        5 => FrontTile,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))};
}

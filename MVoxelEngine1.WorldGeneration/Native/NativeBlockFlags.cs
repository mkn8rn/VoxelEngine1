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

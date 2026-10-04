using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal enum NativeChunkState : int
{
    Empty = 0,
    Reserved = 1,
    Generated = 2,
    MeshReady = 3,
    PacketReady = 4,
    Active = 5,
    Retired = 6
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal enum NativeChunkStorageKind : int
{
    GeneratedProfile = 0,
    HybridSections = 1,
    MaterializedSections = 2,
    UniformSections = 3,
    DeferredSaved = 4
}

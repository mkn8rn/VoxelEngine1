using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal readonly record struct NativeSessionStorageInfo(NativeGtrtSessionHeader Header, NativeTerrainMaterialSet Materials);

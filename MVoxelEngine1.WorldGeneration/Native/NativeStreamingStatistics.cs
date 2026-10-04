using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;
internal readonly record struct NativeStreamingStatistics(int GeneratedColumns, int RetainedColumns, int MeshedChunks, int RetainedPackets);

using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct NativeMaterializedStorageRequirements(int ChunkCount, int SectionCount, int RawSectionCount, int PaletteCount = 0, int PackedWordCount = 0);

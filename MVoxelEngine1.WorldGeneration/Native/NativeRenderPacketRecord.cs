using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeRenderPacketRecord
{
    internal long RenderDataId;
    internal int ChunkIndex;
    internal int RegistryEpoch;
    internal int OpaqueWordOffset;
    internal int OpaqueWordCount;
    internal int OpaqueFaceCount;
    internal int TransparentWordOffset;
    internal int TransparentWordCount;
    internal int TransparentFaceCount;
    internal int PublicationEpoch;
    internal NativeRenderPacketState State;
}

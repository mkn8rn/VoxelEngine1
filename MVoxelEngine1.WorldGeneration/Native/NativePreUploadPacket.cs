using System.Runtime.ExceptionServices;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public readonly record struct NativePreUploadPacket
{
    internal NativePreUploadPacket(long renderDataId, int chunkX, int chunkY, int chunkZ, int opaqueFaceCount, int opaqueWordCount, int transparentFaceCount, int transparentWordCount)
    {
        RenderDataId = renderDataId;
        ChunkX = chunkX;
        ChunkY = chunkY;
        ChunkZ = chunkZ;
        OpaqueFaceCount = opaqueFaceCount;
        OpaqueWordCount = opaqueWordCount;
        TransparentFaceCount = transparentFaceCount;
        TransparentWordCount = transparentWordCount;
    }

    public long RenderDataId { get; }
    public int ChunkX { get; }
    public int ChunkY { get; }
    public int ChunkZ { get; }
    public int OpaqueFaceCount { get; }
    public int OpaqueRectangleCount => OpaqueWordCount / PackedFaceRectangle.WordsPerRectangle;
    public int OpaqueWordCount { get; }
    public int TransparentFaceCount { get; }
    public int TransparentRectangleCount => TransparentWordCount / PackedFaceRectangle.WordsPerRectangle;
    public int TransparentWordCount { get; }
}

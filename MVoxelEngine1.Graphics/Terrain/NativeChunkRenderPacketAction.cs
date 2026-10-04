namespace MVoxelEngine1.Graphics.Terrain;
public delegate void NativeChunkRenderPacketAction(in NativeChunkRenderPacketDescriptor descriptor, ReadOnlySpan<uint> opaqueWords, ReadOnlySpan<uint> transparentWords);

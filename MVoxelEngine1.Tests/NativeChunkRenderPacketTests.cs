using MVoxelEngine1.Graphics.Terrain;

namespace MVoxelEngine1.Tests;

public sealed class NativeChunkRenderPacketTests
{
    [Fact]
    public void DescriptorPreservesNativePacketIdentityAndCounts()
    {
        var descriptor = new NativeChunkRenderPacketDescriptor(
            renderDataId: 17,
            chunkWorldX: -32,
            chunkWorldY: 64,
            chunkWorldZ: 96,
            registryEpoch: 4,
            publicationEpoch: 9,
            opaqueFaceCount: 8,
            opaqueWordCount: 4,
            transparentFaceCount: 3,
            transparentWordCount: 2);

        Assert.Equal(17, descriptor.RenderDataId);
        Assert.Equal(-32, descriptor.ChunkWorldX);
        Assert.Equal(64, descriptor.ChunkWorldY);
        Assert.Equal(96, descriptor.ChunkWorldZ);
        Assert.Equal(4, descriptor.RegistryEpoch);
        Assert.Equal(9, descriptor.PublicationEpoch);
        Assert.Equal(2, descriptor.OpaqueRectangleCount);
        Assert.Equal(1, descriptor.TransparentRectangleCount);
        Assert.False(descriptor.IsEmpty);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    public void DescriptorRejectsInvalidFaceRanges(
        int faceCount,
        int wordCount)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new NativeChunkRenderPacketDescriptor(
                renderDataId: 1,
                chunkWorldX: 0,
                chunkWorldY: 0,
                chunkWorldZ: 0,
                registryEpoch: 1,
                publicationEpoch: 1,
                opaqueFaceCount: faceCount,
                opaqueWordCount: wordCount,
                transparentFaceCount: 0,
                transparentWordCount: 0));
    }
}

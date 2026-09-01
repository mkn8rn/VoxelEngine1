namespace MVoxelEngine1.Graphics.Terrain;

public readonly struct NativeChunkRenderPacketDescriptor
{
    public NativeChunkRenderPacketDescriptor(
        long renderDataId,
        int chunkWorldX,
        int chunkWorldY,
        int chunkWorldZ,
        int registryEpoch,
        int publicationEpoch,
        int opaqueFaceCount,
        int opaqueWordCount,
        int transparentFaceCount,
        int transparentWordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderDataId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryEpoch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(publicationEpoch);
        ValidatePass(opaqueFaceCount, opaqueWordCount, "opaque");
        ValidatePass(
            transparentFaceCount,
            transparentWordCount,
            "transparent");

        RenderDataId = renderDataId;
        ChunkWorldX = chunkWorldX;
        ChunkWorldY = chunkWorldY;
        ChunkWorldZ = chunkWorldZ;
        RegistryEpoch = registryEpoch;
        PublicationEpoch = publicationEpoch;
        OpaqueFaceCount = opaqueFaceCount;
        OpaqueWordCount = opaqueWordCount;
        TransparentFaceCount = transparentFaceCount;
        TransparentWordCount = transparentWordCount;
    }

    public long RenderDataId { get; }

    public int ChunkWorldX { get; }

    public int ChunkWorldY { get; }

    public int ChunkWorldZ { get; }

    public int RegistryEpoch { get; }

    public int PublicationEpoch { get; }

    public int OpaqueFaceCount { get; }

    public int OpaqueWordCount { get; }

    public int OpaqueRectangleCount =>
        OpaqueWordCount / PackedFaceRectangle.WordsPerRectangle;

    public int TransparentFaceCount { get; }

    public int TransparentWordCount { get; }

    public int TransparentRectangleCount =>
        TransparentWordCount / PackedFaceRectangle.WordsPerRectangle;

    public bool IsEmpty =>
        OpaqueWordCount == 0 && TransparentWordCount == 0;

    private static void ValidatePass(
        int faceCount,
        int wordCount,
        string passName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(faceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(wordCount);
        if (wordCount % PackedFaceRectangle.WordsPerRectangle != 0)
        {
            throw new ArgumentException(
                $"The {passName} word count is not a complete rectangle.");
        }

        int rectangleCount =
            wordCount / PackedFaceRectangle.WordsPerRectangle;
        if ((faceCount == 0) != (rectangleCount == 0) ||
            faceCount < rectangleCount)
        {
            throw new ArgumentException(
                $"The {passName} face and rectangle counts are invalid.");
        }
    }
}

public delegate void NativeChunkRenderPacketAction(
    in NativeChunkRenderPacketDescriptor descriptor,
    ReadOnlySpan<uint> opaqueWords,
    ReadOnlySpan<uint> transparentWords);

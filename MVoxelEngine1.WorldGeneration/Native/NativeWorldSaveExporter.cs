using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

internal sealed class NativeWorldSaveExporter
{
    private const int ActiveRecord = 1;
    private const ushort QuadVersion = 1;
    private const ushort ChunkVersion = 1;
    private static ReadOnlySpan<byte> QuadMagic => "MVQH"u8;
    private static ReadOnlySpan<byte> ChunkMagic => "MVCH"u8;
    private static ReadOnlySpan<byte> ChunkFooterMagic => "CMD"u8;

    private readonly NativeGtrtSession session;
    private readonly NativeLeaseAction<byte> saveAction;
    private string? pendingQuadsDirectory;
    private int savedBatchCount;

    internal NativeWorldSaveExporter(NativeGtrtSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.session = session;
        saveAction = SaveCore;
    }

    internal int SaveDirtyChunks(string quadsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quadsDirectory);
        string fullDirectory = Path.GetFullPath(quadsDirectory);
        Directory.CreateDirectory(fullDirectory);

        pendingQuadsDirectory = fullDirectory;
        savedBatchCount = 0;
        try
        {
            session.Access(saveAction);
            return savedBatchCount;
        }
        finally
        {
            pendingQuadsDirectory = null;
        }
    }

    private void SaveCore(scoped NativeLeaseView<byte> owner)
    {
        string quadsDirectory = pendingQuadsDirectory ??
            throw new InvalidOperationException(
                "The native world save directory is not set.");
        var view = new NativeGtrtSessionView(owner.AsSpan());
        ref NativeGtrtSessionState state = ref view.State;
        if (state.PublicationState != 1 ||
            state.FailureCode != 0 ||
            Volatile.Read(ref state.ClaimedGenerationCount) != 0 ||
            Volatile.Read(ref state.ClaimedMeshCount) != 0 ||
            Volatile.Read(ref state.PacketConsumerCount) != 0 ||
            Volatile.Read(ref state.ReadyPacketCount) != 0 ||
            Volatile.Read(ref state.DisposalState) != 0)
        {
            throw new InvalidOperationException(
                "Native world data is not idle for a save.");
        }

        int chunkCount = state.MaterializedChunkCount;
        if ((uint)chunkCount > (uint)view.MaterializedChunks.Length)
        {
            throw new InvalidDataException(
                "Native materialized chunk storage is invalid.");
        }

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            NativeMaterializedChunkRecord chunk =
                view.MaterializedChunks[chunkIndex];
            if (chunk.State != ActiveRecord ||
                chunk.Revision == chunk.PersistedRevision)
            {
                continue;
            }

            (int batchX, int batchZ) = Quadrant.GetBatchIndices(
                chunk.ChunkX,
                chunk.ChunkZ);
            bool handled = false;
            for (int earlierIndex = 0;
                 earlierIndex < chunkIndex;
                 earlierIndex++)
            {
                NativeMaterializedChunkRecord earlier =
                    view.MaterializedChunks[earlierIndex];
                if (earlier.State != ActiveRecord ||
                    earlier.Revision == earlier.PersistedRevision)
                {
                    continue;
                }

                (int earlierBatchX, int earlierBatchZ) =
                    Quadrant.GetBatchIndices(
                        earlier.ChunkX,
                        earlier.ChunkZ);
                if (earlierBatchX == batchX &&
                    earlierBatchZ == batchZ)
                {
                    handled = true;
                    break;
                }
            }

            if (handled)
                continue;

            WriteBatch(
                ref view,
                quadsDirectory,
                batchX,
                batchZ);
            savedBatchCount++;
        }
    }

    private static void WriteBatch(
        scoped ref NativeGtrtSessionView view,
        string quadsDirectory,
        int batchX,
        int batchZ)
    {
        int chunkCount = 0;
        int materializedCount = view.State.MaterializedChunkCount;
        for (int index = 0; index < materializedCount; index++)
        {
            NativeMaterializedChunkRecord chunk =
                view.MaterializedChunks[index];
            if (chunk.State != ActiveRecord)
                continue;
            (int currentBatchX, int currentBatchZ) =
                Quadrant.GetBatchIndices(chunk.ChunkX, chunk.ChunkZ);
            if (currentBatchX == batchX && currentBatchZ == batchZ)
                chunkCount++;
        }
        if (chunkCount == 0)
        {
            throw new InvalidDataException(
                "A dirty native quad has no materialized chunks.");
        }

        string finalPath = Path.Combine(
            quadsDirectory,
            $"quad{batchX}x{batchZ}.bin");
        string temporaryPath = Path.Combine(
            quadsDirectory,
            $".{Path.GetFileName(finalPath)}.{Path.GetRandomFileName()}.tmp");
        bool published = false;
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: 256 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(
                       stream,
                       System.Text.Encoding.UTF8,
                       leaveOpen: true))
            {
                writer.Write(QuadMagic);
                writer.Write(QuadVersion);
                writer.Write((ushort)0);
                writer.Write(batchX);
                writer.Write(batchZ);
                writer.Write(chunkCount);

                for (int index = 0; index < materializedCount; index++)
                {
                    NativeMaterializedChunkRecord chunk =
                        view.MaterializedChunks[index];
                    if (chunk.State != ActiveRecord)
                        continue;
                    (int currentBatchX, int currentBatchZ) =
                        Quadrant.GetBatchIndices(
                            chunk.ChunkX,
                            chunk.ChunkZ);
                    if (currentBatchX != batchX ||
                        currentBatchZ != batchZ)
                    {
                        continue;
                    }

                    writer.Write(chunk.ChunkX);
                    writer.Write(chunk.ChunkY);
                    writer.Write(chunk.ChunkZ);
                    long payloadLengthPosition = stream.Position;
                    writer.Write(0);
                    long payloadStart = stream.Position;
                    WriteChunk(ref view, writer, index, in chunk);
                    long payloadEnd = stream.Position;
                    int payloadLength = checked((int)(payloadEnd - payloadStart));
                    stream.Position = payloadLengthPosition;
                    writer.Write(payloadLength);
                    stream.Position = payloadEnd;
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(finalPath))
                File.Replace(temporaryPath, finalPath, null);
            else
                File.Move(temporaryPath, finalPath);
            published = true;

            for (int index = 0; index < materializedCount; index++)
            {
                ref NativeMaterializedChunkRecord chunk =
                    ref view.MaterializedChunks[index];
                if (chunk.State != ActiveRecord)
                    continue;
                (int currentBatchX, int currentBatchZ) =
                    Quadrant.GetBatchIndices(
                        chunk.ChunkX,
                        chunk.ChunkZ);
                if (currentBatchX == batchX && currentBatchZ == batchZ)
                    chunk.PersistedRevision = chunk.Revision;
            }
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure = null;
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            if (cleanupFailure is not null)
                throw new AggregateException(failure, cleanupFailure);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            if (published && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void WriteChunk(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        int materializedChunkIndex,
        scoped in NativeMaterializedChunkRecord chunk)
    {
        if (chunk.StorageKind == NativeChunkStorageKind.HybridSections)
        {
            throw new InvalidDataException(
                "A hybrid native chunk must be materialized before save.");
        }

        Stream stream = writer.BaseStream;
        long chunkStart = stream.Position;
        writer.Write(ChunkMagic);
        writer.Write(ChunkVersion);
        writer.Write((ushort)0);
        writer.Write(chunk.ChunkX);
        writer.Write(chunk.ChunkY);
        writer.Write(chunk.ChunkZ);
        writer.Write(view.SectionCountX);
        writer.Write(view.SectionCountY);
        writer.Write(view.SectionCountZ);
        writer.Write(view.SectionsPerChunk);

        long offsetTablePosition = stream.Position;
        for (int index = 0; index < view.SectionsPerChunk; index++)
            writer.Write(0u);

        Span<uint> offsets = stackalloc uint[view.SectionsPerChunk];
        offsets.Clear();
        for (int sectionIndex = 0;
             sectionIndex < view.SectionsPerChunk;
             sectionIndex++)
        {
            long sectionPosition = stream.Position;
            if (WriteSection(
                    ref view,
                    writer,
                    materializedChunkIndex,
                    in chunk,
                    sectionIndex))
            {
                offsets[sectionIndex] = checked(
                    (uint)(sectionPosition - chunkStart));
            }
        }

        long footerPosition = stream.Position;
        stream.Position = offsetTablePosition;
        for (int index = 0; index < offsets.Length; index++)
            writer.Write(offsets[index]);
        stream.Position = footerPosition;
        WriteFooter(
            ref view,
            writer,
            materializedChunkIndex,
            in chunk);
    }

    private static bool WriteSection(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        int materializedChunkIndex,
        scoped in NativeMaterializedChunkRecord chunk,
        int sectionIndex)
    {
        int mapIndex = checked(chunk.SectionMapOffset + sectionIndex);
        if ((uint)mapIndex >= (uint)view.MaterializedSectionMaps.Length)
        {
            throw new InvalidDataException(
                "A native materialized section map is invalid.");
        }

        int recordIndex = view.MaterializedSectionMaps[mapIndex];
        if (recordIndex < 0)
        {
            if (chunk.StorageKind ==
                NativeChunkStorageKind.MaterializedSections)
            {
                return false;
            }
            if (chunk.StorageKind != NativeChunkStorageKind.UniformSections)
            {
                throw new InvalidDataException(
                    "A native materialized chunk has incomplete storage.");
            }
            if (chunk.UniformBlockId == 0)
                return false;

            return WriteUniformSection(
                ref view,
                writer,
                chunk.UniformBlockId);
        }

        if ((uint)recordIndex >=
            (uint)view.State.MaterializedSectionCount)
        {
            throw new InvalidDataException(
                "A native materialized section index is invalid.");
        }
        NativeMaterializedSectionRecord section =
            view.MaterializedSections[recordIndex];
        if (section.OwnerChunkIndex != materializedChunkIndex ||
            section.SectionIndex != sectionIndex)
        {
            throw new InvalidDataException(
                "A native materialized section owner is invalid.");
        }

        return section.StorageKind switch
        {
            NativeSectionStorageKind.Uniform =>
                section.UniformBlockId == 0
                    ? false
                    : WriteUniformSection(
                        ref view,
                        writer,
                        section.UniformBlockId),
            NativeSectionStorageKind.Raw =>
                WriteRawSection(ref view, writer, in section),
            NativeSectionStorageKind.Packed =>
                WritePackedSection(ref view, writer, in section),
            _ => throw new InvalidDataException(
                "A native materialized section kind is invalid.")
        };
    }

    private static bool WriteUniformSection(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        ushort blockId)
    {
        CountBlock(
            ref view,
            blockId,
            Section.VOXELS_PER_SECTION,
            out ushort opaqueCount,
            out ushort transparentCount,
            out ushort emptyCount);
        WriteSectionHeader(
            writer,
            kind: 1,
            payloadLength: 13,
            opaqueCount,
            transparentCount,
            emptyCount);
        writer.Write(blockId);
        return true;
    }

    private static bool WriteRawSection(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        scoped in NativeMaterializedSectionRecord section)
    {
        if (section.RawVoxelOffset < 0 ||
            section.RawVoxelOffset >
                view.MaterializedRawVoxels.Length -
                Section.VOXELS_PER_SECTION)
        {
            throw new InvalidDataException(
                "A native raw section range is invalid.");
        }
        ReadOnlySpan<ushort> raw = view.MaterializedRawVoxels.Slice(
            section.RawVoxelOffset,
            Section.VOXELS_PER_SECTION);
        CountBlocks(
            ref view,
            raw,
            out ushort opaqueCount,
            out ushort transparentCount,
            out ushort emptyCount);
        if (emptyCount == Section.VOXELS_PER_SECTION)
            return false;

        int payloadLength = checked(
            11 + Section.VOXELS_PER_SECTION * sizeof(ushort));
        WriteSectionHeader(
            writer,
            kind: 3,
            checked((ushort)payloadLength),
            opaqueCount,
            transparentCount,
            emptyCount);
        writer.Write(MemoryMarshal.AsBytes(raw));
        return true;
    }

    private static bool WritePackedSection(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        scoped in NativeMaterializedSectionRecord section)
    {
        if (section.BitsPerIndex is 0 or > 16 ||
            section.PaletteCount == 0 ||
            section.PaletteOffset < 0 ||
            section.PaletteOffset >
                view.MaterializedPalette.Length - section.PaletteCount ||
            section.PackedWordCount <= 0 ||
            section.PackedWordOffset < 0 ||
            section.PackedWordOffset >
                view.MaterializedPackedWords.Length -
                section.PackedWordCount)
        {
            throw new InvalidDataException(
                "A native packed section range is invalid.");
        }

        ReadOnlySpan<ushort> palette = view.MaterializedPalette.Slice(
            section.PaletteOffset,
            section.PaletteCount);
        ReadOnlySpan<uint> words = view.MaterializedPackedWords.Slice(
            section.PackedWordOffset,
            section.PackedWordCount);
        int opaque = 0;
        int transparent = 0;
        int empty = 0;
        for (int voxelIndex = 0;
             voxelIndex < Section.VOXELS_PER_SECTION;
             voxelIndex++)
        {
            int paletteIndex = ReadPackedIndex(
                words,
                section.BitsPerIndex,
                voxelIndex);
            if ((uint)paletteIndex >= (uint)palette.Length)
            {
                throw new InvalidDataException(
                    "A native packed section has an invalid palette index.");
            }
            CountBlock(
                ref view,
                palette[paletteIndex],
                1,
                ref opaque,
                ref transparent,
                ref empty);
        }
        if (empty == Section.VOXELS_PER_SECTION)
            return false;

        int payloadLength = checked(
            11 +
            sizeof(byte) +
            sizeof(ushort) +
            palette.Length * sizeof(ushort) +
            sizeof(int) +
            words.Length * sizeof(uint));
        WriteSectionHeader(
            writer,
            kind: 4,
            checked((ushort)payloadLength),
            checked((ushort)opaque),
            checked((ushort)transparent),
            checked((ushort)empty));
        writer.Write(section.BitsPerIndex);
        writer.Write(section.PaletteCount);
        writer.Write(MemoryMarshal.AsBytes(palette));
        writer.Write(words.Length);
        writer.Write(MemoryMarshal.AsBytes(words));
        return true;
    }

    private static void WriteSectionHeader(
        BinaryWriter writer,
        byte kind,
        ushort payloadLength,
        ushort opaqueCount,
        ushort transparentCount,
        ushort emptyCount)
    {
        writer.Write(kind);
        writer.Write(payloadLength);
        writer.Write(opaqueCount);
        writer.Write(transparentCount);
        writer.Write(emptyCount);
        writer.Write(0);
        byte flags = 1 << 3;
        if (emptyCount == 0)
            flags |= 1 << 2;
        if (emptyCount == Section.VOXELS_PER_SECTION)
            flags |= 1 << 4;
        writer.Write(flags);
    }

    private static void WriteFooter(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        int materializedChunkIndex,
        scoped in NativeMaterializedChunkRecord chunk)
    {
        writer.Write(ChunkFooterMagic);
        writer.Write(0f);
        writer.Write(0f);

        uint flags = 0;
        ushort allOneBlockId = 0;
        if (chunk.StorageKind == NativeChunkStorageKind.UniformSections &&
            HasNoSectionOverrides(ref view, in chunk))
        {
            allOneBlockId = chunk.UniformBlockId;
            if (allOneBlockId == 0)
            {
                flags |= 1u << 0;
            }
            else
            {
                flags |= 1u << 3;
                if (!view.TryGetBlockDescriptor(
                        allOneBlockId,
                        out NativeBlockDescriptor descriptor))
                {
                    throw new InvalidDataException(
                        "A native uniform chunk block is invalid.");
                }
                BaseBlockType baseType = (BaseBlockType)descriptor.BaseType;
                if (baseType == BaseBlockType.Stone)
                    flags |= 1u << 1;
                if (baseType == BaseBlockType.Soil)
                    flags |= 1u << 2;
                if (baseType == BaseBlockType.Water)
                    flags |= 1u << 6;
            }
        }
        writer.Write(flags);

        byte faceFlags = 0;
        for (byte direction = 0; direction < 6; direction++)
        {
            if (IsOpaqueFace(
                    ref view,
                    materializedChunkIndex,
                    direction))
            {
                faceFlags |= checked((byte)(1 << direction));
            }
        }
        writer.Write(faceFlags);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(allOneBlockId);

        for (byte direction = 0; direction < 6; direction++)
        {
            WriteOpaquePlane(
                ref view,
                writer,
                materializedChunkIndex,
                direction);
        }
        for (byte direction = 0; direction < 6; direction++)
        {
            WriteTransparentPlane(
                ref view,
                writer,
                materializedChunkIndex,
                direction);
        }
    }

    private static bool HasNoSectionOverrides(
        scoped ref NativeGtrtSessionView view,
        scoped in NativeMaterializedChunkRecord chunk)
    {
        if (chunk.SectionMapOffset < 0 ||
            chunk.SectionMapOffset >
                view.MaterializedSectionMaps.Length -
                view.SectionsPerChunk)
        {
            throw new InvalidDataException(
                "A native materialized section map is invalid.");
        }

        ReadOnlySpan<int> sectionMap =
            view.MaterializedSectionMaps.Slice(
                chunk.SectionMapOffset,
                view.SectionsPerChunk);
        for (int index = 0; index < sectionMap.Length; index++)
        {
            if (sectionMap[index] >= 0)
                return false;
        }
        return true;
    }

    private static bool IsOpaqueFace(
        scoped ref NativeGtrtSessionView view,
        int materializedChunkIndex,
        byte direction)
    {
        int cellCount = GetFaceCellCount(ref view, direction);
        for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            ushort blockId = GetFaceBlock(
                ref view,
                materializedChunkIndex,
                direction,
                cellIndex);
            if (!IsOpaque(ref view, blockId))
                return false;
        }
        return true;
    }

    private static void WriteOpaquePlane(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        int materializedChunkIndex,
        byte direction)
    {
        int cellCount = GetFaceCellCount(ref view, direction);
        int wordCount = checked((cellCount + 63) >> 6);
        writer.Write(wordCount);
        for (int wordIndex = 0; wordIndex < wordCount; wordIndex++)
        {
            ulong word = 0;
            int start = wordIndex << 6;
            int end = Math.Min(start + 64, cellCount);
            for (int cellIndex = start; cellIndex < end; cellIndex++)
            {
                ushort blockId = GetFaceBlock(
                    ref view,
                    materializedChunkIndex,
                    direction,
                    cellIndex);
                if (IsOpaque(ref view, blockId))
                    word |= 1UL << (cellIndex - start);
            }
            writer.Write(word);
        }
    }

    private static void WriteTransparentPlane(
        scoped ref NativeGtrtSessionView view,
        BinaryWriter writer,
        int materializedChunkIndex,
        byte direction)
    {
        int cellCount = GetFaceCellCount(ref view, direction);
        bool hasTransparent = false;
        for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            ushort blockId = GetFaceBlock(
                ref view,
                materializedChunkIndex,
                direction,
                cellIndex);
            if (IsTransparent(ref view, blockId))
            {
                hasTransparent = true;
                break;
            }
        }
        if (!hasTransparent)
        {
            writer.Write(0);
            return;
        }

        writer.Write(cellCount);
        for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            ushort blockId = GetFaceBlock(
                ref view,
                materializedChunkIndex,
                direction,
                cellIndex);
            writer.Write(IsTransparent(ref view, blockId)
                ? blockId
                : (ushort)0);
        }
    }

    private static int GetFaceCellCount(
        scoped ref NativeGtrtSessionView view,
        byte direction) => direction switch
    {
        0 or 1 => checked(view.ChunkSizeY * view.ChunkSizeZ),
        2 or 3 => checked(view.ChunkSizeX * view.ChunkSizeZ),
        4 or 5 => checked(view.ChunkSizeX * view.ChunkSizeY),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static ushort GetFaceBlock(
        scoped ref NativeGtrtSessionView view,
        int materializedChunkIndex,
        byte direction,
        int cellIndex)
    {
        int x;
        int y;
        int z;
        switch (direction)
        {
            case 0:
            case 1:
                x = direction == 0 ? 0 : view.ChunkSizeX - 1;
                y = cellIndex % view.ChunkSizeY;
                z = cellIndex / view.ChunkSizeY;
                break;
            case 2:
            case 3:
                x = cellIndex / view.ChunkSizeZ;
                y = direction == 2 ? 0 : view.ChunkSizeY - 1;
                z = cellIndex % view.ChunkSizeZ;
                break;
            case 4:
            case 5:
                x = cellIndex / view.ChunkSizeY;
                y = cellIndex % view.ChunkSizeY;
                z = direction == 4 ? 0 : view.ChunkSizeZ - 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!NativeMaterializedTerrain.TryGetStoredBlock(
                ref view,
                materializedChunkIndex,
                x,
                y,
                z,
                out ushort blockId))
        {
            throw new InvalidDataException(
                "A native boundary block could not be read.");
        }
        return blockId;
    }

    private static bool IsOpaque(
        scoped ref NativeGtrtSessionView view,
        ushort blockId)
    {
        if (blockId == 0)
            return false;
        if (!view.TryGetBlockDescriptor(
                blockId,
                out NativeBlockDescriptor descriptor))
        {
            throw new InvalidDataException(
                "A native saved block is not defined.");
        }
        return (descriptor.Flags & NativeBlockFlags.Opaque) != 0;
    }

    private static bool IsTransparent(
        scoped ref NativeGtrtSessionView view,
        ushort blockId) => blockId != 0 && !IsOpaque(ref view, blockId);

    private static void CountBlocks(
        scoped ref NativeGtrtSessionView view,
        scoped ReadOnlySpan<ushort> blocks,
        out ushort opaqueCount,
        out ushort transparentCount,
        out ushort emptyCount)
    {
        int opaque = 0;
        int transparent = 0;
        int empty = 0;
        for (int index = 0; index < blocks.Length; index++)
        {
            CountBlock(
                ref view,
                blocks[index],
                1,
                ref opaque,
                ref transparent,
                ref empty);
        }
        opaqueCount = checked((ushort)opaque);
        transparentCount = checked((ushort)transparent);
        emptyCount = checked((ushort)empty);
    }

    private static void CountBlock(
        scoped ref NativeGtrtSessionView view,
        ushort blockId,
        int count,
        out ushort opaqueCount,
        out ushort transparentCount,
        out ushort emptyCount)
    {
        int opaque = 0;
        int transparent = 0;
        int empty = 0;
        CountBlock(
            ref view,
            blockId,
            count,
            ref opaque,
            ref transparent,
            ref empty);
        opaqueCount = checked((ushort)opaque);
        transparentCount = checked((ushort)transparent);
        emptyCount = checked((ushort)empty);
    }

    private static void CountBlock(
        scoped ref NativeGtrtSessionView view,
        ushort blockId,
        int count,
        ref int opaqueCount,
        ref int transparentCount,
        ref int emptyCount)
    {
        if (blockId == 0)
        {
            emptyCount = checked(emptyCount + count);
            return;
        }
        if (IsOpaque(ref view, blockId))
            opaqueCount = checked(opaqueCount + count);
        else
            transparentCount = checked(transparentCount + count);
    }

    private static int ReadPackedIndex(
        scoped ReadOnlySpan<uint> words,
        int bitsPerIndex,
        int voxelIndex)
    {
        long bitPosition = (long)voxelIndex * bitsPerIndex;
        int wordIndex = checked((int)(bitPosition >> 5));
        int bitOffset = (int)(bitPosition & 31);
        if ((uint)wordIndex >= (uint)words.Length)
        {
            throw new InvalidDataException(
                "A native packed section has insufficient words.");
        }
        uint value = words[wordIndex] >> bitOffset;
        int remaining = 32 - bitOffset;
        if (remaining < bitsPerIndex)
        {
            if ((uint)(wordIndex + 1) >= (uint)words.Length)
            {
                throw new InvalidDataException(
                    "A native packed section ends inside an index.");
            }
            value |= words[wordIndex + 1] << remaining;
        }
        uint mask = (1u << bitsPerIndex) - 1u;
        return (int)(value & mask);
    }
}

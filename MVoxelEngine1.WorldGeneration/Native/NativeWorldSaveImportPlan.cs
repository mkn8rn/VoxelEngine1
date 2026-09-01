using System.Buffers.Binary;
using System.Security.Cryptography;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

internal sealed class NativeWorldSaveImportPlan
{
    private const ushort QuadVersion = 1;
    private const ushort ChunkVersion = 1;
    private const int QuadHeaderSize = 20;
    private const int ChunkRecordHeaderSize = 16;
    private const int ChunkHeaderSize = 36;
    private static ReadOnlySpan<byte> QuadMagic => "MVQH"u8;
    private static ReadOnlySpan<byte> ChunkMagic => "MVCH"u8;

    private readonly SavedFile[] files;
    private readonly NativeLeaseAction<byte> validateChunkAction;
    private readonly NativeLeaseAction<byte> importChunkAction;
    private readonly int sectionCountX;
    private readonly int sectionCountY;
    private readonly int sectionCountZ;
    private byte[]? pendingPayload;
    private int pendingChunkX;
    private int pendingChunkY;
    private int pendingChunkZ;
    private bool pendingImportSucceeded;
    private bool pendingValidationSucceeded;

    private NativeWorldSaveImportPlan(
        SavedFile[] files,
        int sectionCountX,
        int sectionCountY,
        int sectionCountZ,
        int chunkCount,
        int sectionCount,
        int rawSectionCount,
        int paletteCount,
        int packedWordCount)
    {
        this.files = files;
        this.sectionCountX = sectionCountX;
        this.sectionCountY = sectionCountY;
        this.sectionCountZ = sectionCountZ;
        ChunkCount = chunkCount;
        SectionCount = sectionCount;
        RawSectionCount = rawSectionCount;
        PaletteCount = paletteCount;
        PackedWordCount = packedWordCount;
        validateChunkAction = ValidatePendingChunk;
        importChunkAction = ImportPendingChunk;
    }

    internal int ChunkCount { get; }

    internal int SectionCount { get; }

    internal int RawSectionCount { get; }

    internal int PaletteCount { get; }

    internal int PackedWordCount { get; }

    internal bool HasSavedChunks => ChunkCount != 0;

    internal static NativeWorldSaveImportPlan Create(
        string quadsDirectory,
        GameSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quadsDirectory);
        ArgumentNullException.ThrowIfNull(settings);

        int sectionCountX = DivideRoundUp(
            settings.chunkMaxX,
            Section.SECTION_SIZE);
        int sectionCountY = DivideRoundUp(
            settings.chunkMaxY,
            Section.SECTION_SIZE);
        int sectionCountZ = DivideRoundUp(
            settings.chunkMaxZ,
            Section.SECTION_SIZE);
        if (!Directory.Exists(quadsDirectory))
        {
            return new NativeWorldSaveImportPlan(
                [],
                sectionCountX,
                sectionCountY,
                sectionCountZ,
                0,
                0,
                0,
                0,
                0);
        }

        string[] paths = Directory.GetFiles(
            quadsDirectory,
            "quad*x*.bin",
            SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.Ordinal);
        var files = new SavedFile[paths.Length];
        var chunkCoordinates = new HashSet<(int X, int Y, int Z)>();
        var totals = new ImportTotals();
        for (int index = 0; index < paths.Length; index++)
        {
            string path = paths[index];
            using var stream = OpenRead(path);
            ScanFile(
                stream,
                sectionCountX,
                sectionCountY,
                sectionCountZ,
                chunkCoordinates,
                ref totals,
                out int batchX,
                out int batchZ);
            stream.Position = 0;
            byte[] hash = SHA256.HashData(stream);
            files[index] = new SavedFile(path, batchX, batchZ, hash);
        }

        return new NativeWorldSaveImportPlan(
            files,
            sectionCountX,
            sectionCountY,
            sectionCountZ,
            totals.ChunkCount,
            totals.SectionCount,
            totals.RawSectionCount,
            totals.PaletteCount,
            totals.PackedWordCount);
    }

    internal void Import(NativeGtrtSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!HasSavedChunks)
            return;

        var streams = new FileStream[files.Length];
        try
        {
            for (int index = 0; index < files.Length; index++)
            {
                SavedFile file = files[index];
                FileStream stream = OpenRead(file.Path);
                streams[index] = stream;
                byte[] currentHash = SHA256.HashData(stream);
                if (!currentHash.AsSpan().SequenceEqual(file.Sha256))
                {
                    throw new InvalidDataException(
                        $"The saved quad changed during native import: {file.Path}");
                }
                stream.Position = 0;
            }

            for (int index = 0; index < streams.Length; index++)
            {
                ValidateFile(
                    streams[index],
                    files[index],
                    session);
                streams[index].Position = 0;
            }

            for (int index = 0; index < streams.Length; index++)
            {
                ImportFile(
                    streams[index],
                    files[index],
                    session);
            }
        }
        finally
        {
            pendingPayload = null;
            for (int index = streams.Length - 1; index >= 0; index--)
                streams[index]?.Dispose();
        }
    }

    private void ValidateFile(
        Stream stream,
        SavedFile expectedFile,
        NativeGtrtSession session)
    {
        using var reader = new BinaryReader(
            stream,
            System.Text.Encoding.UTF8,
            leaveOpen: true);
        int chunkCount = ReadQuadHeader(
            reader,
            out int batchX,
            out int batchZ);
        if (batchX != expectedFile.BatchX || batchZ != expectedFile.BatchZ)
        {
            throw new InvalidDataException(
                "The saved quad coordinates changed during validation.");
        }

        for (int index = 0; index < chunkCount; index++)
        {
            byte[] payload = ReadChunkRecord(
                reader,
                batchX,
                batchZ,
                out pendingChunkX,
                out pendingChunkY,
                out pendingChunkZ);
            pendingPayload = payload;
            pendingValidationSucceeded = false;
            try
            {
                session.Access(validateChunkAction);
                if (!pendingValidationSucceeded)
                {
                    throw new InvalidDataException(
                        $"The saved chunk ({pendingChunkX},{pendingChunkY},{pendingChunkZ}) uses invalid block data.");
                }
            }
            finally
            {
                pendingPayload = null;
            }
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "The saved quad changed during validation.");
        }
    }

    private static void ScanFile(
        Stream stream,
        int sectionCountX,
        int sectionCountY,
        int sectionCountZ,
        HashSet<(int X, int Y, int Z)> chunkCoordinates,
        ref ImportTotals totals,
        out int batchX,
        out int batchZ)
    {
        using var reader = new BinaryReader(
            stream,
            System.Text.Encoding.UTF8,
            leaveOpen: true);
        int chunkCount = ReadQuadHeader(
            reader,
            out batchX,
            out batchZ);
        for (int index = 0; index < chunkCount; index++)
        {
            byte[] payload = ReadChunkRecord(
                reader,
                batchX,
                batchZ,
                out int chunkX,
                out int chunkY,
                out int chunkZ);
            if (!chunkCoordinates.Add((chunkX, chunkY, chunkZ)))
            {
                throw new InvalidDataException(
                    $"The saved chunk ({chunkX},{chunkY},{chunkZ}) occurs more than once.");
            }

            ChunkShape shape = AnalyzeChunk(
                payload,
                chunkX,
                chunkY,
                chunkZ,
                sectionCountX,
                sectionCountY,
                sectionCountZ);
            totals.Add(in shape);
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "The saved quad has data after its declared chunk records.");
        }
    }

    private void ImportFile(
        Stream stream,
        SavedFile expectedFile,
        NativeGtrtSession session)
    {
        using var reader = new BinaryReader(
            stream,
            System.Text.Encoding.UTF8,
            leaveOpen: true);
        int chunkCount = ReadQuadHeader(
            reader,
            out int batchX,
            out int batchZ);
        if (batchX != expectedFile.BatchX || batchZ != expectedFile.BatchZ)
        {
            throw new InvalidDataException(
                "The saved quad coordinates changed during native import.");
        }

        for (int index = 0; index < chunkCount; index++)
        {
            byte[] payload = ReadChunkRecord(
                reader,
                batchX,
                batchZ,
                out pendingChunkX,
                out pendingChunkY,
                out pendingChunkZ);
            _ = AnalyzeChunk(
                payload,
                pendingChunkX,
                pendingChunkY,
                pendingChunkZ,
                sectionCountX,
                sectionCountY,
                sectionCountZ);

            pendingPayload = payload;
            pendingImportSucceeded = false;
            try
            {
                session.Access(importChunkAction);
                if (!pendingImportSucceeded)
                {
                    throw new InvalidDataException(
                        $"The saved chunk ({pendingChunkX},{pendingChunkY},{pendingChunkZ}) could not enter native storage.");
                }
            }
            finally
            {
                pendingPayload = null;
            }
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "The saved quad changed during native import.");
        }
    }

    private void ImportPendingChunk(scoped NativeLeaseView<byte> owner)
    {
        byte[] payload = pendingPayload ??
            throw new InvalidOperationException(
                "The native save payload is not available.");
        var session = new NativeGtrtSessionView(owner.AsSpan());
        ChunkShape shape = AnalyzeChunk(
            payload,
            pendingChunkX,
            pendingChunkY,
            pendingChunkZ,
            sectionCountX,
            sectionCountY,
            sectionCountZ);
        if (!NativeMaterializedTerrain.TryImportChunk(
                ref session,
                pendingChunkX,
                pendingChunkY,
                pendingChunkZ,
                shape.IsUniform && shape.UniformBlockId != 0,
                shape.UniformBlockId,
                out int materializedChunkIndex))
        {
            return;
        }
        if (shape.IsUniform)
        {
            pendingImportSucceeded = true;
            return;
        }

        int sectionCount = checked(
            sectionCountX * sectionCountY * sectionCountZ);
        int tableOffset = ChunkHeaderSize;
        for (int sectionIndex = 0;
             sectionIndex < sectionCount;
             sectionIndex++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(tableOffset + sectionIndex * sizeof(uint)));
            if (offset == 0)
                continue;

            SavedSection section = ParseSection(payload, checked((int)offset));
            bool imported = section.Kind switch
            {
                SavedSectionKind.Empty => true,
                SavedSectionKind.Uniform when section.UniformBlockId == 0 =>
                    true,
                SavedSectionKind.Uniform =>
                    NativeMaterializedTerrain.TryImportUniformSection(
                        ref session,
                        materializedChunkIndex,
                        sectionIndex,
                        section.UniformBlockId),
                SavedSectionKind.Raw => ImportRawSection(
                    ref session,
                    materializedChunkIndex,
                    sectionIndex,
                    payload,
                    in section),
                SavedSectionKind.Packed => ImportPackedSection(
                    ref session,
                    materializedChunkIndex,
                    sectionIndex,
                    payload,
                    in section),
                _ => false
            };
            if (!imported)
                return;
        }

        pendingImportSucceeded = true;
    }

    private void ValidatePendingChunk(scoped NativeLeaseView<byte> owner)
    {
        byte[] payload = pendingPayload ??
            throw new InvalidOperationException(
                "The native save validation payload is not available.");
        var session = new NativeGtrtSessionView(owner.AsSpan());
        ChunkShape shape = AnalyzeChunk(
            payload,
            pendingChunkX,
            pendingChunkY,
            pendingChunkZ,
            sectionCountX,
            sectionCountY,
            sectionCountZ);
        if (shape.IsUniform &&
            !session.TryGetBlockDescriptor(shape.UniformBlockId, out _))
        {
            return;
        }

        int sectionCount = checked(
            sectionCountX * sectionCountY * sectionCountZ);
        for (int sectionIndex = 0;
             sectionIndex < sectionCount;
             sectionIndex++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(
                    ChunkHeaderSize + sectionIndex * sizeof(uint)));
            if (offset == 0)
                continue;

            SavedSection section = ParseSection(payload, checked((int)offset));
            if (!ValidateSectionBlocks(ref session, payload, in section))
                return;
        }

        pendingValidationSucceeded = true;
    }

    private static bool ValidateSectionBlocks(
        scoped ref NativeGtrtSessionView session,
        byte[] payload,
        scoped in SavedSection section)
    {
        switch (section.Kind)
        {
            case SavedSectionKind.Empty:
                return true;
            case SavedSectionKind.Uniform:
                return session.TryGetBlockDescriptor(
                    section.UniformBlockId,
                    out _);
            case SavedSectionKind.Raw:
                ReadOnlySpan<byte> raw = payload.AsSpan(
                    section.DataOffset,
                    Section.VOXELS_PER_SECTION * sizeof(ushort));
                for (int index = 0;
                     index < Section.VOXELS_PER_SECTION;
                     index++)
                {
                    ushort blockId = BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.Slice(index * sizeof(ushort), sizeof(ushort)));
                    if (!session.TryGetBlockDescriptor(blockId, out _))
                        return false;
                }
                return true;
            case SavedSectionKind.Packed:
                Span<ushort> palette = stackalloc ushort[section.PaletteCount];
                ReadOnlySpan<byte> paletteBytes = payload.AsSpan(
                    section.PaletteOffset,
                    section.PaletteCount * sizeof(ushort));
                for (int index = 0; index < palette.Length; index++)
                {
                    palette[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                        paletteBytes.Slice(
                            index * sizeof(ushort),
                            sizeof(ushort)));
                    if (!session.TryGetBlockDescriptor(palette[index], out _))
                        return false;
                }

                ReadOnlySpan<byte> words = payload.AsSpan(
                    section.WordOffset,
                    section.WordCount * sizeof(uint));
                for (int voxelIndex = 0;
                     voxelIndex < Section.VOXELS_PER_SECTION;
                     voxelIndex++)
                {
                    int paletteIndex = ReadPackedIndex(
                        words,
                        section.BitsPerIndex,
                        voxelIndex);
                    if ((uint)paletteIndex >= (uint)palette.Length)
                        return false;
                }
                return true;
            default:
                return false;
        }
    }

    private static int ReadPackedIndex(
        ReadOnlySpan<byte> wordBytes,
        int bitsPerIndex,
        int voxelIndex)
    {
        long bitPosition = (long)voxelIndex * bitsPerIndex;
        int wordIndex = (int)(bitPosition >> 5);
        int bitOffset = (int)(bitPosition & 31);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(
            wordBytes.Slice(wordIndex * sizeof(uint), sizeof(uint))) >>
            bitOffset;
        int remaining = 32 - bitOffset;
        if (remaining < bitsPerIndex)
        {
            value |= BinaryPrimitives.ReadUInt32LittleEndian(
                wordBytes.Slice(
                    (wordIndex + 1) * sizeof(uint),
                    sizeof(uint))) << remaining;
        }
        uint mask = (1u << bitsPerIndex) - 1u;
        return (int)(value & mask);
    }

    private static bool ImportRawSection(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int sectionIndex,
        byte[] payload,
        scoped in SavedSection section)
    {
        Span<ushort> voxels = stackalloc ushort[Section.VOXELS_PER_SECTION];
        ReadOnlySpan<byte> bytes = payload.AsSpan(
            section.DataOffset,
            Section.VOXELS_PER_SECTION * sizeof(ushort));
        for (int index = 0; index < voxels.Length; index++)
        {
            voxels[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.Slice(index * sizeof(ushort), sizeof(ushort)));
        }
        return NativeMaterializedTerrain.TryImportRawSection(
            ref session,
            materializedChunkIndex,
            sectionIndex,
            voxels);
    }

    private static bool ImportPackedSection(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int sectionIndex,
        byte[] payload,
        scoped in SavedSection section)
    {
        Span<ushort> palette = stackalloc ushort[section.PaletteCount];
        ReadOnlySpan<byte> paletteBytes = payload.AsSpan(
            section.PaletteOffset,
            section.PaletteCount * sizeof(ushort));
        for (int index = 0; index < palette.Length; index++)
        {
            palette[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                paletteBytes.Slice(index * sizeof(ushort), sizeof(ushort)));
        }

        Span<uint> words = stackalloc uint[section.WordCount];
        ReadOnlySpan<byte> wordBytes = payload.AsSpan(
            section.WordOffset,
            section.WordCount * sizeof(uint));
        for (int index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                wordBytes.Slice(index * sizeof(uint), sizeof(uint)));
        }
        return NativeMaterializedTerrain.TryImportPackedSection(
            ref session,
            materializedChunkIndex,
            sectionIndex,
            section.BitsPerIndex,
            palette,
            words);
    }

    private static ChunkShape AnalyzeChunk(
        ReadOnlySpan<byte> payload,
        int chunkX,
        int chunkY,
        int chunkZ,
        int expectedSectionCountX,
        int expectedSectionCountY,
        int expectedSectionCountZ)
    {
        int expectedSectionCount = checked(
            expectedSectionCountX *
            expectedSectionCountY *
            expectedSectionCountZ);
        int tableSize = checked(expectedSectionCount * sizeof(uint));
        if (payload.Length < ChunkHeaderSize + tableSize ||
            !payload[..4].SequenceEqual(ChunkMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]) !=
                ChunkVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]) != chunkX ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[12..]) != chunkY ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[16..]) != chunkZ ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[20..]) !=
                expectedSectionCountX ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[24..]) !=
                expectedSectionCountY ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[28..]) !=
                expectedSectionCountZ ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[32..]) !=
                expectedSectionCount)
        {
            throw new InvalidDataException(
                $"The saved chunk ({chunkX},{chunkY},{chunkZ}) header is invalid.");
        }

        var shape = new ChunkShape
        {
            IsUniform = true
        };
        bool uniformIdSet = false;
        int recordsStart = checked(ChunkHeaderSize + tableSize);
        var usedOffsets = new HashSet<int>();
        for (int sectionIndex = 0;
             sectionIndex < expectedSectionCount;
             sectionIndex++)
        {
            int tableOffset = checked(
                ChunkHeaderSize + sectionIndex * sizeof(uint));
            uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.Slice(tableOffset, sizeof(uint)));
            SavedSection section;
            if (rawOffset == 0)
            {
                section = SavedSection.Empty;
            }
            else
            {
                int offset = checked((int)rawOffset);
                if (offset < recordsStart || !usedOffsets.Add(offset))
                {
                    throw new InvalidDataException(
                        $"The saved chunk ({chunkX},{chunkY},{chunkZ}) has an invalid section offset.");
                }
                section = ParseSection(payload, offset);
            }

            bool sectionIsUniform = section.Kind is
                SavedSectionKind.Empty or SavedSectionKind.Uniform;
            ushort sectionUniformId = section.Kind ==
                SavedSectionKind.Uniform
                ? section.UniformBlockId
                : (ushort)0;
            if (!sectionIsUniform)
            {
                shape.IsUniform = false;
            }
            else if (!uniformIdSet)
            {
                shape.UniformBlockId = sectionUniformId;
                uniformIdSet = true;
            }
            else if (shape.UniformBlockId != sectionUniformId)
            {
                shape.IsUniform = false;
            }

            switch (section.Kind)
            {
                case SavedSectionKind.Empty:
                    break;
                case SavedSectionKind.Uniform:
                    if (section.UniformBlockId != 0)
                        shape.SectionCount++;
                    break;
                case SavedSectionKind.Raw:
                    shape.SectionCount++;
                    shape.RawSectionCount++;
                    break;
                case SavedSectionKind.Packed:
                    shape.SectionCount++;
                    shape.PaletteCount = checked(
                        shape.PaletteCount + section.PaletteCount);
                    shape.PackedWordCount = checked(
                        shape.PackedWordCount + section.WordCount);
                    break;
            }
        }

        if (shape.IsUniform)
        {
            shape.SectionCount = 0;
            shape.RawSectionCount = 0;
            shape.PaletteCount = 0;
            shape.PackedWordCount = 0;
        }
        return shape;
    }

    private static SavedSection ParseSection(
        ReadOnlySpan<byte> chunk,
        int recordOffset)
    {
        if ((uint)recordOffset > (uint)(chunk.Length - 3))
            throw new InvalidDataException("A saved section record is incomplete.");

        byte kindValue = chunk[recordOffset];
        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(
            chunk.Slice(recordOffset + 1, sizeof(ushort)));
        int payloadOffset = checked(recordOffset + 3);
        if (payloadLength < 11 ||
            payloadOffset > chunk.Length - payloadLength)
        {
            throw new InvalidDataException("A saved section payload is invalid.");
        }

        ReadOnlySpan<byte> payload = chunk.Slice(
            payloadOffset,
            payloadLength);
        var reader = new SpanReader(payload);
        _ = reader.ReadUInt16();
        _ = reader.ReadUInt16();
        _ = reader.ReadUInt16();
        _ = reader.ReadInt32();
        byte flags = reader.ReadByte();
        if ((flags & 1) != 0)
            reader.Skip(6);
        if ((flags & 2) != 0)
        {
            for (int index = 0; index < 7; index++)
                reader.SkipUlongArray();
        }
        if ((flags & 32) != 0)
        {
            for (int index = 0; index < 7; index++)
                reader.SkipUlongArray();
        }
        if ((flags & 64) != 0)
            reader.SkipUlongArray();

        SavedSectionKind kind = kindValue switch
        {
            0 => SavedSectionKind.Empty,
            1 => SavedSectionKind.Uniform,
            2 or 3 => SavedSectionKind.Raw,
            4 or 5 => SavedSectionKind.Packed,
            _ => throw new InvalidDataException(
                $"The saved section kind {kindValue} is not supported.")
        };
        SavedSection section;
        switch (kind)
        {
            case SavedSectionKind.Empty:
                section = SavedSection.Empty;
                break;
            case SavedSectionKind.Uniform:
                section = new SavedSection(
                    kind,
                    reader.ReadUInt16(),
                    0,
                    0,
                    0,
                    0,
                    0,
                    0);
                break;
            case SavedSectionKind.Raw:
                int dataOffset = checked(payloadOffset + reader.Position);
                reader.Skip(Section.VOXELS_PER_SECTION * sizeof(ushort));
                section = new SavedSection(
                    kind,
                    0,
                    dataOffset,
                    0,
                    0,
                    0,
                    0,
                    0);
                break;
            case SavedSectionKind.Packed:
                byte bitsPerIndex = reader.ReadByte();
                int paletteCount = reader.ReadUInt16();
                if (bitsPerIndex is 0 or > 16 ||
                    paletteCount is <= 0 or > Section.VOXELS_PER_SECTION)
                {
                    throw new InvalidDataException(
                        "A saved packed section has invalid indexing metadata.");
                }
                int paletteOffset = checked(payloadOffset + reader.Position);
                reader.Skip(checked(paletteCount * sizeof(ushort)));
                int wordCount = reader.ReadInt32();
                int minimumWordCount = checked(
                    (Section.VOXELS_PER_SECTION * bitsPerIndex + 31) / 32);
                if (wordCount < minimumWordCount)
                {
                    throw new InvalidDataException(
                        "A saved packed section has insufficient word data.");
                }
                int wordOffset = checked(payloadOffset + reader.Position);
                reader.Skip(checked(wordCount * sizeof(uint)));
                section = new SavedSection(
                    kind,
                    0,
                    0,
                    bitsPerIndex,
                    paletteOffset,
                    paletteCount,
                    wordOffset,
                    wordCount);
                break;
            default:
                throw new InvalidDataException(
                    "A saved section kind is invalid.");
        }
        reader.RequireEnd();
        return section;
    }

    private static int ReadQuadHeader(
        BinaryReader reader,
        out int batchX,
        out int batchZ)
    {
        Span<byte> header = stackalloc byte[QuadHeaderSize];
        ReadExactly(reader.BaseStream, header);
        if (!header[..4].SequenceEqual(QuadMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) !=
                QuadVersion)
        {
            throw new InvalidDataException("The saved quad header is invalid.");
        }

        batchX = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        batchZ = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (chunkCount < 0 ||
            chunkCount >
                (reader.BaseStream.Length - reader.BaseStream.Position) /
                ChunkRecordHeaderSize)
        {
            throw new InvalidDataException(
                "The saved quad chunk count is invalid.");
        }
        return chunkCount;
    }

    private static byte[] ReadChunkRecord(
        BinaryReader reader,
        int batchX,
        int batchZ,
        out int chunkX,
        out int chunkY,
        out int chunkZ)
    {
        Span<byte> header = stackalloc byte[ChunkRecordHeaderSize];
        ReadExactly(reader.BaseStream, header);
        chunkX = BinaryPrimitives.ReadInt32LittleEndian(header);
        chunkY = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        chunkZ = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        (int expectedBatchX, int expectedBatchZ) =
            Quadrant.GetBatchIndices(chunkX, chunkZ);
        if (expectedBatchX != batchX || expectedBatchZ != batchZ ||
            payloadLength <= 0 ||
            payloadLength >
                reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException(
                $"The saved chunk ({chunkX},{chunkY},{chunkZ}) record is invalid.");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        ReadExactly(reader.BaseStream, payload);
        return payload;
    }

    private static FileStream OpenRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 256 * 1024,
            FileOptions.SequentialScan);

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        try
        {
            stream.ReadExactly(destination);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                "The saved world file ended before its declared data.",
                exception);
        }
    }

    private static int DivideRoundUp(int value, int divisor) =>
        checked((value + divisor - 1) / divisor);

    private readonly record struct SavedFile(
        string Path,
        int BatchX,
        int BatchZ,
        byte[] Sha256);

    private enum SavedSectionKind : byte
    {
        Empty,
        Uniform,
        Raw,
        Packed
    }

    private readonly record struct SavedSection(
        SavedSectionKind Kind,
        ushort UniformBlockId,
        int DataOffset,
        byte BitsPerIndex,
        int PaletteOffset,
        int PaletteCount,
        int WordOffset,
        int WordCount)
    {
        internal static SavedSection Empty =>
            new(SavedSectionKind.Empty, 0, 0, 0, 0, 0, 0, 0);
    }

    private struct ChunkShape
    {
        internal bool IsUniform;
        internal ushort UniformBlockId;
        internal int SectionCount;
        internal int RawSectionCount;
        internal int PaletteCount;
        internal int PackedWordCount;
    }

    private struct ImportTotals
    {
        internal int ChunkCount;
        internal int SectionCount;
        internal int RawSectionCount;
        internal int PaletteCount;
        internal int PackedWordCount;

        internal void Add(scoped in ChunkShape shape)
        {
            ChunkCount = checked(ChunkCount + 1);
            SectionCount = checked(SectionCount + shape.SectionCount);
            RawSectionCount = checked(
                RawSectionCount + shape.RawSectionCount);
            PaletteCount = checked(PaletteCount + shape.PaletteCount);
            PackedWordCount = checked(
                PackedWordCount + shape.PackedWordCount);
        }
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> bytes;

        internal SpanReader(ReadOnlySpan<byte> bytes)
        {
            this.bytes = bytes;
            Position = 0;
        }

        internal int Position { get; private set; }

        internal byte ReadByte()
        {
            Require(sizeof(byte));
            return bytes[Position++];
        }

        internal ushort ReadUInt16()
        {
            Require(sizeof(ushort));
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes[Position..]);
            Position += sizeof(ushort);
            return value;
        }

        internal int ReadInt32()
        {
            Require(sizeof(int));
            int value = BinaryPrimitives.ReadInt32LittleEndian(
                bytes[Position..]);
            Position += sizeof(int);
            return value;
        }

        internal void Skip(int byteCount)
        {
            Require(byteCount);
            Position += byteCount;
        }

        internal void SkipUlongArray()
        {
            int count = ReadByte();
            Skip(checked(count * sizeof(ulong)));
        }

        internal void RequireEnd()
        {
            if (Position != bytes.Length)
            {
                throw new InvalidDataException(
                    "A saved section payload has trailing data.");
            }
        }

        private void Require(int byteCount)
        {
            if (byteCount < 0 || Position > bytes.Length - byteCount)
            {
                throw new InvalidDataException(
                    "A saved section payload is incomplete.");
            }
        }
    }
}

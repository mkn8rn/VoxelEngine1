using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeMaterializedTerrain
{
    private const int ActiveRecord = 1;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool TryGetBlock(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        out ushort blockId,
        out bool handled)
    {
        blockId = 0;
        handled = false;
        if ((uint)chunkIndex >= (uint)session.ChunkCount ||
            (uint)localX >= (uint)session.ChunkSizeX ||
            (uint)localY >= (uint)session.ChunkSizeY ||
            (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        if (chunk.MaterializedChunkIndex < 0)
            return true;

        return TryGetBlockCore(
            ref session,
            chunk.MaterializedChunkIndex,
            chunk.ChunkX,
            chunk.ChunkY,
            chunk.ChunkZ,
            localX,
            localY,
            localZ,
            out blockId,
            out handled);
    }

    internal static bool TryGetBlockAtWorldChunk(
        scoped ref NativeGtrtSessionView session,
        int chunkX,
        int chunkY,
        int chunkZ,
        int localX,
        int localY,
        int localZ,
        out ushort blockId,
        out bool handled)
    {
        blockId = 0;
        handled = false;
        if ((uint)localX >= (uint)session.ChunkSizeX ||
            (uint)localY >= (uint)session.ChunkSizeY ||
            (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        int materializedChunkIndex = session.FindMaterializedChunkIndex(
            chunkX,
            chunkY,
            chunkZ);
        if (materializedChunkIndex < 0)
            return session.State.FailureCode == 0;

        return TryGetBlockCore(
            ref session,
            materializedChunkIndex,
            chunkX,
            chunkY,
            chunkZ,
            localX,
            localY,
            localZ,
            out blockId,
            out handled);
    }

    internal static bool TryGetStoredBlock(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int localX,
        int localY,
        int localZ,
        out ushort blockId)
    {
        blockId = 0;
        if ((uint)materializedChunkIndex >=
                (uint)session.State.MaterializedChunkCount ||
            (uint)localX >= (uint)session.ChunkSizeX ||
            (uint)localY >= (uint)session.ChunkSizeY ||
            (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        NativeMaterializedChunkRecord materialized =
            session.MaterializedChunks[materializedChunkIndex];
        if (!TryGetBlockCore(
                ref session,
                materializedChunkIndex,
                materialized.ChunkX,
                materialized.ChunkY,
                materialized.ChunkZ,
                localX,
                localY,
                localZ,
                out blockId,
                out bool handled) ||
            !handled)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        return true;
    }

    private static bool TryGetBlockCore(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int chunkX,
        int chunkY,
        int chunkZ,
        int localX,
        int localY,
        int localZ,
        out ushort blockId,
        out bool handled)
    {
        blockId = 0;
        handled = false;

        if (!TryGetChunkRecord(
                ref session,
                materializedChunkIndex,
                chunkX,
                chunkY,
                chunkZ,
                out NativeMaterializedChunkRecord materialized))
        {
            return false;
        }

        int sectionIndex = GetSectionIndex(
            ref session,
            localX,
            localY,
            localZ);
        int mapIndex = checked(
            materialized.SectionMapOffset + sectionIndex);
        int sectionRecordIndex = session.MaterializedSectionMaps[mapIndex];
        if (sectionRecordIndex < 0)
        {
            if (materialized.StorageKind ==
                NativeChunkStorageKind.MaterializedSections)
            {
                handled = true;
            }
            else if (materialized.StorageKind ==
                     NativeChunkStorageKind.UniformSections)
            {
                blockId = materialized.UniformBlockId;
                handled = true;
            }
            return true;
        }

        if (!TryGetSectionRecord(
                ref session,
                materializedChunkIndex,
                sectionIndex,
                sectionRecordIndex,
                out NativeMaterializedSectionRecord section))
        {
            return false;
        }

        switch (section.StorageKind)
        {
            case NativeSectionStorageKind.Uniform:
                blockId = section.UniformBlockId;
                handled = true;
                return true;
            case NativeSectionStorageKind.Raw:
                int localIndex = GetSectionLocalIndex(
                    localX,
                    localY,
                    localZ);
                int voxelIndex = checked(section.RawVoxelOffset + localIndex);
                if ((uint)voxelIndex >=
                    (uint)session.MaterializedRawVoxels.Length)
                {
                    session.Fail(
                        NativeGtrtFailureCode.InvalidMaterializedTerrain);
                    return false;
                }
                blockId = session.MaterializedRawVoxels[voxelIndex];
                handled = true;
                return true;
            case NativeSectionStorageKind.Packed:
                int packedIndex = GetSectionLocalIndex(
                    localX,
                    localY,
                    localZ);
                if (!TryReadPackedBlock(
                        ref session,
                        in section,
                        packedIndex,
                        out blockId))
                {
                    return false;
                }
                handled = true;
                return true;
            default:
                session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool TrySetBlock(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        ushort blockId)
    {
        if (!CanWrite(ref session) ||
            !session.TryGetBlockDescriptor(blockId, out _))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }
        if ((uint)chunkIndex >= (uint)session.ChunkCount ||
            (uint)localX >= (uint)session.ChunkSizeX ||
            (uint)localY >= (uint)session.ChunkSizeY ||
            (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int sectionIndex = GetSectionIndex(
            ref session,
            localX,
            localY,
            localZ);
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        bool newChunk = materializedChunkIndex < 0;
        NativeMaterializedChunkRecord existingChunk = default;
        if (newChunk)
        {
            if (session.State.MaterializedChunkCount >=
                session.MaterializedChunkCapacity)
            {
                session.Fail(
                    NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
                return false;
            }
            materializedChunkIndex = session.State.MaterializedChunkCount;
        }
        else if (!TryGetChunkRecord(
            ref session,
            materializedChunkIndex,
            activeChunk.ChunkX,
            activeChunk.ChunkY,
            activeChunk.ChunkZ,
            out existingChunk))
        {
            return false;
        }

        int sectionMapOffset = checked(
            materializedChunkIndex * session.SectionsPerChunk);
        int mapIndex = checked(sectionMapOffset + sectionIndex);
        int sectionRecordIndex = session.MaterializedSectionMaps[mapIndex];
        bool newSection = sectionRecordIndex < 0;
        NativeMaterializedSectionRecord existingSection = default;
        if (newSection)
        {
            if (session.State.MaterializedSectionCount >=
                session.MaterializedSectionCapacity)
            {
                session.Fail(
                    NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
                return false;
            }
            sectionRecordIndex = session.State.MaterializedSectionCount;
        }
        else if (!TryGetSectionRecord(
            ref session,
            materializedChunkIndex,
            sectionIndex,
            sectionRecordIndex,
            out existingSection))
        {
            return false;
        }

        bool allocateRaw = newSection ||
            existingSection.StorageKind != NativeSectionStorageKind.Raw;
        int rawVoxelOffset = allocateRaw
            ? TryAllocateRawSection(ref session)
            : existingSection.RawVoxelOffset;
        if (rawVoxelOffset < 0)
            return false;

        Span<ushort> raw = session.MaterializedRawVoxels.Slice(
            rawVoxelOffset,
            Section.VOXELS_PER_SECTION);
        if (newSection)
        {
            if (newChunk ||
                existingChunk.StorageKind ==
                    NativeChunkStorageKind.HybridSections)
            {
                if (!CopyGeneratedSection(
                        ref session,
                        chunkIndex,
                        sectionIndex,
                        raw))
                {
                    return false;
                }
            }
            else
            {
                if (existingChunk.StorageKind ==
                    NativeChunkStorageKind.UniformSections)
                {
                    raw.Fill(existingChunk.UniformBlockId);
                }
                else
                {
                    raw.Clear();
                }
            }
        }
        else
        {
            if (existingSection.StorageKind ==
                NativeSectionStorageKind.Uniform)
            {
                raw.Fill(existingSection.UniformBlockId);
            }
            else if (existingSection.StorageKind ==
                     NativeSectionStorageKind.Packed)
            {
                if (!TryDecodePackedSection(
                        ref session,
                        in existingSection,
                        raw))
                {
                    return false;
                }
            }
            else if (existingSection.StorageKind !=
                     NativeSectionStorageKind.Raw)
            {
                session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }

        raw[GetSectionLocalIndex(localX, localY, localZ)] = blockId;
        if (newChunk)
        {
            session.MaterializedChunks[materializedChunkIndex] =
                new NativeMaterializedChunkRecord
                {
                    ChunkX = activeChunk.ChunkX,
                    ChunkY = activeChunk.ChunkY,
                    ChunkZ = activeChunk.ChunkZ,
                    StorageKind = NativeChunkStorageKind.HybridSections,
                    SectionMapOffset = sectionMapOffset,
                    State = ActiveRecord,
                    Revision = 1
                };
            session.State.MaterializedChunkCount++;
        }

        ref NativeMaterializedChunkRecord materialized =
            ref session.MaterializedChunks[materializedChunkIndex];
        if (newSection)
        {
            session.MaterializedSections[sectionRecordIndex] =
                new NativeMaterializedSectionRecord
                {
                    OwnerChunkIndex = materializedChunkIndex,
                    SectionIndex = sectionIndex,
                    RawVoxelOffset = rawVoxelOffset,
                    PaletteOffset = -1,
                    PackedWordOffset = -1,
                    Revision = 1,
                    StorageKind = NativeSectionStorageKind.Raw
                };
            session.MaterializedSectionMaps[mapIndex] = sectionRecordIndex;
            session.State.MaterializedSectionCount++;
        }
        else
        {
            ref NativeMaterializedSectionRecord section =
                ref session.MaterializedSections[sectionRecordIndex];
            section.StorageKind = NativeSectionStorageKind.Raw;
            section.RawVoxelOffset = rawVoxelOffset;
            section.PaletteOffset = -1;
            section.PackedWordOffset = -1;
            section.PackedWordCount = 0;
            section.PaletteCount = 0;
            section.BitsPerIndex = 0;
            section.UniformBlockId = 0;
            section.Revision = checked(section.Revision + 1);
        }

        if (!newChunk)
            materialized.Revision = checked(materialized.Revision + 1);
        Attach(
            ref activeChunk,
            materializedChunkIndex,
            in materialized);
        return true;
    }

    internal static bool TryMakeChunkMaterialized(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex)
    {
        if (!CanWrite(ref session) ||
            (uint)chunkIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        if (materializedChunkIndex < 0)
        {
            if (session.State.MaterializedChunkCount >=
                session.MaterializedChunkCapacity)
            {
                session.Fail(
                    NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
                return false;
            }

            materializedChunkIndex = session.State.MaterializedChunkCount++;
            session.MaterializedChunks[materializedChunkIndex] =
                new NativeMaterializedChunkRecord
                {
                    ChunkX = activeChunk.ChunkX,
                    ChunkY = activeChunk.ChunkY,
                    ChunkZ = activeChunk.ChunkZ,
                    StorageKind =
                        NativeChunkStorageKind.MaterializedSections,
                    SectionMapOffset = checked(
                        materializedChunkIndex * session.SectionsPerChunk),
                    State = ActiveRecord,
                    Revision = 1
                };
        }
        else
        {
            if (!TryGetChunkRecord(
                    ref session,
                    materializedChunkIndex,
                    activeChunk.ChunkX,
                    activeChunk.ChunkY,
                    activeChunk.ChunkZ,
                    out _))
            {
                return false;
            }
            ref NativeMaterializedChunkRecord existing =
                ref session.MaterializedChunks[materializedChunkIndex];
            existing.StorageKind = NativeChunkStorageKind.MaterializedSections;
            existing.Revision = checked(existing.Revision + 1);
        }

        ref NativeMaterializedChunkRecord materialized =
            ref session.MaterializedChunks[materializedChunkIndex];
        Attach(
            ref activeChunk,
            materializedChunkIndex,
            in materialized);
        return true;
    }

    internal static bool TryMaterializeCompleteChunk(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex)
    {
        if (!CanWrite(ref session) ||
            (uint)chunkIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        if (materializedChunkIndex < 0 ||
            !TryGetChunkRecord(
                ref session,
                materializedChunkIndex,
                activeChunk.ChunkX,
                activeChunk.ChunkY,
                activeChunk.ChunkZ,
                out NativeMaterializedChunkRecord materialized))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        if (materialized.StorageKind ==
                NativeChunkStorageKind.MaterializedSections ||
            materialized.StorageKind ==
                NativeChunkStorageKind.UniformSections)
        {
            return true;
        }
        if (materialized.StorageKind !=
            NativeChunkStorageKind.HybridSections)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        int missingSectionCount = 0;
        for (int sectionIndex = 0;
             sectionIndex < session.SectionsPerChunk;
             sectionIndex++)
        {
            int mapIndex = checked(
                materialized.SectionMapOffset + sectionIndex);
            if (session.MaterializedSectionMaps[mapIndex] < 0)
                missingSectionCount++;
        }

        if (missingSectionCount >
                session.MaterializedSectionCapacity -
                session.State.MaterializedSectionCount ||
            missingSectionCount >
                session.MaterializedRawSectionCapacity -
                session.State.MaterializedRawSectionCount)
        {
            session.Fail(
                NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return false;
        }

        for (int sectionIndex = 0;
             sectionIndex < session.SectionsPerChunk;
             sectionIndex++)
        {
            int mapIndex = checked(
                materialized.SectionMapOffset + sectionIndex);
            if (session.MaterializedSectionMaps[mapIndex] >= 0)
                continue;

            int rawVoxelOffset = TryAllocateRawSection(ref session);
            if (rawVoxelOffset < 0)
                return false;
            Span<ushort> raw = session.MaterializedRawVoxels.Slice(
                rawVoxelOffset,
                Section.VOXELS_PER_SECTION);
            if (!CopyGeneratedSection(
                    ref session,
                    chunkIndex,
                    sectionIndex,
                    raw))
            {
                return false;
            }

            int sectionRecordIndex =
                session.State.MaterializedSectionCount++;
            session.MaterializedSections[sectionRecordIndex] =
                new NativeMaterializedSectionRecord
                {
                    OwnerChunkIndex = materializedChunkIndex,
                    SectionIndex = sectionIndex,
                    RawVoxelOffset = rawVoxelOffset,
                    PaletteOffset = -1,
                    PackedWordOffset = -1,
                    Revision = 1,
                    StorageKind = NativeSectionStorageKind.Raw
                };
            session.MaterializedSectionMaps[mapIndex] = sectionRecordIndex;
        }

        ref NativeMaterializedChunkRecord completed =
            ref session.MaterializedChunks[materializedChunkIndex];
        completed.StorageKind = NativeChunkStorageKind.MaterializedSections;
        Attach(
            ref activeChunk,
            materializedChunkIndex,
            in completed);
        return true;
    }

    internal static bool TrySetUniformSection(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int sectionX,
        int sectionY,
        int sectionZ,
        ushort blockId,
        NativeChunkStorageKind storageKind)
    {
        if (!CanWrite(ref session) ||
            !session.TryGetBlockDescriptor(blockId, out _) ||
            (uint)chunkIndex >= (uint)session.ChunkCount ||
            (uint)sectionX >= (uint)session.SectionCountX ||
            (uint)sectionY >= (uint)session.SectionCountY ||
            (uint)sectionZ >= (uint)session.SectionCountZ ||
            (storageKind != NativeChunkStorageKind.HybridSections &&
             storageKind != NativeChunkStorageKind.MaterializedSections))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        bool newChunk = materializedChunkIndex < 0;
        if (newChunk &&
            session.State.MaterializedChunkCount >=
                session.MaterializedChunkCapacity)
        {
            session.Fail(
                NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
            return false;
        }

        if (newChunk)
            materializedChunkIndex = session.State.MaterializedChunkCount;
        else if (!TryGetChunkRecord(
            ref session,
            materializedChunkIndex,
            activeChunk.ChunkX,
            activeChunk.ChunkY,
            activeChunk.ChunkZ,
            out _))
        {
            return false;
        }
        int sectionIndex = GetSectionIndex(
            ref session,
            sectionX,
            sectionY,
            sectionZ,
            coordinatesAreSections: true);
        int sectionMapOffset = checked(
            materializedChunkIndex * session.SectionsPerChunk);
        int mapIndex = checked(sectionMapOffset + sectionIndex);
        int sectionRecordIndex = session.MaterializedSectionMaps[mapIndex];
        bool newSection = sectionRecordIndex < 0;
        if (newSection &&
            session.State.MaterializedSectionCount >=
                session.MaterializedSectionCapacity)
        {
            session.Fail(
                NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return false;
        }
        if (!newSection &&
            !TryGetSectionRecord(
                ref session,
                materializedChunkIndex,
                sectionIndex,
                sectionRecordIndex,
                out _))
        {
            return false;
        }

        if (newChunk)
        {
            session.MaterializedChunks[materializedChunkIndex] =
                new NativeMaterializedChunkRecord
                {
                    ChunkX = activeChunk.ChunkX,
                    ChunkY = activeChunk.ChunkY,
                    ChunkZ = activeChunk.ChunkZ,
                    StorageKind = storageKind,
                    SectionMapOffset = sectionMapOffset,
                    State = ActiveRecord,
                    Revision = 1
                };
            session.State.MaterializedChunkCount++;
        }

        ref NativeMaterializedChunkRecord materialized =
            ref session.MaterializedChunks[materializedChunkIndex];
        if (materialized.StorageKind == NativeChunkStorageKind.HybridSections &&
            storageKind == NativeChunkStorageKind.MaterializedSections)
        {
            materialized.StorageKind = storageKind;
        }
        if (newSection)
        {
            sectionRecordIndex = session.State.MaterializedSectionCount++;
            session.MaterializedSectionMaps[mapIndex] = sectionRecordIndex;
            session.MaterializedSections[sectionRecordIndex] =
                new NativeMaterializedSectionRecord
                {
                    OwnerChunkIndex = materializedChunkIndex,
                    SectionIndex = sectionIndex,
                    RawVoxelOffset = -1,
                    PaletteOffset = -1,
                    PackedWordOffset = -1,
                    Revision = 1,
                    UniformBlockId = blockId,
                    StorageKind = NativeSectionStorageKind.Uniform
                };
        }
        else
        {
            ref NativeMaterializedSectionRecord section =
                ref session.MaterializedSections[sectionRecordIndex];
            section.StorageKind = NativeSectionStorageKind.Uniform;
            section.RawVoxelOffset = -1;
            section.PaletteOffset = -1;
            section.PackedWordOffset = -1;
            section.PackedWordCount = 0;
            section.PaletteCount = 0;
            section.BitsPerIndex = 0;
            section.UniformBlockId = blockId;
            section.Revision = checked(section.Revision + 1);
        }

        if (!newChunk)
            materialized.Revision = checked(materialized.Revision + 1);
        Attach(
            ref activeChunk,
            materializedChunkIndex,
            in materialized);
        return true;
    }

    internal static bool TryImportChunk(
        scoped ref NativeGtrtSessionView session,
        int chunkX,
        int chunkY,
        int chunkZ,
        bool isUniform,
        ushort uniformBlockId,
        out int materializedChunkIndex)
    {
        materializedChunkIndex = -1;
        if (!CanImport(ref session) ||
            (isUniform &&
             !session.TryGetBlockDescriptor(uniformBlockId, out _)) ||
            session.FindMaterializedChunkIndex(chunkX, chunkY, chunkZ) >= 0)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeGtrtSessionState state = ref session.State;
        if (state.MaterializedChunkCount >=
            session.MaterializedChunkCapacity)
        {
            session.Fail(
                NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
            return false;
        }

        materializedChunkIndex = state.MaterializedChunkCount++;
        int sectionMapOffset = checked(
            materializedChunkIndex * session.SectionsPerChunk);
        session.MaterializedChunks[materializedChunkIndex] =
            new NativeMaterializedChunkRecord
            {
                ChunkX = chunkX,
                ChunkY = chunkY,
                ChunkZ = chunkZ,
                StorageKind = isUniform
                    ? NativeChunkStorageKind.UniformSections
                    : NativeChunkStorageKind.MaterializedSections,
                SectionMapOffset = sectionMapOffset,
                State = ActiveRecord,
                Revision = 1,
                UniformBlockId = uniformBlockId
            };

        int chunkIndex = session.GetChunkIndex(chunkX, chunkY, chunkZ);
        if (chunkIndex >= 0)
        {
            ref NativeChunkRecord activeChunk =
                ref session.Chunks[chunkIndex];
            NativeMaterializedChunkRecord materialized =
                session.MaterializedChunks[materializedChunkIndex];
            Attach(
                ref activeChunk,
                materializedChunkIndex,
                in materialized);
        }

        return true;
    }

    internal static bool TryImportUniformSection(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int sectionIndex,
        ushort blockId)
    {
        if (!CanImportSection(
                ref session,
                materializedChunkIndex,
                sectionIndex,
                out int mapIndex,
                out int sectionRecordIndex) ||
            !session.TryGetBlockDescriptor(blockId, out _))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        session.MaterializedSections[sectionRecordIndex] =
            new NativeMaterializedSectionRecord
            {
                OwnerChunkIndex = materializedChunkIndex,
                SectionIndex = sectionIndex,
                RawVoxelOffset = -1,
                PaletteOffset = -1,
                PackedWordOffset = -1,
                Revision = 1,
                UniformBlockId = blockId,
                StorageKind = NativeSectionStorageKind.Uniform
            };
        PublishImportedSection(
            ref session,
            materializedChunkIndex,
            mapIndex,
            sectionRecordIndex);
        return true;
    }

    internal static bool TryImportRawSection(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int sectionIndex,
        scoped ReadOnlySpan<ushort> voxels)
    {
        if (voxels.Length != Section.VOXELS_PER_SECTION ||
            !CanImportSection(
                ref session,
                materializedChunkIndex,
                sectionIndex,
                out int mapIndex,
                out int sectionRecordIndex))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }
        for (int index = 0; index < voxels.Length; index++)
        {
            if (!session.TryGetBlockDescriptor(voxels[index], out _))
            {
                session.Fail(
                    NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }

        int rawVoxelOffset = TryAllocateRawSection(ref session);
        if (rawVoxelOffset < 0)
            return false;

        voxels.CopyTo(session.MaterializedRawVoxels.Slice(
            rawVoxelOffset,
            Section.VOXELS_PER_SECTION));
        session.MaterializedSections[sectionRecordIndex] =
            new NativeMaterializedSectionRecord
            {
                OwnerChunkIndex = materializedChunkIndex,
                SectionIndex = sectionIndex,
                RawVoxelOffset = rawVoxelOffset,
                PaletteOffset = -1,
                PackedWordOffset = -1,
                Revision = 1,
                StorageKind = NativeSectionStorageKind.Raw
            };
        PublishImportedSection(
            ref session,
            materializedChunkIndex,
            mapIndex,
            sectionRecordIndex);
        return true;
    }

    internal static bool TryImportPackedSection(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int sectionIndex,
        byte bitsPerIndex,
        scoped ReadOnlySpan<ushort> palette,
        scoped ReadOnlySpan<uint> packedWords)
    {
        int minimumWordCount = bitsPerIndex is > 0 and <= 16
            ? checked((Section.VOXELS_PER_SECTION * bitsPerIndex + 31) / 32)
            : -1;
        if (palette.IsEmpty ||
            palette.Length > ushort.MaxValue ||
            minimumWordCount < 0 ||
            packedWords.Length < minimumWordCount ||
            !CanImportSection(
                ref session,
                materializedChunkIndex,
                sectionIndex,
                out int mapIndex,
                out int sectionRecordIndex))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }
        for (int index = 0; index < palette.Length; index++)
        {
            if (!session.TryGetBlockDescriptor(palette[index], out _))
            {
                session.Fail(
                    NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }
        for (int voxelIndex = 0;
             voxelIndex < Section.VOXELS_PER_SECTION;
             voxelIndex++)
        {
            int paletteIndex = ReadPackedIndex(
                packedWords,
                bitsPerIndex,
                voxelIndex);
            if ((uint)paletteIndex >= (uint)palette.Length)
            {
                session.Fail(
                    NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }

        ref NativeGtrtSessionState state = ref session.State;
        int paletteOffset = state.MaterializedPaletteCursor;
        int wordOffset = state.MaterializedPackedWordCursor;
        if (palette.Length >
                session.MaterializedPaletteCapacity - paletteOffset ||
            packedWords.Length >
                session.MaterializedPackedWordCapacity - wordOffset)
        {
            session.Fail(
                NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return false;
        }

        palette.CopyTo(session.MaterializedPalette.Slice(
            paletteOffset,
            palette.Length));
        packedWords.CopyTo(session.MaterializedPackedWords.Slice(
            wordOffset,
            packedWords.Length));
        state.MaterializedPaletteCursor = checked(
            paletteOffset + palette.Length);
        state.MaterializedPackedWordCursor = checked(
            wordOffset + packedWords.Length);
        session.MaterializedSections[sectionRecordIndex] =
            new NativeMaterializedSectionRecord
            {
                OwnerChunkIndex = materializedChunkIndex,
                SectionIndex = sectionIndex,
                RawVoxelOffset = -1,
                PaletteOffset = paletteOffset,
                PackedWordOffset = wordOffset,
                PackedWordCount = packedWords.Length,
                Revision = 1,
                PaletteCount = checked((ushort)palette.Length),
                BitsPerIndex = bitsPerIndex,
                StorageKind = NativeSectionStorageKind.Packed
            };
        PublishImportedSection(
            ref session,
            materializedChunkIndex,
            mapIndex,
            sectionRecordIndex);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool CopyGeneratedSection(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int sectionIndex,
        Span<ushort> destination)
    {
        destination.Clear();
        GetSectionCoordinates(
            ref session,
            sectionIndex,
            out int sectionX,
            out int sectionY,
            out int sectionZ);
        int baseX = sectionX * Section.SECTION_SIZE;
        int baseY = sectionY * Section.SECTION_SIZE;
        int baseZ = sectionZ * Section.SECTION_SIZE;
        int endX = Math.Min(
            baseX + Section.SECTION_SIZE,
            session.ChunkSizeX);
        int endY = Math.Min(
            baseY + Section.SECTION_SIZE,
            session.ChunkSizeY);
        int endZ = Math.Min(
            baseZ + Section.SECTION_SIZE,
            session.ChunkSizeZ);
        for (int localZ = baseZ; localZ < endZ; localZ++)
        {
            for (int localX = baseX; localX < endX; localX++)
            {
                for (int localY = baseY; localY < endY; localY++)
                {
                    if (!NativeGeneratedTerrain.TryGetGeneratedBlock(
                            ref session,
                            chunkIndex,
                            localX,
                            localY,
                            localZ,
                            out ushort blockId))
                    {
                        return false;
                    }
                    destination[GetSectionLocalIndex(
                        localX,
                        localY,
                        localZ)] = blockId;
                }
            }
        }

        return true;
    }

    private static bool CanWrite(scoped ref NativeGtrtSessionView session)
    {
        ref NativeGtrtSessionState state = ref session.State;
        return state.PublicationState == 1 &&
            Volatile.Read(ref state.ClaimedGenerationCount) == 0 &&
            Volatile.Read(ref state.ClaimedMeshCount) == 0 &&
            Volatile.Read(ref state.PacketConsumerCount) == 0 &&
            Volatile.Read(ref state.DisposalState) == 0;
    }

    private static bool CanImport(scoped ref NativeGtrtSessionView session)
    {
        ref NativeGtrtSessionState state = ref session.State;
        return state.PublicationState == 0 &&
            Volatile.Read(ref state.ClaimedGenerationCount) == 0 &&
            Volatile.Read(ref state.ClaimedMeshCount) == 0 &&
            Volatile.Read(ref state.PacketConsumerCount) == 0 &&
            Volatile.Read(ref state.DisposalState) == 0;
    }

    private static bool CanImportSection(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int sectionIndex,
        out int mapIndex,
        out int sectionRecordIndex)
    {
        mapIndex = -1;
        sectionRecordIndex = -1;
        if (!CanImport(ref session) ||
            (uint)materializedChunkIndex >=
                (uint)session.State.MaterializedChunkCount ||
            (uint)sectionIndex >= (uint)session.SectionsPerChunk ||
            session.State.MaterializedSectionCount >=
                session.MaterializedSectionCapacity)
        {
            return false;
        }

        NativeMaterializedChunkRecord chunk =
            session.MaterializedChunks[materializedChunkIndex];
        if (chunk.State != ActiveRecord ||
            chunk.SectionMapOffset !=
                materializedChunkIndex * session.SectionsPerChunk)
        {
            return false;
        }

        mapIndex = checked(chunk.SectionMapOffset + sectionIndex);
        if (session.MaterializedSectionMaps[mapIndex] >= 0)
            return false;

        sectionRecordIndex = session.State.MaterializedSectionCount;
        return true;
    }

    private static void PublishImportedSection(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int mapIndex,
        int sectionRecordIndex)
    {
        session.MaterializedSectionMaps[mapIndex] = sectionRecordIndex;
        session.State.MaterializedSectionCount++;
        ref NativeMaterializedChunkRecord chunk =
            ref session.MaterializedChunks[materializedChunkIndex];
        chunk.Revision = checked(chunk.Revision + 1);

        int activeChunkIndex = session.GetChunkIndex(
            chunk.ChunkX,
            chunk.ChunkY,
            chunk.ChunkZ);
        if (activeChunkIndex >= 0)
        {
            Attach(
                ref session.Chunks[activeChunkIndex],
                materializedChunkIndex,
                in chunk);
        }
    }

    private static int TryAllocateRawSection(
        scoped ref NativeGtrtSessionView session)
    {
        ref NativeGtrtSessionState state = ref session.State;
        if (state.MaterializedRawSectionCount >=
            session.MaterializedRawSectionCapacity)
        {
            session.Fail(
                NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return -1;
        }

        int rawSectionIndex = state.MaterializedRawSectionCount++;
        return checked(rawSectionIndex * Section.VOXELS_PER_SECTION);
    }

    private static bool TryDecodePackedSection(
        scoped ref NativeGtrtSessionView session,
        scoped in NativeMaterializedSectionRecord section,
        Span<ushort> destination)
    {
        if (destination.Length != Section.VOXELS_PER_SECTION)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        for (int index = 0; index < destination.Length; index++)
        {
            if (!TryReadPackedBlock(
                    ref session,
                    in section,
                    index,
                    out destination[index]))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadPackedIndex(
        scoped ReadOnlySpan<uint> words,
        int bitsPerIndex,
        int voxelIndex)
    {
        long bitPosition = (long)voxelIndex * bitsPerIndex;
        int wordIndex = (int)(bitPosition >> 5);
        int bitOffset = (int)(bitPosition & 31);
        uint value = words[wordIndex] >> bitOffset;
        int remaining = 32 - bitOffset;
        if (remaining < bitsPerIndex)
            value |= words[wordIndex + 1] << remaining;

        uint mask = (1u << bitsPerIndex) - 1u;
        return (int)(value & mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadPackedBlock(
        scoped ref NativeGtrtSessionView session,
        scoped in NativeMaterializedSectionRecord section,
        int voxelIndex,
        out ushort blockId)
    {
        blockId = 0;
        int bitsPerIndex = section.BitsPerIndex;
        long bitPosition = (long)voxelIndex * bitsPerIndex;
        int wordIndex = checked((int)(bitPosition >> 5));
        int bitOffset = (int)(bitPosition & 31);
        if ((uint)wordIndex >= (uint)section.PackedWordCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ReadOnlySpan<uint> words = session.MaterializedPackedWords.Slice(
            section.PackedWordOffset,
            section.PackedWordCount);
        uint value = words[wordIndex] >> bitOffset;
        int remaining = 32 - bitOffset;
        if (remaining < bitsPerIndex)
        {
            if ((uint)(wordIndex + 1) >= (uint)words.Length)
            {
                session.Fail(
                    NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
            value |= words[wordIndex + 1] << remaining;
        }

        uint mask = (1u << bitsPerIndex) - 1u;
        int paletteIndex = (int)(value & mask);
        if ((uint)paletteIndex >= (uint)section.PaletteCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        blockId = session.MaterializedPalette[
            section.PaletteOffset + paletteIndex];
        return true;
    }

    private static bool TryGetChunkRecord(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int chunkX,
        int chunkY,
        int chunkZ,
        out NativeMaterializedChunkRecord materialized)
    {
        int count = session.State.MaterializedChunkCount;
        if ((uint)materializedChunkIndex >= (uint)count ||
            (uint)count > (uint)session.MaterializedChunks.Length)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            materialized = default;
            return false;
        }

        materialized = session.MaterializedChunks[materializedChunkIndex];
        if (materialized.State != ActiveRecord ||
            materialized.ChunkX != chunkX ||
            materialized.ChunkY != chunkY ||
            materialized.ChunkZ != chunkZ ||
            materialized.SectionMapOffset !=
                materializedChunkIndex * session.SectionsPerChunk)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        return true;
    }

    private static bool TryGetSectionRecord(
        scoped ref NativeGtrtSessionView session,
        int materializedChunkIndex,
        int sectionIndex,
        int sectionRecordIndex,
        out NativeMaterializedSectionRecord section)
    {
        int count = session.State.MaterializedSectionCount;
        if ((uint)sectionRecordIndex >= (uint)count ||
            (uint)count > (uint)session.MaterializedSections.Length)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            section = default;
            return false;
        }

        section = session.MaterializedSections[sectionRecordIndex];
        if (section.OwnerChunkIndex != materializedChunkIndex ||
            section.SectionIndex != sectionIndex)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        bool validStorage = section.StorageKind switch
        {
            NativeSectionStorageKind.Uniform =>
                section.RawVoxelOffset == -1 &&
                section.PaletteOffset == -1 &&
                section.PackedWordOffset == -1,
            NativeSectionStorageKind.Raw =>
                section.RawVoxelOffset >= 0 &&
                section.RawVoxelOffset % Section.VOXELS_PER_SECTION == 0 &&
                section.RawVoxelOffset <=
                    session.MaterializedRawVoxels.Length -
                    Section.VOXELS_PER_SECTION,
            NativeSectionStorageKind.Packed =>
                section.RawVoxelOffset == -1 &&
                section.BitsPerIndex is > 0 and <= 16 &&
                section.PaletteCount > 0 &&
                section.PaletteOffset >= 0 &&
                section.PaletteOffset <=
                    session.MaterializedPalette.Length -
                    section.PaletteCount &&
                section.PackedWordCount > 0 &&
                section.PackedWordOffset >= 0 &&
                section.PackedWordOffset <=
                    session.MaterializedPackedWords.Length -
                    section.PackedWordCount,
            _ => false
        };
        if (!validStorage)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSectionIndex(
        scoped ref NativeGtrtSessionView session,
        int x,
        int y,
        int z,
        bool coordinatesAreSections = false)
    {
        int sectionX = coordinatesAreSections
            ? x
            : x / Section.SECTION_SIZE;
        int sectionY = coordinatesAreSections
            ? y
            : y / Section.SECTION_SIZE;
        int sectionZ = coordinatesAreSections
            ? z
            : z / Section.SECTION_SIZE;
        return checked(
            ((sectionX * session.SectionCountY) + sectionY) *
            session.SectionCountZ + sectionZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSectionLocalIndex(int x, int y, int z) =>
        (((z & (Section.SECTION_SIZE - 1)) * Section.SECTION_SIZE) +
         (x & (Section.SECTION_SIZE - 1))) * Section.SECTION_SIZE +
        (y & (Section.SECTION_SIZE - 1));

    private static void GetSectionCoordinates(
        scoped ref NativeGtrtSessionView session,
        int sectionIndex,
        out int sectionX,
        out int sectionY,
        out int sectionZ)
    {
        sectionZ = sectionIndex % session.SectionCountZ;
        int remaining = sectionIndex / session.SectionCountZ;
        sectionY = remaining % session.SectionCountY;
        sectionX = remaining / session.SectionCountY;
    }

    private static void Attach(
        ref NativeChunkRecord chunk,
        int materializedChunkIndex,
        scoped in NativeMaterializedChunkRecord materialized)
    {
        chunk.MaterializedChunkIndex = materializedChunkIndex;
        chunk.StorageKind = materialized.StorageKind;
        chunk.DirtyRevision = materialized.Revision;
    }
}

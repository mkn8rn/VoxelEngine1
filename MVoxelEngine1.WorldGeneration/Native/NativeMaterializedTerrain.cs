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

        if (!TryGetChunkRecord(
                ref session,
                chunk.MaterializedChunkIndex,
                chunk.ChunkX,
                chunk.ChunkY,
                chunk.ChunkZ,
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
            return true;
        }

        if (!TryGetSectionRecord(
                ref session,
                chunk.MaterializedChunkIndex,
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

        int rawVoxelOffset = checked(
            sectionRecordIndex * Section.VOXELS_PER_SECTION);
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
                raw.Clear();
            }
        }
        else
        {
            if (existingSection.StorageKind ==
                NativeSectionStorageKind.Uniform)
            {
                raw.Fill(existingSection.UniformBlockId);
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
                    RawVoxelOffset = checked(
                        sectionRecordIndex * Section.VOXELS_PER_SECTION),
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
            section.SectionIndex != sectionIndex ||
            section.RawVoxelOffset !=
                sectionRecordIndex * Section.VOXELS_PER_SECTION)
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

using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;
internal static class NativeMaterializedTerrain
{
    private const int ActiveRecord = 1;
    internal const int DeferredRecord = 2;
    internal static bool TryReserveSavedChunk(scoped ref NativeGtrtSessionView session, int chunkX, int chunkY, int chunkZ, out int index)
    {
        index = session.FindMaterializedChunkIndex(chunkX, chunkY, chunkZ);
        if (!CanImport(ref session))
            return false;
        if (index >= 0)
            return true;
        if (session.State.MaterializedChunkCount >= session.MaterializedChunkCapacity)
            return false;
        index = session.State.MaterializedChunkCount++;
        session.MaterializedChunks[index] = new NativeMaterializedChunkRecord
        {
            ChunkX = chunkX,
            ChunkY = chunkY,
            ChunkZ = chunkZ,
            SectionMapOffset = checked(index * session.SectionsPerChunk),
            StorageKind = NativeChunkStorageKind.DeferredSaved,
            State = DeferredRecord
        };
        session.IndexMaterializedChunk(index);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool TryGetBlock(scoped ref NativeGtrtSessionView session, int chunkIndex, int localX, int localY, int localZ, out ushort blockId, out bool handled)
    {
        blockId = 0;
        handled = false;
        if ((uint)chunkIndex >= (uint)session.ChunkCount || (uint)localX >= (uint)session.ChunkSizeX || (uint)localY >= (uint)session.ChunkSizeY || (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        if (chunk.MaterializedChunkIndex < 0)
            return true;
        return TryGetBlockCore(ref session, chunk.MaterializedChunkIndex, chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, localX, localY, localZ, out blockId, out handled);
    }

    internal static bool TryGetBlockAtWorldChunk(scoped ref NativeGtrtSessionView session, int chunkX, int chunkY, int chunkZ, int localX, int localY, int localZ, out ushort blockId, out bool handled)
    {
        blockId = 0;
        handled = false;
        if ((uint)localX >= (uint)session.ChunkSizeX || (uint)localY >= (uint)session.ChunkSizeY || (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        int materializedChunkIndex = session.FindMaterializedChunkIndex(chunkX, chunkY, chunkZ);
        if (materializedChunkIndex < 0)
            return session.State.FailureCode == 0;
        return TryGetBlockCore(ref session, materializedChunkIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, out blockId, out handled);
    }

    internal static bool TryGetStoredBlock(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int localX, int localY, int localZ, out ushort blockId)
    {
        blockId = 0;
        if ((uint)materializedChunkIndex >= (uint)session.State.MaterializedChunkCount || (uint)localX >= (uint)session.ChunkSizeX || (uint)localY >= (uint)session.ChunkSizeY || (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        NativeMaterializedChunkRecord materialized = session.MaterializedChunks[materializedChunkIndex];
        if (!TryGetBlockCore(ref session, materializedChunkIndex, materialized.ChunkX, materialized.ChunkY, materialized.ChunkZ, localX, localY, localZ, out blockId, out bool handled) || !handled)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        return true;
    }

    private static bool TryGetBlockCore(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int chunkX, int chunkY, int chunkZ, int localX, int localY, int localZ, out ushort blockId, out bool handled)
    {
        blockId = 0;
        handled = false;
        if (!TryGetChunkRecord(ref session, materializedChunkIndex, chunkX, chunkY, chunkZ, out NativeMaterializedChunkRecord materialized))
        {
            return false;
        }

        int sectionIndex = GetSectionIndex(ref session, localX, localY, localZ);
        int mapIndex = checked(materialized.SectionMapOffset + sectionIndex);
        int sectionRecordIndex = session.MaterializedSectionMaps[mapIndex];
        if (sectionRecordIndex < 0)
        {
            if (materialized.StorageKind == NativeChunkStorageKind.MaterializedSections)
            {
                handled = true;
            }
            else if (materialized.StorageKind == NativeChunkStorageKind.UniformSections)
            {
                blockId = materialized.UniformBlockId;
                handled = true;
            }

            return true;
        }

        if (!TryGetSectionRecord(ref session, materializedChunkIndex, sectionIndex, sectionRecordIndex, out NativeMaterializedSectionRecord section))
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
                int localIndex = GetSectionLocalIndex(localX, localY, localZ);
                int voxelIndex = checked(section.RawVoxelOffset + localIndex);
                if ((uint)voxelIndex >= (uint)session.MaterializedRawVoxels.Length)
                {
                    session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                    return false;
                }

                blockId = session.MaterializedRawVoxels[voxelIndex];
                handled = true;
                return true;
            case NativeSectionStorageKind.Packed:
                int packedIndex = GetSectionLocalIndex(localX, localY, localZ);
                if (!TryReadPackedBlock(ref session, in section, packedIndex, out blockId))
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
    internal static bool TrySetBlock(scoped ref NativeGtrtSessionView session, int chunkIndex, int localX, int localY, int localZ, ushort blockId)
    {
        if (!CanWrite(ref session) || !session.TryGetBlockDescriptor(blockId, out _))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        if ((uint)chunkIndex >= (uint)session.ChunkCount || (uint)localX >= (uint)session.ChunkSizeX || (uint)localY >= (uint)session.ChunkSizeY || (uint)localZ >= (uint)session.ChunkSizeZ)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int sectionIndex = GetSectionIndex(ref session, localX, localY, localZ);
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        bool newChunk = materializedChunkIndex < 0;
        NativeMaterializedChunkRecord existingChunk = default;
        if (newChunk)
        {
            if (session.State.MaterializedChunkCount >= session.MaterializedChunkCapacity)
            {
                session.Fail(NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
                return false;
            }

            materializedChunkIndex = session.State.MaterializedChunkCount;
        }
        else if (!TryGetChunkRecord(ref session, materializedChunkIndex, activeChunk.ChunkX, activeChunk.ChunkY, activeChunk.ChunkZ, out existingChunk))
        {
            return false;
        }

        int sectionMapOffset = checked(materializedChunkIndex * session.SectionsPerChunk);
        int mapIndex = checked(sectionMapOffset + sectionIndex);
        int sectionRecordIndex = session.MaterializedSectionMaps[mapIndex];
        bool newSection = sectionRecordIndex < 0;
        NativeMaterializedSectionRecord existingSection = default;
        if (newSection)
        {
            if (session.State.MaterializedSectionCount >= session.MaterializedSectionCapacity)
            {
                session.Fail(NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
                return false;
            }

            sectionRecordIndex = session.State.MaterializedSectionCount;
        }
        else if (!TryGetSectionRecord(ref session, materializedChunkIndex, sectionIndex, sectionRecordIndex, out existingSection))
        {
            return false;
        }

        bool allocateRaw = newSection || existingSection.StorageKind != NativeSectionStorageKind.Raw;
        int rawVoxelOffset = allocateRaw ? TryAllocateRawSection(ref session) : existingSection.RawVoxelOffset;
        if (rawVoxelOffset < 0)
            return false;
        Span<ushort> raw = session.MaterializedRawVoxels.Slice(rawVoxelOffset, VoxelSection.VoxelCount);
        if (newSection)
        {
            if (newChunk || existingChunk.StorageKind == NativeChunkStorageKind.HybridSections)
            {
                if (!CopyGeneratedSection(ref session, chunkIndex, sectionIndex, raw))
                {
                    return false;
                }
            }
            else
            {
                if (existingChunk.StorageKind == NativeChunkStorageKind.UniformSections)
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
            if (existingSection.StorageKind == NativeSectionStorageKind.Uniform)
            {
                raw.Fill(existingSection.UniformBlockId);
            }
            else if (existingSection.StorageKind == NativeSectionStorageKind.Packed)
            {
                if (!TryDecodePackedSection(ref session, in existingSection, raw))
                {
                    return false;
                }
            }
            else if (existingSection.StorageKind != NativeSectionStorageKind.Raw)
            {
                session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }

        raw[GetSectionLocalIndex(localX, localY, localZ)] = blockId;
        if (newChunk)
        {
            session.MaterializedChunks[materializedChunkIndex] = new NativeMaterializedChunkRecord
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

        ref NativeMaterializedChunkRecord materialized = ref session.MaterializedChunks[materializedChunkIndex];
        if (newSection)
        {
            session.MaterializedSections[sectionRecordIndex] = new NativeMaterializedSectionRecord
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
            ref NativeMaterializedSectionRecord section = ref session.MaterializedSections[sectionRecordIndex];
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
        Attach(ref session, ref activeChunk, materializedChunkIndex, in materialized);
        return true;
    }

    internal static bool TryMakeChunkMaterialized(scoped ref NativeGtrtSessionView session, int chunkIndex)
    {
        if (!CanWrite(ref session) || (uint)chunkIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        if (materializedChunkIndex < 0)
        {
            if (session.State.MaterializedChunkCount >= session.MaterializedChunkCapacity)
            {
                session.Fail(NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
                return false;
            }

            materializedChunkIndex = session.State.MaterializedChunkCount++;
            session.MaterializedChunks[materializedChunkIndex] = new NativeMaterializedChunkRecord
            {
                ChunkX = activeChunk.ChunkX,
                ChunkY = activeChunk.ChunkY,
                ChunkZ = activeChunk.ChunkZ,
                StorageKind = NativeChunkStorageKind.MaterializedSections,
                SectionMapOffset = checked(materializedChunkIndex * session.SectionsPerChunk),
                State = ActiveRecord,
                Revision = 1
            };
        }
        else
        {
            if (!TryGetChunkRecord(ref session, materializedChunkIndex, activeChunk.ChunkX, activeChunk.ChunkY, activeChunk.ChunkZ, out _))
            {
                return false;
            }

            ref NativeMaterializedChunkRecord existing = ref session.MaterializedChunks[materializedChunkIndex];
            existing.StorageKind = NativeChunkStorageKind.MaterializedSections;
            existing.Revision = checked(existing.Revision + 1);
        }

        ref NativeMaterializedChunkRecord materialized = ref session.MaterializedChunks[materializedChunkIndex];
        Attach(ref session, ref activeChunk, materializedChunkIndex, in materialized);
        return true;
    }

    internal static bool TryGetEditStorageRequirements(scoped ref NativeGtrtSessionView session, int chunkIndex, int localX, int localY, int localZ, out NativeMaterializedStorageRequirements requirements)
    {
        requirements = default;
        if (!CanWrite(ref session) || (uint)chunkIndex >= (uint)session.ChunkCount)
            return false;
        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        int materializedIndex = chunk.MaterializedChunkIndex;
        NativeMaterializedChunkRecord materialized = materializedIndex >= 0 ? session.MaterializedChunks[materializedIndex] : default;
        int targetSection = GetSectionIndex(ref session, localX, localY, localZ);
        int newSections = 0;
        int newRawSections = 0;
        Span<ushort> scratch = stackalloc ushort[VoxelSection.VoxelCount];
        for (int sectionIndex = 0; sectionIndex < session.SectionsPerChunk; sectionIndex++)
        {
            int recordIndex = materializedIndex >= 0 ? session.MaterializedSectionMaps[materialized.SectionMapOffset + sectionIndex] : -1;
            if (recordIndex >= 0)
            {
                if (sectionIndex == targetSection && session.MaterializedSections[recordIndex].StorageKind != NativeSectionStorageKind.Raw)
                    newRawSections++;
                continue;
            }

            bool generated = materializedIndex < 0 || materialized.StorageKind == NativeChunkStorageKind.HybridSections;
            if (!generated && sectionIndex != targetSection)
                continue;
            newSections++;
            if (sectionIndex == targetSection)
            {
                newRawSections++;
                continue;
            }

            if (!CopyGeneratedSection(ref session, chunkIndex, sectionIndex, scratch))
                return false;
            if (scratch.IndexOfAnyExcept(scratch[0]) >= 0)
                newRawSections++;
        }

        requirements = new NativeMaterializedStorageRequirements(checked(session.State.MaterializedChunkCount + (materializedIndex < 0 ? 1 : 0)), checked(session.State.MaterializedSectionCount + newSections), checked(session.State.MaterializedRawSectionCount + newRawSections));
        return true;
    }

    internal static bool TryMaterializeCompleteChunk(scoped ref NativeGtrtSessionView session, int chunkIndex)
    {
        if (!CanWrite(ref session) || (uint)chunkIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        if (materializedChunkIndex < 0 || !TryGetChunkRecord(ref session, materializedChunkIndex, activeChunk.ChunkX, activeChunk.ChunkY, activeChunk.ChunkZ, out NativeMaterializedChunkRecord materialized))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        if (materialized.StorageKind == NativeChunkStorageKind.MaterializedSections || materialized.StorageKind == NativeChunkStorageKind.UniformSections)
        {
            return true;
        }

        if (materialized.StorageKind != NativeChunkStorageKind.HybridSections)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        int missingSectionCount = 0;
        int missingRawSectionCount = 0;
        Span<ushort> scratch = stackalloc ushort[VoxelSection.VoxelCount];
        for (int sectionIndex = 0; sectionIndex < session.SectionsPerChunk; sectionIndex++)
        {
            int mapIndex = checked(materialized.SectionMapOffset + sectionIndex);
            if (session.MaterializedSectionMaps[mapIndex] >= 0)
                continue;
            missingSectionCount++;
            if (!CopyGeneratedSection(ref session, chunkIndex, sectionIndex, scratch))
                return false;
            if (scratch.IndexOfAnyExcept(scratch[0]) >= 0)
                missingRawSectionCount++;
        }

        if (missingSectionCount > session.MaterializedSectionCapacity - session.State.MaterializedSectionCount || missingRawSectionCount > session.MaterializedRawSectionCapacity - session.State.MaterializedRawSectionCount)
        {
            session.Fail(NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return false;
        }

        for (int sectionIndex = 0; sectionIndex < session.SectionsPerChunk; sectionIndex++)
        {
            int mapIndex = checked(materialized.SectionMapOffset + sectionIndex);
            if (session.MaterializedSectionMaps[mapIndex] >= 0)
                continue;
            if (!CopyGeneratedSection(ref session, chunkIndex, sectionIndex, scratch))
            {
                return false;
            }

            bool uniform = scratch.IndexOfAnyExcept(scratch[0]) < 0;
            int rawVoxelOffset = -1;
            if (!uniform)
            {
                rawVoxelOffset = TryAllocateRawSection(ref session);
                if (rawVoxelOffset < 0)
                    return false;
                scratch.CopyTo(session.MaterializedRawVoxels.Slice(rawVoxelOffset, VoxelSection.VoxelCount));
            }

            int sectionRecordIndex = session.State.MaterializedSectionCount++;
            session.MaterializedSections[sectionRecordIndex] = new NativeMaterializedSectionRecord
            {
                OwnerChunkIndex = materializedChunkIndex,
                SectionIndex = sectionIndex,
                RawVoxelOffset = rawVoxelOffset,
                PaletteOffset = -1,
                PackedWordOffset = -1,
                Revision = 1,
                UniformBlockId = uniform ? scratch[0] : (ushort)0,
                StorageKind = uniform ? NativeSectionStorageKind.Uniform : NativeSectionStorageKind.Raw
            };
            session.MaterializedSectionMaps[mapIndex] = sectionRecordIndex;
        }

        ref NativeMaterializedChunkRecord completed = ref session.MaterializedChunks[materializedChunkIndex];
        completed.StorageKind = NativeChunkStorageKind.MaterializedSections;
        Attach(ref session, ref activeChunk, materializedChunkIndex, in completed);
        return true;
    }

    internal static bool TrySetUniformSection(scoped ref NativeGtrtSessionView session, int chunkIndex, int sectionX, int sectionY, int sectionZ, ushort blockId, NativeChunkStorageKind storageKind)
    {
        if (!CanWrite(ref session) || !session.TryGetBlockDescriptor(blockId, out _) || (uint)chunkIndex >= (uint)session.ChunkCount || (uint)sectionX >= (uint)session.SectionCountX || (uint)sectionY >= (uint)session.SectionCountY || (uint)sectionZ >= (uint)session.SectionCountZ || (storageKind != NativeChunkStorageKind.HybridSections && storageKind != NativeChunkStorageKind.MaterializedSections))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
        int materializedChunkIndex = activeChunk.MaterializedChunkIndex;
        bool newChunk = materializedChunkIndex < 0;
        if (newChunk && session.State.MaterializedChunkCount >= session.MaterializedChunkCapacity)
        {
            session.Fail(NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
            return false;
        }

        if (newChunk)
            materializedChunkIndex = session.State.MaterializedChunkCount;
        else if (!TryGetChunkRecord(ref session, materializedChunkIndex, activeChunk.ChunkX, activeChunk.ChunkY, activeChunk.ChunkZ, out _))
        {
            return false;
        }

        int sectionIndex = GetSectionIndex(ref session, sectionX, sectionY, sectionZ, coordinatesAreSections: true);
        int sectionMapOffset = checked(materializedChunkIndex * session.SectionsPerChunk);
        int mapIndex = checked(sectionMapOffset + sectionIndex);
        int sectionRecordIndex = session.MaterializedSectionMaps[mapIndex];
        bool newSection = sectionRecordIndex < 0;
        if (newSection && session.State.MaterializedSectionCount >= session.MaterializedSectionCapacity)
        {
            session.Fail(NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return false;
        }

        if (!newSection && !TryGetSectionRecord(ref session, materializedChunkIndex, sectionIndex, sectionRecordIndex, out _))
        {
            return false;
        }

        if (newChunk)
        {
            session.MaterializedChunks[materializedChunkIndex] = new NativeMaterializedChunkRecord
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

        ref NativeMaterializedChunkRecord materialized = ref session.MaterializedChunks[materializedChunkIndex];
        if (materialized.StorageKind == NativeChunkStorageKind.HybridSections && storageKind == NativeChunkStorageKind.MaterializedSections)
        {
            materialized.StorageKind = storageKind;
        }

        if (newSection)
        {
            sectionRecordIndex = session.State.MaterializedSectionCount++;
            session.MaterializedSectionMaps[mapIndex] = sectionRecordIndex;
            session.MaterializedSections[sectionRecordIndex] = new NativeMaterializedSectionRecord
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
            ref NativeMaterializedSectionRecord section = ref session.MaterializedSections[sectionRecordIndex];
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
        Attach(ref session, ref activeChunk, materializedChunkIndex, in materialized);
        return true;
    }

    internal static bool TryImportChunk(scoped ref NativeGtrtSessionView session, int chunkX, int chunkY, int chunkZ, bool isUniform, ushort uniformBlockId, out int materializedChunkIndex)
    {
        materializedChunkIndex = -1;
        if (!CanImport(ref session) || (isUniform && !session.TryGetBlockDescriptor(uniformBlockId, out _)))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        ref NativeGtrtSessionState state = ref session.State;
        int existing = session.FindMaterializedChunkIndex(chunkX, chunkY, chunkZ);
        if (existing >= 0 && session.MaterializedChunks[existing].State != 2)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        if (existing < 0 && state.MaterializedChunkCount >= session.MaterializedChunkCapacity)
        {
            session.Fail(NativeGtrtFailureCode.MaterializedChunkStorageExhausted);
            return false;
        }

        materializedChunkIndex = existing >= 0 ? existing : state.MaterializedChunkCount++;
        NativeSavedChunkSource savedSource = existing >= 0 ? session.MaterializedChunks[existing].SavedSource : default;
        int sectionMapOffset = checked(materializedChunkIndex * session.SectionsPerChunk);
        session.MaterializedChunks[materializedChunkIndex] = new NativeMaterializedChunkRecord
        {
            ChunkX = chunkX,
            ChunkY = chunkY,
            ChunkZ = chunkZ,
            StorageKind = isUniform ? NativeChunkStorageKind.UniformSections : NativeChunkStorageKind.MaterializedSections,
            SectionMapOffset = sectionMapOffset,
            State = ActiveRecord,
            Revision = 1,
            UniformBlockId = uniformBlockId,
            SavedSource = savedSource
        };
        session.IndexMaterializedChunk(materializedChunkIndex);
        int chunkIndex = session.GetChunkIndex(chunkX, chunkY, chunkZ);
        if (chunkIndex >= 0)
        {
            ref NativeChunkRecord activeChunk = ref session.Chunks[chunkIndex];
            NativeMaterializedChunkRecord materialized = session.MaterializedChunks[materializedChunkIndex];
            Attach(ref session, ref activeChunk, materializedChunkIndex, in materialized);
        }

        return true;
    }

    internal static bool TryImportUniformSection(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int sectionIndex, ushort blockId)
    {
        if (!CanImportSection(ref session, materializedChunkIndex, sectionIndex, out int mapIndex, out int sectionRecordIndex) || !session.TryGetBlockDescriptor(blockId, out _))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        session.MaterializedSections[sectionRecordIndex] = new NativeMaterializedSectionRecord
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
        PublishImportedSection(ref session, materializedChunkIndex, mapIndex, sectionRecordIndex);
        return true;
    }

    internal static bool TryImportRawSection(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int sectionIndex, scoped ReadOnlySpan<ushort> voxels)
    {
        if (voxels.Length != VoxelSection.VoxelCount || !CanImportSection(ref session, materializedChunkIndex, sectionIndex, out int mapIndex, out int sectionRecordIndex))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        for (int index = 0; index < voxels.Length; index++)
        {
            if (!session.TryGetBlockDescriptor(voxels[index], out _))
            {
                session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }

        int rawVoxelOffset = TryAllocateRawSection(ref session);
        if (rawVoxelOffset < 0)
            return false;
        voxels.CopyTo(session.MaterializedRawVoxels.Slice(rawVoxelOffset, VoxelSection.VoxelCount));
        session.MaterializedSections[sectionRecordIndex] = new NativeMaterializedSectionRecord
        {
            OwnerChunkIndex = materializedChunkIndex,
            SectionIndex = sectionIndex,
            RawVoxelOffset = rawVoxelOffset,
            PaletteOffset = -1,
            PackedWordOffset = -1,
            Revision = 1,
            StorageKind = NativeSectionStorageKind.Raw
        };
        PublishImportedSection(ref session, materializedChunkIndex, mapIndex, sectionRecordIndex);
        return true;
    }

    internal static bool TryImportPackedSection(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int sectionIndex, byte bitsPerIndex, scoped ReadOnlySpan<ushort> palette, scoped ReadOnlySpan<uint> packedWords)
    {
        int minimumWordCount = bitsPerIndex is> 0 and <= 16 ? checked((VoxelSection.VoxelCount * bitsPerIndex + 31) / 32) : -1;
        if (palette.IsEmpty || palette.Length > ushort.MaxValue || minimumWordCount < 0 || packedWords.Length < minimumWordCount || !CanImportSection(ref session, materializedChunkIndex, sectionIndex, out int mapIndex, out int sectionRecordIndex))
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        for (int index = 0; index < palette.Length; index++)
        {
            if (!session.TryGetBlockDescriptor(palette[index], out _))
            {
                session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }

        for (int voxelIndex = 0; voxelIndex < VoxelSection.VoxelCount; voxelIndex++)
        {
            int paletteIndex = ReadPackedIndex(packedWords, bitsPerIndex, voxelIndex);
            if ((uint)paletteIndex >= (uint)palette.Length)
            {
                session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
                return false;
            }
        }

        ref NativeGtrtSessionState state = ref session.State;
        int paletteOffset = state.MaterializedPaletteCursor;
        int wordOffset = state.MaterializedPackedWordCursor;
        if (palette.Length > session.MaterializedPaletteCapacity - paletteOffset || packedWords.Length > session.MaterializedPackedWordCapacity - wordOffset)
        {
            session.Fail(NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return false;
        }

        palette.CopyTo(session.MaterializedPalette.Slice(paletteOffset, palette.Length));
        packedWords.CopyTo(session.MaterializedPackedWords.Slice(wordOffset, packedWords.Length));
        state.MaterializedPaletteCursor = checked(paletteOffset + palette.Length);
        state.MaterializedPackedWordCursor = checked(wordOffset + packedWords.Length);
        session.MaterializedSections[sectionRecordIndex] = new NativeMaterializedSectionRecord
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
        PublishImportedSection(ref session, materializedChunkIndex, mapIndex, sectionRecordIndex);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool CopyGeneratedSection(scoped ref NativeGtrtSessionView session, int chunkIndex, int sectionIndex, scoped Span<ushort> destination)
    {
        destination.Clear();
        GetSectionCoordinates(ref session, sectionIndex, out int sectionX, out int sectionY, out int sectionZ);
        int baseX = sectionX * VoxelSection.Size;
        int baseY = sectionY * VoxelSection.Size;
        int baseZ = sectionZ * VoxelSection.Size;
        int endX = Math.Min(baseX + VoxelSection.Size, session.ChunkSizeX);
        int endY = Math.Min(baseY + VoxelSection.Size, session.ChunkSizeY);
        int endZ = Math.Min(baseZ + VoxelSection.Size, session.ChunkSizeZ);
        for (int localZ = baseZ; localZ < endZ; localZ++)
        {
            for (int localX = baseX; localX < endX; localX++)
            {
                for (int localY = baseY; localY < endY; localY++)
                {
                    if (!NativeGeneratedTerrain.TryGetGeneratedBlock(ref session, chunkIndex, localX, localY, localZ, out ushort blockId))
                    {
                        return false;
                    }

                    destination[GetSectionLocalIndex(localX, localY, localZ)] = blockId;
                }
            }
        }

        return true;
    }

    private static bool CanWrite(scoped ref NativeGtrtSessionView session)
    {
        ref NativeGtrtSessionState state = ref session.State;
        return state.PublicationState == 1 && Volatile.Read(ref state.ClaimedGenerationCount) == 0 && Volatile.Read(ref state.ClaimedMeshCount) == 0 && Volatile.Read(ref state.PacketConsumerCount) == 0 && Volatile.Read(ref state.DisposalState) == 0;
    }

    private static bool CanImport(scoped ref NativeGtrtSessionView session)
    {
        ref NativeGtrtSessionState state = ref session.State;
        if (state.PublicationState is not (0 or 1) || state.TransactionOpen != 0)
            return false;
        if (state.PublicationState == 1 && (state.RemainingColumns != 0 || state.RemainingChunks != 0 || state.ReadyPacketCount != 0))
            return false;
        return state.FailureCode == 0 && Volatile.Read(ref state.ClaimedGenerationCount) == 0 && Volatile.Read(ref state.ClaimedMeshCount) == 0 && Volatile.Read(ref state.PacketConsumerCount) == 0 && Volatile.Read(ref state.DisposalState) == 0;
    }

    private static bool CanImportSection(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int sectionIndex, out int mapIndex, out int sectionRecordIndex)
    {
        mapIndex = -1;
        sectionRecordIndex = -1;
        if (!CanImport(ref session) || (uint)materializedChunkIndex >= (uint)session.State.MaterializedChunkCount || (uint)sectionIndex >= (uint)session.SectionsPerChunk || session.State.MaterializedSectionCount >= session.MaterializedSectionCapacity)
        {
            return false;
        }

        NativeMaterializedChunkRecord chunk = session.MaterializedChunks[materializedChunkIndex];
        if (chunk.State != ActiveRecord || chunk.SectionMapOffset != materializedChunkIndex * session.SectionsPerChunk)
        {
            return false;
        }

        mapIndex = checked(chunk.SectionMapOffset + sectionIndex);
        if (session.MaterializedSectionMaps[mapIndex] >= 0)
            return false;
        sectionRecordIndex = session.State.MaterializedSectionCount;
        return true;
    }

    private static void PublishImportedSection(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int mapIndex, int sectionRecordIndex)
    {
        session.MaterializedSectionMaps[mapIndex] = sectionRecordIndex;
        session.State.MaterializedSectionCount++;
        ref NativeMaterializedChunkRecord chunk = ref session.MaterializedChunks[materializedChunkIndex];
        chunk.Revision = checked(chunk.Revision + 1);
        int activeChunkIndex = session.GetChunkIndex(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ);
        if (activeChunkIndex >= 0)
        {
            Attach(ref session, ref session.Chunks[activeChunkIndex], materializedChunkIndex, in chunk);
        }
    }

    private static int TryAllocateRawSection(scoped ref NativeGtrtSessionView session)
    {
        ref NativeGtrtSessionState state = ref session.State;
        if (state.MaterializedRawSectionCount >= session.MaterializedRawSectionCapacity)
        {
            session.Fail(NativeGtrtFailureCode.MaterializedSectionStorageExhausted);
            return -1;
        }

        int rawSectionIndex = state.MaterializedRawSectionCount++;
        return checked(rawSectionIndex * VoxelSection.VoxelCount);
    }

    private static bool TryDecodePackedSection(scoped ref NativeGtrtSessionView session, scoped in NativeMaterializedSectionRecord section, Span<ushort> destination)
    {
        if (destination.Length != VoxelSection.VoxelCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        for (int index = 0; index < destination.Length; index++)
        {
            if (!TryReadPackedBlock(ref session, in section, index, out destination[index]))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadPackedIndex(scoped ReadOnlySpan<uint> words, int bitsPerIndex, int voxelIndex)
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
    private static bool TryReadPackedBlock(scoped ref NativeGtrtSessionView session, scoped in NativeMaterializedSectionRecord section, int voxelIndex, out ushort blockId)
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

        ReadOnlySpan<uint> words = session.MaterializedPackedWords.Slice(section.PackedWordOffset, section.PackedWordCount);
        uint value = words[wordIndex] >> bitOffset;
        int remaining = 32 - bitOffset;
        if (remaining < bitsPerIndex)
        {
            if ((uint)(wordIndex + 1) >= (uint)words.Length)
            {
                session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
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

        blockId = session.MaterializedPalette[section.PaletteOffset + paletteIndex];
        return true;
    }

    private static bool TryGetChunkRecord(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int chunkX, int chunkY, int chunkZ, out NativeMaterializedChunkRecord materialized)
    {
        int count = session.State.MaterializedChunkCount;
        if ((uint)materializedChunkIndex >= (uint)count || (uint)count > (uint)session.MaterializedChunks.Length)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            materialized = default;
            return false;
        }

        materialized = session.MaterializedChunks[materializedChunkIndex];
        if (materialized.State != ActiveRecord || materialized.ChunkX != chunkX || materialized.ChunkY != chunkY || materialized.ChunkZ != chunkZ || materialized.SectionMapOffset != materializedChunkIndex * session.SectionsPerChunk)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        return true;
    }

    private static bool TryGetSectionRecord(scoped ref NativeGtrtSessionView session, int materializedChunkIndex, int sectionIndex, int sectionRecordIndex, out NativeMaterializedSectionRecord section)
    {
        int count = session.State.MaterializedSectionCount;
        if ((uint)sectionRecordIndex >= (uint)count || (uint)count > (uint)session.MaterializedSections.Length)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            section = default;
            return false;
        }

        section = session.MaterializedSections[sectionRecordIndex];
        if (section.OwnerChunkIndex != materializedChunkIndex || section.SectionIndex != sectionIndex)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMaterializedTerrain);
            return false;
        }

        bool validStorage = section.StorageKind switch
        {
            NativeSectionStorageKind.Uniform => section.RawVoxelOffset == -1 && section.PaletteOffset == -1 && section.PackedWordOffset == -1,
            NativeSectionStorageKind.Raw => section.RawVoxelOffset >= 0 && section.RawVoxelOffset % VoxelSection.VoxelCount == 0 && section.RawVoxelOffset <= session.MaterializedRawVoxels.Length - VoxelSection.VoxelCount,
            NativeSectionStorageKind.Packed => section.RawVoxelOffset == -1 && section.BitsPerIndex is> 0 and <= 16 && section.PaletteCount > 0 && section.PaletteOffset >= 0 && section.PaletteOffset <= session.MaterializedPalette.Length - section.PaletteCount && section.PackedWordCount > 0 && section.PackedWordOffset >= 0 && section.PackedWordOffset <= session.MaterializedPackedWords.Length - section.PackedWordCount,
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
    internal static int GetSectionIndex(scoped ref NativeGtrtSessionView session, int x, int y, int z, bool coordinatesAreSections = false)
    {
        int sectionX = coordinatesAreSections ? x : x / VoxelSection.Size;
        int sectionY = coordinatesAreSections ? y : y / VoxelSection.Size;
        int sectionZ = coordinatesAreSections ? z : z / VoxelSection.Size;
        return checked(((sectionX * session.SectionCountY) + sectionY) * session.SectionCountZ + sectionZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetSectionLocalIndex(int x, int y, int z) => (((z & (VoxelSection.Size - 1)) * VoxelSection.Size) + (x & (VoxelSection.Size - 1))) * VoxelSection.Size + (y & (VoxelSection.Size - 1));
    private static void GetSectionCoordinates(scoped ref NativeGtrtSessionView session, int sectionIndex, out int sectionX, out int sectionY, out int sectionZ)
    {
        sectionZ = sectionIndex % session.SectionCountZ;
        int remaining = sectionIndex / session.SectionCountZ;
        sectionY = remaining % session.SectionCountY;
        sectionX = remaining / session.SectionCountY;
    }

    private static void Attach(scoped ref NativeGtrtSessionView session, ref NativeChunkRecord chunk, int materializedChunkIndex, scoped in NativeMaterializedChunkRecord materialized)
    {
        session.IndexMaterializedChunk(materializedChunkIndex);
        chunk.MaterializedChunkIndex = materializedChunkIndex;
        chunk.StorageKind = materialized.StorageKind;
        chunk.DirtyRevision = materialized.Revision;
    }
}

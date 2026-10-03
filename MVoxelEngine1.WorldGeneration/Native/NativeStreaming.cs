using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;

internal ref partial struct NativeGtrtSessionView
{
    private Span<NativePacketWordRange> FreeRanges =>
        ReadRange<NativePacketWordRange>(header.Streaming.FreeRanges, header.Streaming.FreeRangeCapacity);

    private Span<NativeRenderPacketRecord> PreviousPackets =>
        ReadRange<NativeRenderPacketRecord>(header.Streaming.PacketsBackup, header.ChunkCount);

    internal NativeStreamingStatistics StreamingStatistics => new(
        State.PlannedColumns, State.RetainedColumns, State.PlannedMeshes, State.RetainedPackets);

    internal void InitializePacketRanges()
    {
        State.PacketWordCursor = 0;
        State.FreeRangeCount = 1;
        State.PacketAllocationLock = 0;
        FreeRanges[0] = new NativePacketWordRange { Count = header.PacketWordCapacity };
    }

    private bool TryPrepareStreamingRun(int centerX, int centerY, int centerZ)
    {
        foreach (ref readonly NativeRenderPacketRecord packet in Packets)
        {
            if (packet.State is not (NativeRenderPacketState.Empty or NativeRenderPacketState.Retired))
                return false;
        }
        SaveStreamingState();
        ref NativeGtrtSessionState state = ref State;
        int epoch = checked(state.SessionEpoch + 1);
        state.CenterChunkX = centerX;
        state.CenterChunkY = centerY;
        state.CenterChunkZ = centerZ;
        state.SessionEpoch = epoch;
        state.GenerationCursor = 0;
        state.MeshCursor = 0;
        state.RemainingColumns = 0;
        state.RemainingChunks = 0;
        state.PlannedColumns = 0;
        state.PlannedMeshes = 0;
        state.RetainedColumns = 0;
        state.RetainedPackets = 0;
        state.ReadyPacketCount = 0;
        state.FailureCode = 0;
        state.CancellationState = 0;
        state.MeshEnqueuePosition = 0;
        state.MeshDequeuePosition = 0;
        GenerationWorkspaces.Clear();
        MeshWorkspaces.Clear();
        for (int index = 0; index < MeshReadySlots.Length; index++)
            MeshReadySlots[index] = new NativeReadySlot { Sequence = index };

        Span<byte> changedColumns = ReadRange<byte>(header.Streaming.ChangedColumns, header.ColumnCount);
        for (int relativeX = header.MinimumChunkX; relativeX <= header.MaximumChunkX; relativeX++)
        {
            int chunkX = checked(centerX + relativeX);
            for (int relativeZ = header.MinimumChunkZ; relativeZ <= header.MaximumChunkZ; relativeZ++)
            {
                int chunkZ = checked(centerZ + relativeZ);
                int index = GetColumnIndex(chunkX, chunkZ);
                ref NativeColumnRecord column = ref Columns[index];
                bool retained = column.ChunkX == chunkX && column.ChunkZ == chunkZ &&
                    column.State == NativeColumnState.Generated;
                if (retained)
                {
                    column.GenerationEpoch = epoch;
                    state.RetainedColumns++;
                }
                else
                {
                    int offset = checked(index * header.ProfilesPerColumn);
                    Profiles.Slice(offset, header.ProfilesPerColumn).CopyTo(
                        ReadRange<BlockColumnProfile>(header.Streaming.ProfilesBackup, header.ProfileCount)
                            .Slice(offset, header.ProfilesPerColumn));
                    changedColumns[index] = 1;
                    column = new NativeColumnRecord
                    {
                        ChunkX = chunkX, ChunkZ = chunkZ, ProfileOffset = offset, BiomeIndex = -1
                    };
                    ColumnSummaries[index] = default;
                    state.PlannedColumns++;
                }
                GenerationJobs[index] = new NativeWorkItem
                {
                    RecordIndex = index, Epoch = epoch, Kind = NativeWorkKind.GenerateColumn,
                    State = retained ? NativeWorkState.Completed : NativeWorkState.Scheduled
                };
            }
        }

        // All columns must have their new coordinates before dependencies are counted.
        for (int relativeX = header.MinimumChunkX; relativeX <= header.MaximumChunkX; relativeX++)
        {
            int chunkX = checked(centerX + relativeX);
            for (int relativeZ = header.MinimumChunkZ; relativeZ <= header.MaximumChunkZ; relativeZ++)
            {
                int chunkZ = checked(centerZ + relativeZ);
                int columnIndex = GetColumnIndex(chunkX, chunkZ);
                bool required = Math.Abs(relativeX) <= header.Lod1Radius && Math.Abs(relativeZ) <= header.Lod1Radius;
                bool generated = Columns[columnIndex].State == NativeColumnState.Generated;
                for (int relativeY = header.MinimumChunkY; relativeY <= header.MaximumChunkY; relativeY++)
                {
                    int chunkY = checked(centerY + relativeY);
                    int index = GetChunkIndex(chunkX, chunkY, chunkZ);
                    NativeChunkRecord previous = Chunks[index];
                    ref NativeRenderPacketRecord packet = ref Packets[index];
                    bool retained = required && generated && previous.ChunkX == chunkX &&
                        previous.ChunkY == chunkY && previous.ChunkZ == chunkZ &&
                        (previous.Flags & (int)NativeChunkFlags.MeshInvalidated) == 0 &&
                        previous.State == NativeChunkState.Retired && packet.State == NativeRenderPacketState.Retired;
                    int materializedIndex = FindMaterializedChunkIndex(chunkX, chunkY, chunkZ);
                    int dependencies = required && !retained ? CountPendingColumns(chunkX, chunkZ) : 0;
                    Chunks[index] = new NativeChunkRecord
                    {
                        ChunkX = chunkX, ChunkY = chunkY, ChunkZ = chunkZ,
                        ColumnIndex = columnIndex, ProfileOffset = columnIndex * header.ProfilesPerColumn,
                        PacketIndex = index, MaterializedChunkIndex = materializedIndex,
                        StorageKind = materializedIndex < 0 ? NativeChunkStorageKind.GeneratedProfile :
                            MaterializedChunks[materializedIndex].StorageKind,
                        DirtyRevision = materializedIndex < 0 ? 0 : MaterializedChunks[materializedIndex].Revision,
                        Flags = required ? (int)NativeChunkFlags.InitialMeshRequired : 0,
                        RemainingDependencies = dependencies,
                        GenerationEpoch = generated ? epoch : 0,
                        MeshEpoch = retained ? epoch : 0,
                        State = retained ? NativeChunkState.Retired :
                            generated ? NativeChunkState.Generated : NativeChunkState.Empty
                    };
                    MeshJobs[index] = new NativeWorkItem
                    {
                        RecordIndex = index, Epoch = epoch, Kind = NativeWorkKind.BuildChunkMesh,
                        State = retained ? NativeWorkState.Completed : !required ? NativeWorkState.Canceled :
                            dependencies == 0 ? NativeWorkState.Scheduled : NativeWorkState.Waiting
                    };
                    if (retained)
                    {
                        packet.PublicationEpoch = epoch;
                        packet.RegistryEpoch = epoch;
                        state.RetainedPackets++;
                        continue;
                    }
                    packet = default;
                    if (!required)
                        continue;
                    state.PlannedMeshes++;
                    if (dependencies == 0 && !TryEnqueueMeshReady(index, epoch))
                        return false;
                }
            }
        }
        state.RemainingColumns = state.PlannedColumns;
        state.RemainingChunks = state.PlannedMeshes;
        return true;
    }

    private int CountPendingColumns(int chunkX, int chunkZ) =>
        PendingColumn(chunkX, chunkZ) + PendingColumn(chunkX - 1, chunkZ) +
        PendingColumn(chunkX + 1, chunkZ) + PendingColumn(chunkX, chunkZ - 1) + PendingColumn(chunkX, chunkZ + 1);

    private int PendingColumn(int chunkX, int chunkZ) =>
        Columns[GetColumnIndex(chunkX, chunkZ)].State == NativeColumnState.Generated ? 0 : 1;

    private void SaveStreamingState()
    {
        NativeStreamingLayout layout = header.Streaming;
        ReadRange<NativeGtrtSessionState>(layout.StateBackup, 1)[0] = State;
        Columns.CopyTo(ReadRange<NativeColumnRecord>(layout.ColumnsBackup, header.ColumnCount));
        ColumnSummaries.CopyTo(ReadRange<NativeColumnSummary>(layout.SummariesBackup, header.ColumnCount));
        Chunks.CopyTo(ReadRange<NativeChunkRecord>(layout.ChunksBackup, header.ChunkCount));
        GenerationJobs.CopyTo(ReadRange<NativeWorkItem>(layout.GenerationJobsBackup, header.ColumnCount));
        MeshJobs.CopyTo(ReadRange<NativeWorkItem>(layout.MeshJobsBackup, header.ChunkCount));
        Packets.CopyTo(PreviousPackets);
        MeshReadySlots.CopyTo(ReadRange<NativeReadySlot>(layout.ReadySlotsBackup, header.RequiredChunkCount));
        FreeRanges.CopyTo(ReadRange<NativePacketWordRange>(layout.FreeRangesBackup, layout.FreeRangeCapacity));
        ReadRange<byte>(layout.ChangedColumns, header.ColumnCount).Clear();
        State.TransactionOpen = 1;
    }

    internal void CommitStreamingRun()
    {
        if (State.TransactionOpen == 0)
            return;
        for (int index = 0; index < PreviousPackets.Length; index++)
        {
            NativeRenderPacketRecord previous = PreviousPackets[index];
            if (previous.State != NativeRenderPacketState.Empty &&
                previous.RenderDataId != Packets[index].RenderDataId)
                ReleasePacketRange(previous.OpaqueWordOffset,
                    checked(previous.OpaqueWordCount + previous.TransparentWordCount));
        }
        State.TransactionOpen = 0;
    }

    internal bool RollbackStreamingRun()
    {
        if (State.TransactionOpen == 0)
            return false;
        if (State.ClaimedGenerationCount != 0 || State.ClaimedMeshCount != 0 || State.PacketConsumerCount != 0)
            throw new InvalidOperationException("Native streaming rollback requires idle workers and packet readers.");
        NativeStreamingLayout layout = header.Streaming;
        Span<byte> changed = ReadRange<byte>(layout.ChangedColumns, header.ColumnCount);
        Span<BlockColumnProfile> backup = ReadRange<BlockColumnProfile>(layout.ProfilesBackup, header.ProfileCount);
        for (int index = 0; index < changed.Length; index++)
        {
            if (changed[index] != 0)
            {
                int offset = index * header.ProfilesPerColumn;
                backup.Slice(offset, header.ProfilesPerColumn).CopyTo(Profiles.Slice(offset, header.ProfilesPerColumn));
            }
        }
        ReadRange<NativeColumnRecord>(layout.ColumnsBackup, header.ColumnCount).CopyTo(Columns);
        ReadRange<NativeColumnSummary>(layout.SummariesBackup, header.ColumnCount).CopyTo(ColumnSummaries);
        ReadRange<NativeChunkRecord>(layout.ChunksBackup, header.ChunkCount).CopyTo(Chunks);
        ReadRange<NativeWorkItem>(layout.GenerationJobsBackup, header.ColumnCount).CopyTo(GenerationJobs);
        ReadRange<NativeWorkItem>(layout.MeshJobsBackup, header.ChunkCount).CopyTo(MeshJobs);
        PreviousPackets.CopyTo(Packets);
        ReadRange<NativeReadySlot>(layout.ReadySlotsBackup, header.RequiredChunkCount).CopyTo(MeshReadySlots);
        ReadRange<NativePacketWordRange>(layout.FreeRangesBackup, layout.FreeRangeCapacity).CopyTo(FreeRanges);
        State = ReadRange<NativeGtrtSessionState>(layout.StateBackup, 1)[0];
        GenerationWorkspaces.Clear();
        MeshWorkspaces.Clear();
        return true;
    }

    internal void InvalidateEditedMeshes(int chunkX, int chunkY, int chunkZ, int localX, int localY, int localZ)
    {
        InvalidateMesh(chunkX, chunkY, chunkZ);
        if (localX == 0) InvalidateMesh(chunkX - 1, chunkY, chunkZ);
        if (localX == header.ChunkSizeX - 1) InvalidateMesh(chunkX + 1, chunkY, chunkZ);
        if (localY == 0) InvalidateMesh(chunkX, chunkY - 1, chunkZ);
        if (localY == header.ChunkSizeY - 1) InvalidateMesh(chunkX, chunkY + 1, chunkZ);
        if (localZ == 0) InvalidateMesh(chunkX, chunkY, chunkZ - 1);
        if (localZ == header.ChunkSizeZ - 1) InvalidateMesh(chunkX, chunkY, chunkZ + 1);
    }

    private void InvalidateMesh(int chunkX, int chunkY, int chunkZ)
    {
        int index = GetChunkIndex(chunkX, chunkY, chunkZ);
        if (index >= 0)
            Chunks[index].Flags |= (int)NativeChunkFlags.MeshInvalidated;
    }

    private bool TryAllocatePacketRange(int wordCount, out int wordOffset)
    {
        wordOffset = 0;
        if (wordCount == 0)
            return true;
        ref NativeGtrtSessionState state = ref State;
        while (Interlocked.CompareExchange(ref state.PacketAllocationLock, 1, 0) != 0)
            Thread.SpinWait(4);
        try
        {
            for (int index = 0; index < state.FreeRangeCount; index++)
            {
                ref NativePacketWordRange range = ref FreeRanges[index];
                if (range.Count < wordCount)
                    continue;
                wordOffset = range.Offset;
                range.Offset += wordCount;
                range.Count -= wordCount;
                state.PacketWordCursor = Math.Max(state.PacketWordCursor, range.Offset);
                if (range.Count == 0)
                    RemoveFreeRange(index);
                return true;
            }
            return false;
        }
        finally
        {
            Volatile.Write(ref state.PacketAllocationLock, 0);
        }
    }

    // Runs on the owner thread only after every worker and borrowed reader has finished.
    private void ReleasePacketRange(int offset, int count)
    {
        if (count == 0)
            return;
        Span<NativePacketWordRange> ranges = FreeRanges;
        int index = 0;
        while (index < State.FreeRangeCount && ranges[index].Offset < offset)
            index++;
        if (index > 0 && ranges[index - 1].Offset + ranges[index - 1].Count == offset)
        {
            ranges[index - 1].Count += count;
            index--;
        }
        else
        {
            if (State.FreeRangeCount == ranges.Length)
                throw new InvalidOperationException("Native packet free-range storage is exhausted.");
            ranges.Slice(index, State.FreeRangeCount - index).CopyTo(ranges.Slice(index + 1));
            ranges[index] = new NativePacketWordRange { Offset = offset, Count = count };
            State.FreeRangeCount++;
        }
        if (index + 1 < State.FreeRangeCount &&
            ranges[index].Offset + ranges[index].Count == ranges[index + 1].Offset)
        {
            ranges[index].Count += ranges[index + 1].Count;
            RemoveFreeRange(index + 1);
        }
    }

    private void RemoveFreeRange(int index)
    {
        FreeRanges.Slice(index + 1, State.FreeRangeCount - index - 1).CopyTo(FreeRanges.Slice(index));
        State.FreeRangeCount--;
    }
}

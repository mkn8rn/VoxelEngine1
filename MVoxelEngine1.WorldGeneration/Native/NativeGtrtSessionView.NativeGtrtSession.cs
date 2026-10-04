using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal readonly ref partial struct NativeGtrtSessionView
{
    private readonly Span<byte> bytes;
    private readonly NativeGtrtSessionHeader header;
    internal NativeGtrtSessionView(Span<byte> bytes)
    {
        if (bytes.Length < Unsafe.SizeOf<NativeGtrtSessionHeader>())
        {
            throw new InvalidDataException("The native GTRT session header is incomplete.");
        }

        header = MemoryMarshal.Read<NativeGtrtSessionHeader>(bytes);
        if (header.Magic != NativeGtrtSessionHeader.ExpectedMagic || header.Version != NativeGtrtSessionHeader.ExpectedVersion || header.TotalByteCount != bytes.Length)
        {
            throw new InvalidDataException("The native GTRT session header is invalid.");
        }

        this.bytes = bytes;
        ValidateRange<int>(header.MaterializedIndexOffset, header.MaterializedIndexCapacity);
        ValidateRange<NativeGtrtSessionState>(header.StateOffset, 1);
        ValidateRange<byte>(header.NoiseStateOffset, NativeOpenSimplexNoiseState.StateByteCount);
        ValidateRange<NativeTerrainMaterialSet>(header.MaterialOffset, 1);
        ValidateRange<NativeColumnRecord>(header.ColumnOffset, header.ColumnCount);
        ValidateRange<BlockColumnProfile>(header.ProfileOffset, header.ProfileCount);
        ValidateRange<NativeColumnSummary>(header.ColumnSummaryOffset, header.ColumnCount);
        ValidateRange<NativeGenerationWorkspaceRecord>(header.GenerationWorkspaceOffset, header.GenerationWorkerCount);
        ValidateRange<float>(header.GenerationFloatScratchOffset, checked(header.GenerationWorkerCount * header.GenerationFloatCountPerWorker));
        ValidateRange<TerrainGenerationUtils.NoiseAxisSample>(header.GenerationXScratchOffset, checked(header.GenerationWorkerCount * header.ChunkSizeX));
        ValidateRange<TerrainGenerationUtils.NoiseAxisSample>(header.GenerationZScratchOffset, checked(header.GenerationWorkerCount * header.ChunkSizeZ));
        ValidateRange<float>(header.GenerationLatticeScratchOffset, checked(header.GenerationWorkerCount * header.GenerationLatticeCountPerWorker));
        ValidateRange<NativeMeshWorkspaceRecord>(header.MeshWorkspaceOffset, header.MeshWorkerCount);
        ValidateRange<int>(header.MeshFaceScratchOffset, checked(header.MeshWorkerCount * header.MeshFaceScratchCountPerWorker));
        ValidateRange<NativeChunkRecord>(header.ChunkOffset, header.ChunkCount);
        ValidateRange<NativeMaterializedChunkRecord>(header.MaterializedChunkOffset, header.MaterializedChunkCapacity);
        ValidateRange<int>(header.MaterializedSectionMapOffset, header.MaterializedSectionMapCount);
        ValidateRange<NativeMaterializedSectionRecord>(header.MaterializedSectionOffset, header.MaterializedSectionCapacity);
        ValidateRange<ushort>(header.MaterializedRawVoxelOffset, header.MaterializedRawVoxelCount);
        ValidateRange<ushort>(header.MaterializedPaletteOffset, header.MaterializedPaletteCapacity);
        ValidateRange<uint>(header.MaterializedPackedWordOffset, header.MaterializedPackedWordCapacity);
        ValidateRange<NativeWorkItem>(header.GenerationJobOffset, header.ColumnCount);
        ValidateRange<NativeWorkItem>(header.MeshJobOffset, header.ChunkCount);
        ValidateRange<NativeRenderPacketRecord>(header.PacketOffset, header.ChunkCount);
        ValidateRange<uint>(header.PacketWordOffset, header.PacketWordCapacity);
        ValidateRange<NativeReadySlot>(header.MeshReadyOffset, header.RequiredChunkCount);
        ValidateRange<byte>(header.GameSnapshotOffset, header.GameSnapshotByteCount);
    }

    internal ref NativeGtrtSessionState State => ref ReadRange<NativeGtrtSessionState>(header.StateOffset, 1)[0];
    internal NativeOpenSimplexNoiseState NoiseState => new(ReadRange<byte>(header.NoiseStateOffset, NativeOpenSimplexNoiseState.StateByteCount));
    internal ref readonly NativeTerrainMaterialSet Materials => ref ReadRange<NativeTerrainMaterialSet>(header.MaterialOffset, 1)[0];
    internal Span<NativeColumnRecord> Columns => ReadRange<NativeColumnRecord>(header.ColumnOffset, header.ColumnCount);
    internal Span<BlockColumnProfile> Profiles => ReadRange<BlockColumnProfile>(header.ProfileOffset, header.ProfileCount);
    internal Span<NativeColumnSummary> ColumnSummaries => ReadRange<NativeColumnSummary>(header.ColumnSummaryOffset, header.ColumnCount);
    internal Span<NativeGenerationWorkspaceRecord> GenerationWorkspaces => ReadRange<NativeGenerationWorkspaceRecord>(header.GenerationWorkspaceOffset, header.GenerationWorkerCount);
    internal Span<NativeChunkRecord> Chunks => ReadRange<NativeChunkRecord>(header.ChunkOffset, header.ChunkCount);
    internal Span<NativeMaterializedChunkRecord> MaterializedChunks => ReadRange<NativeMaterializedChunkRecord>(header.MaterializedChunkOffset, header.MaterializedChunkCapacity);
    internal Span<int> MaterializedSectionMaps => ReadRange<int>(header.MaterializedSectionMapOffset, header.MaterializedSectionMapCount);
    internal Span<NativeMaterializedSectionRecord> MaterializedSections => ReadRange<NativeMaterializedSectionRecord>(header.MaterializedSectionOffset, header.MaterializedSectionCapacity);
    internal Span<ushort> MaterializedRawVoxels => ReadRange<ushort>(header.MaterializedRawVoxelOffset, header.MaterializedRawVoxelCount);
    internal Span<ushort> MaterializedPalette => ReadRange<ushort>(header.MaterializedPaletteOffset, header.MaterializedPaletteCapacity);
    internal Span<uint> MaterializedPackedWords => ReadRange<uint>(header.MaterializedPackedWordOffset, header.MaterializedPackedWordCapacity);
    internal Span<NativeWorkItem> GenerationJobs => ReadRange<NativeWorkItem>(header.GenerationJobOffset, header.ColumnCount);
    internal Span<NativeWorkItem> MeshJobs => ReadRange<NativeWorkItem>(header.MeshJobOffset, header.ChunkCount);
    internal Span<NativeMeshWorkspaceRecord> MeshWorkspaces => ReadRange<NativeMeshWorkspaceRecord>(header.MeshWorkspaceOffset, header.MeshWorkerCount);
    internal Span<NativeRenderPacketRecord> Packets => ReadRange<NativeRenderPacketRecord>(header.PacketOffset, header.ChunkCount);
    internal Span<uint> PacketWords => ReadRange<uint>(header.PacketWordOffset, header.PacketWordCapacity);
    internal Span<NativeReadySlot> MeshReadySlots => ReadRange<NativeReadySlot>(header.MeshReadyOffset, header.RequiredChunkCount);
    internal Span<byte> GameSnapshotBytes => ReadRange<byte>(header.GameSnapshotOffset, header.GameSnapshotByteCount);
    internal NativeGameSnapshotView GameSnapshot => new(GameSnapshotBytes);
    internal bool HasGameSnapshot => header.GameSnapshotByteCount != 0;

    internal bool TryGetBlockDescriptor(ushort blockId, out NativeBlockDescriptor descriptor)
    {
        if (blockId == 0)
        {
            descriptor = default;
            return true;
        }

        if (HasGameSnapshot)
        {
            ReadOnlySpan<NativeBlockDescriptor> blocks = GameSnapshot.Blocks;
            if ((uint)blockId >= (uint)blocks.Length)
            {
                descriptor = default;
                return false;
            }

            descriptor = blocks[blockId];
            return descriptor.Id == blockId && descriptor.HasFlag(NativeBlockFlags.Defined);
        }

        if (blockId == Materials.Stone.Id)
            descriptor = Materials.Stone;
        else if (blockId == Materials.Soil.Id)
            descriptor = Materials.Soil;
        else if (blockId == Materials.Water.Id)
            descriptor = Materials.Water;
        else
        {
            descriptor = default;
            return false;
        }

        return true;
    }

    internal bool TryIsBlockOpaque(ushort blockId, out bool opaque)
    {
        if (!TryGetBlockDescriptor(blockId, out NativeBlockDescriptor block))
        {
            opaque = false;
            return false;
        }

        opaque = blockId != 0 && block.HasFlag(NativeBlockFlags.Opaque);
        return true;
    }

    internal int ColumnCount => header.ColumnCount;
    internal int ChunkCount => header.ChunkCount;
    internal int ProfileCount => header.ProfileCount;
    internal int ProfilesPerColumn => header.ProfilesPerColumn;
    internal int ChunkSizeX => header.ChunkSizeX;
    internal int ChunkSizeY => header.ChunkSizeY;
    internal int ChunkSizeZ => header.ChunkSizeZ;
    internal int GenerationWorkerCount => header.GenerationWorkerCount;
    internal int MeshWorkerCount => header.MeshWorkerCount;
    internal int PacketWordCapacity => header.PacketWordCapacity;
    internal int RequiredChunkCount => header.RequiredChunkCount;
    internal int SectionCountX => header.SectionCountX;
    internal int SectionCountY => header.SectionCountY;
    internal int SectionCountZ => header.SectionCountZ;
    internal int SectionsPerChunk => header.SectionsPerChunk;
    internal NativeGtrtSessionHeader SessionHeader => header;
    internal int MaterializedChunkCapacity => header.MaterializedChunkCapacity;
    internal int MaterializedSectionCapacity => header.MaterializedSectionCapacity;
    internal int MaterializedRawSectionCapacity => header.MaterializedRawSectionCapacity;
    internal int MaterializedPaletteCapacity => header.MaterializedPaletteCapacity;
    internal int MaterializedPackedWordCapacity => header.MaterializedPackedWordCapacity;

    internal Span<BlockColumnProfile> GetColumnProfiles(int columnIndex) => Profiles.Slice(checked(columnIndex * header.ProfilesPerColumn), header.ProfilesPerColumn);
    internal Span<float> GetGenerationFloatScratch(int workerIndex) => ReadRange<float>(checked(header.GenerationFloatScratchOffset + workerIndex * header.GenerationFloatCountPerWorker * Unsafe.SizeOf<float>()), header.GenerationFloatCountPerWorker);
    internal Span<TerrainGenerationUtils.NoiseAxisSample> GetGenerationXScratch(int workerIndex) => ReadRange<TerrainGenerationUtils.NoiseAxisSample>(checked(header.GenerationXScratchOffset + workerIndex * header.ChunkSizeX * Unsafe.SizeOf<TerrainGenerationUtils.NoiseAxisSample>()), header.ChunkSizeX);
    internal Span<TerrainGenerationUtils.NoiseAxisSample> GetGenerationZScratch(int workerIndex) => ReadRange<TerrainGenerationUtils.NoiseAxisSample>(checked(header.GenerationZScratchOffset + workerIndex * header.ChunkSizeZ * Unsafe.SizeOf<TerrainGenerationUtils.NoiseAxisSample>()), header.ChunkSizeZ);
    internal Span<float> GetGenerationLatticeScratch(int workerIndex) => ReadRange<float>(checked(header.GenerationLatticeScratchOffset + workerIndex * header.GenerationLatticeCountPerWorker * Unsafe.SizeOf<float>()), header.GenerationLatticeCountPerWorker);
    internal Span<int> GetMeshBottomFaceScratch(int workerIndex) => ReadRange<int>(GetMeshScratchOffset(workerIndex), header.ChunkSizeX * header.ChunkSizeZ);
    internal Span<int> GetMeshTopFaceScratch(int workerIndex) => ReadRange<int>(checked(GetMeshScratchOffset(workerIndex) + GetMeshFacePlaneCount() * Unsafe.SizeOf<int>()), header.ChunkSizeX * header.ChunkSizeZ);
    internal Span<int> GetMeshNegativeFaceScratch(int workerIndex) => ReadRange<int>(GetMeshScratchOffset(workerIndex), GetMeshFacePlaneCount());
    internal Span<int> GetMeshPositiveFaceScratch(int workerIndex) => ReadRange<int>(checked(GetMeshScratchOffset(workerIndex) + GetMeshFacePlaneCount() * Unsafe.SizeOf<int>()), GetMeshFacePlaneCount());
    internal bool TryAcquireGenerationWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.GenerationWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
            return false;
        }

        Span<NativeGenerationWorkspaceRecord> workspaces = GenerationWorkspaces;
        ref NativeGenerationWorkspaceRecord workspace = ref workspaces[workerIndex];
        if (Interlocked.CompareExchange(ref workspace.State, 1, 0) != 0)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
            return false;
        }

        workspace.Epoch = State.SessionEpoch;
        return true;
    }

    internal void ReleaseGenerationWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.GenerationWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
            return;
        }

        Span<NativeGenerationWorkspaceRecord> workspaces = GenerationWorkspaces;
        ref NativeGenerationWorkspaceRecord workspace = ref workspaces[workerIndex];
        workspace.Epoch = 0;
        if (Interlocked.CompareExchange(ref workspace.State, 0, 1) != 1)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationWorkspace);
        }
    }

    internal bool TryAcquireMeshWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.MeshWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return false;
        }

        Span<NativeMeshWorkspaceRecord> workspaces = MeshWorkspaces;
        ref NativeMeshWorkspaceRecord workspace = ref workspaces[workerIndex];
        if (Interlocked.CompareExchange(ref workspace.State, 1, 0) != 0)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return false;
        }

        workspace.Epoch = State.SessionEpoch;
        return true;
    }

    internal void ReleaseMeshWorkspace(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.MeshWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return;
        }

        Span<NativeMeshWorkspaceRecord> workspaces = MeshWorkspaces;
        ref NativeMeshWorkspaceRecord workspace = ref workspaces[workerIndex];
        workspace.Epoch = 0;
        if (Interlocked.CompareExchange(ref workspace.State, 0, 1) != 1)
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
    }

    internal bool TryBeginPacket(scoped ref readonly NativeWorkItem claimedWork, int opaqueWordCount, int opaqueFaceCount, int transparentWordCount, int transparentFaceCount, out NativePacketWriteView packet)
    {
        packet = default;
        if (claimedWork.Kind != NativeWorkKind.BuildChunkMesh || claimedWork.Epoch != State.SessionEpoch || (uint)claimedWork.RecordIndex >= (uint)Chunks.Length || opaqueWordCount < 0 || transparentWordCount < 0 || opaqueWordCount > int.MaxValue - transparentWordCount || (opaqueWordCount & 1) != 0 || (transparentWordCount & 1) != 0 || opaqueFaceCount < opaqueWordCount / 2 || transparentFaceCount < transparentWordCount / 2)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        if (Volatile.Read(ref State.DisposalState) != 0 || Volatile.Read(ref State.PacketRecycleState) != 0)
            return false;
        int chunkIndex = claimedWork.RecordIndex;
        ref NativeWorkItem job = ref MeshJobs[chunkIndex];
        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        if (ReadState(ref job.State) != NativeWorkState.Claimed || ReadState(ref chunk.State) != NativeChunkState.MeshReady)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        ref NativeRenderPacketRecord record = ref Packets[chunk.PacketIndex];
        if (!TryTransition(ref record.State, NativeRenderPacketState.Empty, NativeRenderPacketState.Writing))
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        record.ChunkIndex = chunkIndex;
        record.RegistryEpoch = claimedWork.Epoch;
        record.PublicationEpoch = 0;
        int totalWordCount = checked(opaqueWordCount + transparentWordCount);
        if (!TryReservePacketWords(totalWordCount, out int wordOffset))
        {
            TryTransition(ref record.State, NativeRenderPacketState.Writing, NativeRenderPacketState.Retired);
            Fail(NativeGtrtFailureCode.PacketStorageExhausted);
            return false;
        }

        record.RenderDataId = ((long)claimedWork.Epoch << 32) | (uint)chunkIndex;
        record.OpaqueWordOffset = wordOffset;
        record.OpaqueWordCount = opaqueWordCount;
        record.OpaqueFaceCount = opaqueFaceCount;
        record.TransparentWordOffset = checked(wordOffset + opaqueWordCount);
        record.TransparentWordCount = transparentWordCount;
        record.TransparentFaceCount = transparentFaceCount;
        Span<uint> words = PacketWords;
        packet = new NativePacketWriteView(words.Slice(record.OpaqueWordOffset, opaqueWordCount), words.Slice(record.TransparentWordOffset, transparentWordCount));
        return true;
    }

    internal bool TryActivatePacket(int chunkIndex, out NativePacketReadView packet)
    {
        packet = default;
        if (!TryEnterPacketConsumer())
            return false;
        try
        {
            return TryActivatePacketCore(chunkIndex, out packet);
        }
        finally
        {
            ExitPacketConsumer();
        }
    }

    private bool TryActivatePacketCore(int chunkIndex, out NativePacketReadView packet)
    {
        packet = default;
        if ((uint)chunkIndex >= (uint)Chunks.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketActivation);
            return false;
        }

        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        ref NativeRenderPacketRecord source = ref Packets[chunk.PacketIndex];
        if (ReadState(ref chunk.State) != NativeChunkState.PacketReady || source.PublicationEpoch != State.SessionEpoch || source.ChunkIndex != chunkIndex || !TryTransition(ref source.State, NativeRenderPacketState.Ready, NativeRenderPacketState.Active))
        {
            return false;
        }

        if (!TryTransition(ref chunk.State, NativeChunkState.PacketReady, NativeChunkState.Active))
        {
            TryTransition(ref source.State, NativeRenderPacketState.Active, NativeRenderPacketState.Retired);
            Fail(NativeGtrtFailureCode.InvalidPacketActivation);
            return false;
        }

        int ready = Interlocked.Decrement(ref State.ReadyPacketCount);
        if (ready < 0)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketActivation);
            return false;
        }

        NativeRenderPacketRecord record = source;
        Span<uint> words = PacketWords;
        packet = new NativePacketReadView(record, words.Slice(record.OpaqueWordOffset, record.OpaqueWordCount), words.Slice(record.TransparentWordOffset, record.TransparentWordCount));
        return true;
    }

    internal bool TryRetirePacket(int chunkIndex)
    {
        if (!TryEnterPacketConsumer())
            return false;
        try
        {
            return TryRetirePacketCore(chunkIndex);
        }
        finally
        {
            ExitPacketConsumer();
        }
    }

    private bool TryRetirePacketCore(int chunkIndex)
    {
        if ((uint)chunkIndex >= (uint)Chunks.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketRetirement);
            return false;
        }

        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        ref NativeRenderPacketRecord packet = ref Packets[chunk.PacketIndex];
        NativeRenderPacketState packetState = ReadState(ref packet.State);
        NativeChunkState expectedChunkState;
        switch (packetState)
        {
            case NativeRenderPacketState.Ready:
                expectedChunkState = NativeChunkState.PacketReady;
                break;
            case NativeRenderPacketState.Active:
                expectedChunkState = NativeChunkState.Active;
                break;
            default:
                return false;
        }

        if (packet.PublicationEpoch != State.SessionEpoch || packet.ChunkIndex != chunkIndex || ReadState(ref chunk.State) != expectedChunkState || !TryTransition(ref packet.State, packetState, NativeRenderPacketState.Retired))
        {
            return false;
        }

        if (!TryTransition(ref chunk.State, expectedChunkState, NativeChunkState.Retired))
        {
            Fail(NativeGtrtFailureCode.InvalidPacketRetirement);
            return false;
        }

        if (packetState == NativeRenderPacketState.Ready)
        {
            int ready = Interlocked.Decrement(ref State.ReadyPacketCount);
            if (ready < 0)
            {
                Fail(NativeGtrtFailureCode.InvalidPacketRetirement);
                return false;
            }
        }

        return true;
    }

    internal bool TryAbandonMesh(scoped ref readonly NativeWorkItem claimedWork)
    {
        if (claimedWork.Kind != NativeWorkKind.BuildChunkMesh || claimedWork.Epoch != State.SessionEpoch || (uint)claimedWork.RecordIndex >= (uint)MeshJobs.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
            return false;
        }

        int chunkIndex = claimedWork.RecordIndex;
        ref NativeWorkItem job = ref MeshJobs[chunkIndex];
        ref NativeChunkRecord chunk = ref Chunks[chunkIndex];
        ref NativeRenderPacketRecord packet = ref Packets[chunk.PacketIndex];
        if (ReadState(ref job.State) != NativeWorkState.Claimed || ReadState(ref chunk.State) != NativeChunkState.MeshReady)
        {
            return false;
        }

        NativeRenderPacketState packetState = ReadState(ref packet.State);
        if (packetState == NativeRenderPacketState.Writing)
        {
            if (!TryTransition(ref packet.State, NativeRenderPacketState.Writing, NativeRenderPacketState.Retired))
            {
                Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
                return false;
            }
        }
        else if (packetState != NativeRenderPacketState.Empty && packetState != NativeRenderPacketState.Retired)
        {
            return false;
        }

        if (!TryTransition(ref chunk.State, NativeChunkState.MeshReady, NativeChunkState.Retired) || !TryTransition(ref job.State, NativeWorkState.Claimed, NativeWorkState.Canceled))
        {
            Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
            return false;
        }

        int claimed = Interlocked.Decrement(ref State.ClaimedMeshCount);
        if (claimed < 0)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshCancellation);
            return false;
        }

        return true;
    }

    internal bool TryRecyclePacketStorage()
    {
        ref NativeGtrtSessionState state = ref State;
        if (Interlocked.CompareExchange(ref state.PacketRecycleState, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            if (Volatile.Read(ref state.ClaimedMeshCount) != 0 || Volatile.Read(ref state.PacketConsumerCount) != 0 || Volatile.Read(ref state.ReadyPacketCount) != 0)
            {
                return false;
            }

            Span<NativeMeshWorkspaceRecord> workspaces = MeshWorkspaces;
            foreach (ref NativeMeshWorkspaceRecord workspace in workspaces)
            {
                if (Volatile.Read(ref workspace.State) != 0)
                    return false;
            }

            Span<NativeRenderPacketRecord> packets = Packets;
            Span<NativeChunkRecord> chunks = Chunks;
            for (int index = 0; index < packets.Length; index++)
            {
                ref NativeRenderPacketRecord packet = ref packets[index];
                NativeRenderPacketState packetState = ReadState(ref packet.State);
                if (packetState == NativeRenderPacketState.Empty)
                    continue;
                if (packetState != NativeRenderPacketState.Retired)
                    return false;
                if ((uint)packet.ChunkIndex >= (uint)chunks.Length || chunks[packet.ChunkIndex].PacketIndex != index || ReadState(ref chunks[packet.ChunkIndex].State) != NativeChunkState.Retired)
                {
                    Fail(NativeGtrtFailureCode.InvalidPacketRecycle);
                    return false;
                }
            }

            foreach (ref NativeRenderPacketRecord packet in packets)
            {
                if (ReadState(ref packet.State) == NativeRenderPacketState.Retired)
                {
                    packet = default;
                }
            }

            InitializePacketRanges();
            return true;
        }
        finally
        {
            Volatile.Write(ref state.PacketRecycleState, 0);
        }
    }

    internal bool TryPrepareRun(long seed, int centerChunkX, int centerChunkY, int centerChunkZ)
    {
        ref NativeGtrtSessionState state = ref State;
        int publicationState = Volatile.Read(ref state.PublicationState);
        if (publicationState == 0)
        {
            if (Interlocked.CompareExchange(ref state.PublicationState, -1, 0) != 0)
            {
                return false;
            }

            StartupPerformanceRecorder.RecordSeedAccepted();
            state.Seed = seed;
            NativeOpenSimplexNoiseState noise = NoiseState;
            noise.Initialize(seed);
            if (centerChunkX != 0 || centerChunkY != 0 || centerChunkZ != 0)
            {
                ResetRunRecords(centerChunkX, centerChunkY, centerChunkZ, epoch: 1);
            }
            else
            {
                state.SessionEpoch = 1;
            }

            Volatile.Write(ref state.PublicationState, 1);
            return true;
        }

        if (publicationState != 1 || state.Seed != seed || Volatile.Read(ref state.ClaimedGenerationCount) != 0 || Volatile.Read(ref state.ClaimedMeshCount) != 0 || Volatile.Read(ref state.PacketConsumerCount) != 0 || Volatile.Read(ref state.ReadyPacketCount) != 0 || Volatile.Read(ref state.DisposalState) != 0 || state.TransactionOpen != 0)
        {
            Fail(NativeGtrtFailureCode.InvalidSessionReset);
            return false;
        }

        return TryPrepareStreamingRun(centerChunkX, centerChunkY, centerChunkZ);
    }

    private void ResetRunRecords(int centerChunkX, int centerChunkY, int centerChunkZ, int epoch)
    {
        ref NativeGtrtSessionState state = ref State;
        Span<NativeColumnRecord> columns = Columns;
        Span<NativeChunkRecord> chunks = Chunks;
        Span<NativeWorkItem> generationJobs = GenerationJobs;
        Span<NativeWorkItem> meshJobs = MeshJobs;
        Span<NativeReadySlot> readySlots = MeshReadySlots;
        state.CenterChunkX = centerChunkX;
        state.CenterChunkY = centerChunkY;
        state.CenterChunkZ = centerChunkZ;
        MemoryMarshal.AsBytes(Profiles).Fill(byte.MaxValue);
        GenerationWorkspaces.Clear();
        MeshWorkspaces.Clear();
        Packets.Clear();
        for (int relativeX = header.MinimumChunkX; relativeX <= header.MaximumChunkX; relativeX++)
        {
            int chunkX = checked(centerChunkX + relativeX);
            for (int relativeZ = header.MinimumChunkZ; relativeZ <= header.MaximumChunkZ; relativeZ++)
            {
                int chunkZ = checked(centerChunkZ + relativeZ);
                int columnIndex = GetColumnIndex(chunkX, chunkZ);
        ResetColumnAndMeshJobs(centerChunkY, epoch, columns, chunks, generationJobs, meshJobs, relativeX, chunkX, relativeZ, chunkZ, columnIndex);
            }
        }
        FinishResetRunRecordsPhase(epoch, ref state, readySlots);
    }

    internal bool TryPrepareForDisposal()
    {
        ref NativeGtrtSessionState state = ref State;
        int disposalState = Volatile.Read(ref state.DisposalState);
        if (disposalState == 0)
        {
            disposalState = Interlocked.CompareExchange(ref state.DisposalState, 1, 0);
        }

        if (disposalState != 0 && disposalState != 1)
            return false;
        RequestCancellation();
        if (Volatile.Read(ref state.ClaimedGenerationCount) != 0 || Volatile.Read(ref state.ClaimedMeshCount) != 0)
            return false;
        Span<NativeGenerationWorkspaceRecord> generationWorkspaces = GenerationWorkspaces;
        foreach (ref NativeGenerationWorkspaceRecord workspace in generationWorkspaces)
        {
            if (Volatile.Read(ref workspace.State) != 0)
                return false;
        }

        Span<NativeMeshWorkspaceRecord> meshWorkspaces = MeshWorkspaces;
        foreach (ref NativeMeshWorkspaceRecord workspace in meshWorkspaces)
        {
            if (Volatile.Read(ref workspace.State) != 0)
                return false;
        }

        Span<NativeRenderPacketRecord> packets = Packets;
        foreach (ref NativeRenderPacketRecord packet in packets)
        {
            NativeRenderPacketState packetState = ReadState(ref packet.State);
            if (packetState == NativeRenderPacketState.Empty || packetState == NativeRenderPacketState.Retired)
            {
                continue;
            }

            if (packetState == NativeRenderPacketState.Writing || packetState == NativeRenderPacketState.Active)
                return false;
            if (packetState != NativeRenderPacketState.Ready)
            {
                Fail(NativeGtrtFailureCode.InvalidPacketRecycle);
                return false;
            }

            if (!TryRetirePacket(packet.ChunkIndex))
                return false;
        }

        return TryRecyclePacketStorage();
    }

    internal bool TryReadPacket(int chunkIndex, out NativePacketReadView packet)
    {
        packet = default;
        if ((uint)chunkIndex >= (uint)Chunks.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        NativeChunkRecord chunk = Chunks[chunkIndex];
        ref NativeRenderPacketRecord source = ref Packets[chunk.PacketIndex];
        if (ReadState(ref chunk.State) != NativeChunkState.PacketReady || ReadState(ref source.State) != NativeRenderPacketState.Ready || source.PublicationEpoch != State.SessionEpoch || source.ChunkIndex != chunkIndex)
        {
            Fail(NativeGtrtFailureCode.InvalidPacketPublication);
            return false;
        }

        NativeRenderPacketRecord record = source;
        Span<uint> words = PacketWords;
        packet = new NativePacketReadView(record, words.Slice(record.OpaqueWordOffset, record.OpaqueWordCount), words.Slice(record.TransparentWordOffset, record.TransparentWordCount));
        return true;
    }

    internal bool TryInspectRetiredPacket(int chunkIndex, out NativePacketReadView packet)
    {
        packet = default;
        if ((uint)chunkIndex >= (uint)Chunks.Length)
            return false;
        NativeChunkRecord chunk = Chunks[chunkIndex];
        NativeRenderPacketRecord source = Packets[chunk.PacketIndex];
        if (ReadState(ref chunk.State) != NativeChunkState.Retired || ReadState(ref source.State) != NativeRenderPacketState.Retired || source.PublicationEpoch != State.SessionEpoch || source.ChunkIndex != chunkIndex)
        {
            return false;
        }

        packet = new NativePacketReadView(source, PacketWords.Slice(source.OpaqueWordOffset, source.OpaqueWordCount), PacketWords.Slice(source.TransparentWordOffset, source.TransparentWordCount));
        return true;
    }

    internal void Fail(NativeGtrtFailureCode failure) => RecordFailure(ref State, failure);
    internal int GetColumnIndex(int chunkX, int chunkZ)
    {
        long localX = (long)chunkX - State.CenterChunkX - header.MinimumChunkX;
        long localZ = (long)chunkZ - State.CenterChunkZ - header.MinimumChunkZ;
        if ((ulong)localX >= (ulong)header.ColumnWidth || (ulong)localZ >= (ulong)header.ColumnWidth)
            return -1;
        return FloorMod((long)chunkX - header.MinimumChunkX, header.ColumnWidth) * header.ColumnWidth + FloorMod((long)chunkZ - header.MinimumChunkZ, header.ColumnWidth);
    }

    internal int GetChunkIndex(int chunkX, int chunkY, int chunkZ)
    {
        int columnIndex = GetColumnIndex(chunkX, chunkZ);
        long localY = (long)chunkY - State.CenterChunkY - header.MinimumChunkY;
        if (columnIndex < 0 || (ulong)localY >= (ulong)header.VerticalChunkCount)
            return -1;
        return columnIndex * header.VerticalChunkCount + FloorMod((long)chunkY - header.MinimumChunkY, header.VerticalChunkCount);
    }

    internal static int FloorMod(long coordinate, int width) => (int)((coordinate % width + width) % width);
    internal bool TryClaimGeneration(out NativeWorkItem work)
    {
        Span<NativeWorkItem> jobs = GenerationJobs;
        Span<NativeColumnRecord> columns = Columns;
        ref NativeGtrtSessionState state = ref State;
        if (Volatile.Read(ref state.CancellationState) != 0 || Volatile.Read(ref state.DisposalState) != 0)
        {
            work = default;
            return false;
        }

        Interlocked.Increment(ref state.ClaimedGenerationCount);
        if (Volatile.Read(ref state.CancellationState) != 0 || Volatile.Read(ref state.DisposalState) != 0)
        {
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            work = default;
            return false;
        }

        while (true)
        {
            if (Volatile.Read(ref state.CancellationState) != 0)
            {
                Interlocked.Decrement(ref state.ClaimedGenerationCount);
                work = default;
                return false;
            }

            int index = Interlocked.Increment(ref state.GenerationCursor) - 1;
            if ((uint)index >= (uint)jobs.Length)
            {
                Interlocked.Decrement(ref state.ClaimedGenerationCount);
                work = default;
                return false;
            }

            ref NativeWorkItem job = ref jobs[index];
            if (!TryTransition(ref job.State, NativeWorkState.Scheduled, NativeWorkState.Claimed))
            {
                continue;
            }

            ref NativeColumnRecord column = ref columns[job.RecordIndex];
            if (!TryTransition(ref column.State, NativeColumnState.Empty, NativeColumnState.Reserved))
            {
                RecordFailure(ref state, NativeGtrtFailureCode.InvalidGenerationClaim);
                Interlocked.Decrement(ref state.ClaimedGenerationCount);
                work = default;
                return false;
            }

            work = job;
            return true;
        }
    }

    internal bool TryCompleteGeneration(scoped ref readonly NativeWorkItem claimedWork)
    {
        ref NativeGtrtSessionState state = ref State;
        if (claimedWork.Kind != NativeWorkKind.GenerateColumn || claimedWork.Epoch != state.SessionEpoch || (uint)claimedWork.RecordIndex >= (uint)Columns.Length)
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidGenerationCompletion);
            return false;
        }

        Span<NativeWorkItem> jobs = GenerationJobs;
        ref NativeWorkItem job = ref jobs[claimedWork.RecordIndex];
        if (!TryTransition(ref job.State, NativeWorkState.Claimed, NativeWorkState.Completed))
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidGenerationCompletion);
            return false;
        }

        Span<NativeColumnRecord> columns = Columns;
        ref NativeColumnRecord column = ref columns[claimedWork.RecordIndex];
        if (!TryTransition(ref column.State, NativeColumnState.Reserved, NativeColumnState.Generated))
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidGenerationCompletion);
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            return false;
        }

        if (!PublishGeneratedChunks(column.ChunkX, column.ChunkZ, ref state))
        {
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            return false;
        }

        int remaining = Interlocked.Decrement(ref state.RemainingColumns);
        if (remaining < 0)
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidGenerationCompletion);
            Interlocked.Decrement(ref state.ClaimedGenerationCount);
            return false;
        }

        bool released = ReleaseMeshDependencies(column.ChunkX, column.ChunkZ, claimedWork.Epoch, ref state);
        int claimed = Interlocked.Decrement(ref state.ClaimedGenerationCount);
        if (claimed < 0)
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidGenerationCompletion);
            return false;
        }

        return released;
    }

    internal bool TryAbandonGeneration(scoped ref readonly NativeWorkItem claimedWork)
    {
        if (claimedWork.Kind != NativeWorkKind.GenerateColumn || claimedWork.Epoch != State.SessionEpoch || (uint)claimedWork.RecordIndex >= (uint)GenerationJobs.Length)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationCancellation);
            return false;
        }

        ref NativeWorkItem job = ref GenerationJobs[claimedWork.RecordIndex];
        ref NativeColumnRecord column = ref Columns[claimedWork.RecordIndex];
        if (ReadState(ref job.State) != NativeWorkState.Claimed || ReadState(ref column.State) != NativeColumnState.Reserved)
        {
            return false;
        }

        if (!TryTransition(ref column.State, NativeColumnState.Reserved, NativeColumnState.Retired) || !TryTransition(ref job.State, NativeWorkState.Claimed, NativeWorkState.Canceled))
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationCancellation);
            return false;
        }

        int claimed = Interlocked.Decrement(ref State.ClaimedGenerationCount);
        if (claimed < 0)
        {
            Fail(NativeGtrtFailureCode.InvalidGenerationCancellation);
            return false;
        }

        return true;
    }

    internal bool TryClaimMesh(out NativeWorkItem work)
    {
        ref NativeGtrtSessionState state = ref State;
        if (Volatile.Read(ref state.CancellationState) != 0 || Volatile.Read(ref state.DisposalState) != 0 || Volatile.Read(ref state.PacketRecycleState) != 0)
        {
            work = default;
            return false;
        }

        Interlocked.Increment(ref state.ClaimedMeshCount);
        if (Volatile.Read(ref state.CancellationState) != 0 || Volatile.Read(ref state.DisposalState) != 0 || Volatile.Read(ref state.PacketRecycleState) != 0)
        {
            Interlocked.Decrement(ref state.ClaimedMeshCount);
            work = default;
            return false;
        }

        while (TryDequeueMeshReady(out int chunkIndex, out int epoch))
        {
            if (epoch != state.SessionEpoch || (uint)chunkIndex >= (uint)MeshJobs.Length)
            {
                RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshClaim);
                Interlocked.Decrement(ref state.ClaimedMeshCount);
                work = default;
                return false;
            }

            Span<NativeWorkItem> jobs = MeshJobs;
            ref NativeWorkItem job = ref jobs[chunkIndex];
            if (!TryTransition(ref job.State, NativeWorkState.Scheduled, NativeWorkState.Claimed))
            {
                if (ReadState(ref job.State) == NativeWorkState.Canceled)
                {
                    continue;
                }

                RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshClaim);
                Interlocked.Decrement(ref state.ClaimedMeshCount);
                work = default;
                return false;
            }

            Span<NativeChunkRecord> chunks = Chunks;
            ref NativeChunkRecord chunk = ref chunks[chunkIndex];
            if (!TryTransition(ref chunk.State, NativeChunkState.Generated, NativeChunkState.MeshReady))
            {
                RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshClaim);
                Interlocked.Decrement(ref state.ClaimedMeshCount);
                work = default;
                return false;
            }

            work = job;
            return true;
        }

        Interlocked.Decrement(ref state.ClaimedMeshCount);
        work = default;
        return false;
    }

    internal bool TryCompleteMesh(scoped ref readonly NativeWorkItem claimedWork)
    {
        ref NativeGtrtSessionState state = ref State;
        if (claimedWork.Kind != NativeWorkKind.BuildChunkMesh || claimedWork.Epoch != state.SessionEpoch || (uint)claimedWork.RecordIndex >= (uint)MeshJobs.Length)
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        Span<NativeWorkItem> jobs = MeshJobs;
        ref NativeWorkItem job = ref jobs[claimedWork.RecordIndex];
        Span<NativeChunkRecord> chunks = Chunks;
        ref NativeChunkRecord chunk = ref chunks[claimedWork.RecordIndex];
        ref NativeRenderPacketRecord packet = ref Packets[chunk.PacketIndex];
        if (ReadState(ref packet.State) != NativeRenderPacketState.Writing || packet.ChunkIndex != claimedWork.RecordIndex || packet.RegistryEpoch != claimedWork.Epoch || packet.PublicationEpoch != 0)
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        packet.PublicationEpoch = claimedWork.Epoch;
        chunk.MeshEpoch = claimedWork.Epoch;
        if (!TryTransition(ref chunk.State, NativeChunkState.MeshReady, NativeChunkState.PacketReady))
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        Interlocked.Increment(ref state.ReadyPacketCount);
        if (!TryTransition(ref job.State, NativeWorkState.Claimed, NativeWorkState.Completed))
        {
            Interlocked.Decrement(ref state.ReadyPacketCount);
            TryTransition(ref chunk.State, NativeChunkState.PacketReady, NativeChunkState.Retired);
            TryTransition(ref packet.State, NativeRenderPacketState.Writing, NativeRenderPacketState.Retired);
            Interlocked.Decrement(ref state.ClaimedMeshCount);
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        ref int packetState = ref Unsafe.As<NativeRenderPacketState, int>(ref packet.State);
        Volatile.Write(ref packetState, (int)NativeRenderPacketState.Ready);
        int claimed = Interlocked.Decrement(ref state.ClaimedMeshCount);
        if (claimed < 0)
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        int remaining = Interlocked.Decrement(ref state.RemainingChunks);
        if (remaining < 0)
        {
            RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshCompletion);
            return false;
        }

        return true;
    }

    internal void RequestCancellation() => Interlocked.Exchange(ref State.CancellationState, 1);
    internal bool CancellationRequested => Volatile.Read(ref State.CancellationState) != 0;

    private bool PublishGeneratedChunks(int chunkX, int chunkZ, ref NativeGtrtSessionState state)
    {
        Span<NativeChunkRecord> chunks = Chunks;
        int minimumChunkY = checked(state.CenterChunkY + header.MinimumChunkY);
        int maximumChunkY = checked(state.CenterChunkY + header.MaximumChunkY);
        for (int chunkY = minimumChunkY; chunkY <= maximumChunkY; chunkY++)
        {
            int chunkIndex = GetChunkIndex(chunkX, chunkY, chunkZ);
            ref NativeChunkRecord chunk = ref chunks[chunkIndex];
            chunk.GenerationEpoch = state.SessionEpoch;
            if (!TryTransition(ref chunk.State, NativeChunkState.Empty, NativeChunkState.Generated))
            {
                RecordFailure(ref state, NativeGtrtFailureCode.InvalidGenerationCompletion);
                return false;
            }
        }

        return true;
    }

    private bool ReleaseMeshDependencies(int generatedChunkX, int generatedChunkZ, int epoch, ref NativeGtrtSessionState state)
    {
        if (!ReleaseMeshColumn(generatedChunkX, generatedChunkZ, epoch, ref state) || !ReleaseMeshColumn(generatedChunkX - 1, generatedChunkZ, epoch, ref state) || !ReleaseMeshColumn(generatedChunkX + 1, generatedChunkZ, epoch, ref state) || !ReleaseMeshColumn(generatedChunkX, generatedChunkZ - 1, epoch, ref state) || !ReleaseMeshColumn(generatedChunkX, generatedChunkZ + 1, epoch, ref state))
        {
            return false;
        }

        return true;
    }

    private bool ReleaseMeshColumn(int chunkX, int chunkZ, int epoch, ref NativeGtrtSessionState state)
    {
        int minimumChunkX = checked(state.CenterChunkX - header.Lod1Radius);
        int maximumChunkX = checked(state.CenterChunkX + header.Lod1Radius);
        int minimumChunkZ = checked(state.CenterChunkZ - header.Lod1Radius);
        int maximumChunkZ = checked(state.CenterChunkZ + header.Lod1Radius);
        if (chunkX < minimumChunkX || chunkX > maximumChunkX || chunkZ < minimumChunkZ || chunkZ > maximumChunkZ)
        {
            return true;
        }

        Span<NativeChunkRecord> chunks = Chunks;
        Span<NativeWorkItem> jobs = MeshJobs;
        int minimumChunkY = checked(state.CenterChunkY + header.MinimumChunkY);
        int maximumChunkY = checked(state.CenterChunkY + header.MaximumChunkY);
        for (int chunkY = minimumChunkY; chunkY <= maximumChunkY; chunkY++)
        {
            int chunkIndex = GetChunkIndex(chunkX, chunkY, chunkZ);
            ref NativeChunkRecord chunk = ref chunks[chunkIndex];
            ref NativeWorkItem job = ref jobs[chunkIndex];
            if (ReadState(ref job.State) != NativeWorkState.Waiting)
                continue;
            int dependencies = Interlocked.Decrement(ref chunk.RemainingDependencies);
            if (dependencies < 0)
            {
                RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshDependency);
                return false;
            }

            if (dependencies != 0)
            {
                continue;
            }

            if (!TryTransition(ref job.State, NativeWorkState.Waiting, NativeWorkState.Scheduled))
            {
                RecordFailure(ref state, NativeGtrtFailureCode.InvalidMeshDependency);
                return false;
            }

            if (!TryEnqueueMeshReady(chunkIndex, epoch))
            {
                TryTransition(ref job.State, NativeWorkState.Scheduled, NativeWorkState.Waiting);
                RecordFailure(ref state, NativeGtrtFailureCode.MeshReadyQueueFull);
                return false;
            }
        }

        return true;
    }

    private bool TryEnqueueMeshReady(int chunkIndex, int epoch)
    {
        Span<NativeReadySlot> slots = MeshReadySlots;
        ref long enqueuePosition = ref State.MeshEnqueuePosition;
        while (true)
        {
            long position = Volatile.Read(ref enqueuePosition);
            ref NativeReadySlot slot = ref slots[(int)(position % slots.Length)];
            long sequence = Volatile.Read(ref slot.Sequence);
            long difference = sequence - position;
            if (difference == 0)
            {
                if (Interlocked.CompareExchange(ref enqueuePosition, position + 1, position) == position)
                {
                    slot.RecordIndex = chunkIndex;
                    slot.Epoch = epoch;
                    Volatile.Write(ref slot.Sequence, position + 1);
                    return true;
                }
            }
            else if (difference < 0)
            {
                return false;
            }
            else
            {
                Thread.SpinWait(1);
            }
        }
    }

    private bool TryDequeueMeshReady(out int chunkIndex, out int epoch)
    {
        Span<NativeReadySlot> slots = MeshReadySlots;
        ref long dequeuePosition = ref State.MeshDequeuePosition;
        while (true)
        {
            long position = Volatile.Read(ref dequeuePosition);
            ref NativeReadySlot slot = ref slots[(int)(position % slots.Length)];
            long sequence = Volatile.Read(ref slot.Sequence);
            long difference = sequence - (position + 1);
            if (difference == 0)
            {
                if (Interlocked.CompareExchange(ref dequeuePosition, position + 1, position) == position)
                {
                    chunkIndex = slot.RecordIndex;
                    epoch = slot.Epoch;
                    Volatile.Write(ref slot.Sequence, position + slots.Length);
                    return true;
                }
            }
            else if (difference < 0)
            {
                chunkIndex = -1;
                epoch = 0;
                return false;
            }
            else
            {
                Thread.SpinWait(1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMeshScratchOffset(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)header.MeshWorkerCount)
        {
            Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return header.MeshFaceScratchOffset;
        }

        return checked(header.MeshFaceScratchOffset + workerIndex * header.MeshFaceScratchCountPerWorker * Unsafe.SizeOf<int>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMeshFacePlaneCount() => header.MeshFaceScratchCountPerWorker / 2;
    private bool TryEnterPacketConsumer()
    {
        ref NativeGtrtSessionState state = ref State;
        if (Volatile.Read(ref state.PacketRecycleState) != 0)
            return false;
        Interlocked.Increment(ref state.PacketConsumerCount);
        if (Volatile.Read(ref state.PacketRecycleState) == 0)
            return true;
        Interlocked.Decrement(ref state.PacketConsumerCount);
        return false;
    }

    private void ExitPacketConsumer()
    {
        int consumers = Interlocked.Decrement(ref State.PacketConsumerCount);
        if (consumers < 0)
            Fail(NativeGtrtFailureCode.InvalidPacketRecycle);
    }

    private bool TryReservePacketWords(int wordCount, out int wordOffset)
    {
        return TryAllocatePacketRange(wordCount, out wordOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryTransition<TState>(ref TState state, TState expected, TState next)
        where TState : unmanaged, Enum
    {
        ref int value = ref Unsafe.As<TState, int>(ref state);
        int expectedValue = Unsafe.As<TState, int>(ref expected);
        int nextValue = Unsafe.As<TState, int>(ref next);
        return Interlocked.CompareExchange(ref value, nextValue, expectedValue) == expectedValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TState ReadState<TState>(ref TState state)
        where TState : unmanaged, Enum
    {
        ref int value = ref Unsafe.As<TState, int>(ref state);
        int observed = Volatile.Read(ref value);
        return Unsafe.As<int, TState>(ref observed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordFailure(ref NativeGtrtSessionState state, NativeGtrtFailureCode failure) => Interlocked.CompareExchange(ref state.FailureCode, (int)failure, (int)NativeGtrtFailureCode.None);
    private Span<T> ReadRange<T>(int offset, int count)
        where T : unmanaged
    {
        int byteCount = checked(count * Unsafe.SizeOf<T>());
        return MemoryMarshal.Cast<byte, T>(bytes.Slice(offset, byteCount));
    }

    private void ValidateRange<T>(int offset, int count)
        where T : unmanaged
    {
        if (offset < 0 || count < 0)
            throw new InvalidDataException("A native GTRT range is negative.");
        int end = checked(offset + checked(count * Unsafe.SizeOf<T>()));
        if (end > bytes.Length)
        {
            throw new InvalidDataException("A native GTRT range is outside its owner.");
        }
    }

    private void FinishResetRunRecordsPhase(int epoch, ref global::MVoxelEngine1.WorldGeneration.Native.NativeGtrtSessionState state, global::System.Span<global::MVoxelEngine1.WorldGeneration.Native.NativeReadySlot> readySlots)
    {

        for (int index = 0; index < readySlots.Length; index++)
        {
            readySlots[index] = new NativeReadySlot
            {
                Sequence = index
            };
        }

        state.SessionEpoch = epoch;
        state.GenerationCursor = 0;
        state.RemainingColumns = header.ColumnCount;
        state.MeshCursor = 0;
        state.RemainingChunks = header.RequiredChunkCount;
        state.PlannedColumns = header.ColumnCount;
        state.PlannedMeshes = header.RequiredChunkCount;
        state.RetainedColumns = 0;
        state.RetainedPackets = 0;
        state.ReadyPacketCount = 0;
        state.FailureCode = 0;
        state.CancellationState = 0;
        state.MeshEnqueuePosition = 0;
        state.MeshDequeuePosition = 0;
        state.PacketWordCursor = 0;
        state.ClaimedMeshCount = 0;
        state.PacketRecycleState = 0;
        state.ClaimedGenerationCount = 0;
        state.PacketConsumerCount = 0;

    }

    private void ResetColumnAndMeshJobs(int centerChunkY, int epoch, global::System.Span<global::MVoxelEngine1.WorldGeneration.Native.NativeColumnRecord> columns, global::System.Span<global::MVoxelEngine1.WorldGeneration.Native.NativeChunkRecord> chunks, global::System.Span<global::MVoxelEngine1.WorldGeneration.Native.NativeWorkItem> generationJobs, global::System.Span<global::MVoxelEngine1.WorldGeneration.Native.NativeWorkItem> meshJobs, int relativeX, int chunkX, int relativeZ, int chunkZ, int columnIndex)
    {
                int profileOffset = checked(columnIndex * header.ProfilesPerColumn);
                columns[columnIndex] = new NativeColumnRecord
                {
                    ChunkX = chunkX,
                    ChunkZ = chunkZ,
                    ProfileOffset = profileOffset,
                    BiomeIndex = -1,
                    State = NativeColumnState.Empty
                };
                generationJobs[columnIndex] = new NativeWorkItem
                {
                    RecordIndex = columnIndex,
                    Epoch = epoch,
                    Kind = NativeWorkKind.GenerateColumn,
                    State = NativeWorkState.Scheduled
                };
                bool initialMeshRequired = relativeX >= -header.Lod1Radius && relativeX <= header.Lod1Radius && relativeZ >= -header.Lod1Radius && relativeZ <= header.Lod1Radius;
                for (int relativeY = header.MinimumChunkY; relativeY <= header.MaximumChunkY; relativeY++)
                {
                    int chunkY = checked(centerChunkY + relativeY);
                    int chunkIndex = GetChunkIndex(chunkX, chunkY, chunkZ);
                    int materializedChunkIndex = FindMaterializedChunkIndex(chunkX, chunkY, chunkZ);
                    NativeChunkStorageKind storageKind = NativeChunkStorageKind.GeneratedProfile;
                    long dirtyRevision = 0;
                    if (materializedChunkIndex >= 0)
                    {
                        NativeMaterializedChunkRecord materialized = MaterializedChunks[materializedChunkIndex];
                        storageKind = materialized.StorageKind;
                        dirtyRevision = materialized.Revision;
                    }

                    chunks[chunkIndex] = new NativeChunkRecord
                    {
                        ChunkX = chunkX,
                        ChunkY = chunkY,
                        ChunkZ = chunkZ,
                        ColumnIndex = columnIndex,
                        ProfileOffset = profileOffset,
                        PacketIndex = chunkIndex,
                        DirtyRevision = dirtyRevision,
                        Flags = initialMeshRequired ? (int)NativeChunkFlags.InitialMeshRequired : 0,
                        RemainingDependencies = initialMeshRequired ? 5 : 0,
                        StorageKind = storageKind,
                        MaterializedChunkIndex = materializedChunkIndex,
                        State = NativeChunkState.Empty
                    };
                    meshJobs[chunkIndex] = new NativeWorkItem
                    {
                        RecordIndex = chunkIndex,
                        Epoch = epoch,
                        Kind = NativeWorkKind.BuildChunkMesh,
                        State = initialMeshRequired ? NativeWorkState.Waiting : NativeWorkState.Canceled
                    };
                }

    }
}

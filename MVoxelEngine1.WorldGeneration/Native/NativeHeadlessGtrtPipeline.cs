using System.Runtime.ExceptionServices;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

public readonly struct NativePreUploadPacket
{
    internal NativePreUploadPacket(
        long renderDataId,
        int chunkX,
        int chunkY,
        int chunkZ,
        int opaqueFaceCount,
        int opaqueWordCount,
        int transparentFaceCount,
        int transparentWordCount)
    {
        RenderDataId = renderDataId;
        ChunkX = chunkX;
        ChunkY = chunkY;
        ChunkZ = chunkZ;
        OpaqueFaceCount = opaqueFaceCount;
        OpaqueWordCount = opaqueWordCount;
        TransparentFaceCount = transparentFaceCount;
        TransparentWordCount = transparentWordCount;
    }

    public long RenderDataId { get; }

    public int ChunkX { get; }

    public int ChunkY { get; }

    public int ChunkZ { get; }

    public int OpaqueFaceCount { get; }

    public int OpaqueRectangleCount =>
        OpaqueWordCount / PackedFaceRectangle.WordsPerRectangle;

    public int OpaqueWordCount { get; }

    public int TransparentFaceCount { get; }

    public int TransparentRectangleCount =>
        TransparentWordCount / PackedFaceRectangle.WordsPerRectangle;

    public int TransparentWordCount { get; }
}

public sealed class NativeGtrtPipeline : IDisposable
{
    private readonly NativeGameSnapshot game;
    private readonly NativeGtrtSession session;
    private readonly NativeGtrtWorkerPool workers;
    private readonly NativeWorldSaveExporter saveExporter;
    private readonly NativeLeaseAction<byte> capturePacketAction;
    private readonly NativeLeaseAction<byte> consumePacketsAction;
    private readonly NativeLeaseAction<byte> editBlockAction;
    private readonly NativeLeaseAction<byte> rollbackBlockAction;
    private readonly NativeLeaseAction<byte> readBlockAction;
    private readonly int requiredPacketCount;
    private NativePreUploadPacket capturedPacket;
    private NativeChunkRenderPacketAction? pendingPacketConsumer;
    private Exception? packetConsumerFailure;
    private bool packetCaptured;
    private int consumedPacketCount;
    private int retiredPacketCount;
    private int packetConsumptionState;
    private int completedRunCount;
    private bool packetConsumptionCompleted;
    private bool pendingEdit;
    private bool pendingEditActionSucceeded;
    private bool pendingEditChanged;
    private bool pendingRollbackSucceeded;
    private bool pendingReadSucceeded;
    private int centerChunkX;
    private int centerChunkY;
    private int centerChunkZ;
    private int pendingWorldX;
    private int pendingWorldY;
    private int pendingWorldZ;
    private ushort pendingBlockId;
    private ushort pendingReadBlockId;
    private ushort pendingPreviousBlockId;
    private int pendingEditedChunkX;
    private int pendingEditedChunkY;
    private int pendingEditedChunkZ;
    private int pendingEditedLocalX;
    private int pendingEditedLocalY;
    private int pendingEditedLocalZ;
    private int pendingPreviousMaterializedChunkIndex;
    private int pendingCurrentMaterializedChunkIndex;
    private long pendingPreviousRevision;
    private long pendingPreviousPersistedRevision;
    private NativeChunkStorageKind pendingPreviousStorageKind;
    private long publishedSeed;
    private long coordinatorManagedAllocationBytes;
    private double? generationToRenderMilliseconds;
    private int disposed;

    private NativeGtrtPipeline(
        NativeGameSnapshot game,
        NativeGtrtSession session,
        NativeGtrtWorkerPool workers,
        int requiredPacketCount)
    {
        this.game = game;
        this.session = session;
        this.workers = workers;
        saveExporter = new NativeWorldSaveExporter(session);
        this.requiredPacketCount = requiredPacketCount;
        capturePacketAction = CaptureFirstPacket;
        consumePacketsAction = ConsumePackets;
        editBlockAction = EditBlockCore;
        rollbackBlockAction = RollbackBlockCore;
        readBlockAction = ReadBlockCore;
    }

    public static NativeGtrtPipeline Create(
        BlockTextureAtlas textureAtlas) =>
        CreateConfigured(textureAtlas, savePlan: null);

    internal static NativeGtrtPipeline Create(
        BlockTextureAtlas textureAtlas,
        NativeWorldSaveImportPlan savePlan) =>
        CreateConfigured(textureAtlas, savePlan);

    private static NativeGtrtPipeline CreateConfigured(
        BlockTextureAtlas textureAtlas,
        NativeWorldSaveImportPlan? savePlan)
    {
        ArgumentNullException.ThrowIfNull(textureAtlas);
        FaceGenerationMode mode =
            FlagManager.flags.faceGenerationMode ??
            FaceGenerationMode.Optimized;
        if (mode != FaceGenerationMode.Optimized)
        {
            throw new InvalidOperationException(
                "The native headless GTRT path requires optimized faces.");
        }

        float processorCount = Environment.ProcessorCount;
        float generationWorkersPerCore =
            FlagManager.flags.worldGenWorkersPerCoreInitial ??
            FlagManager.flags.worldGenWorkersPerCore ??
            throw new InvalidOperationException(
                "The world generation worker count is not set.");
        float meshWorkersPerCore =
            FlagManager.flags.meshRenderWorkersPerCoreInitial ??
            FlagManager.flags.meshRenderWorkersPerCore ??
            throw new InvalidOperationException(
                "The mesh worker count is not set.");
        int generationWorkerCount = Math.Max(
            1,
            (int)(generationWorkersPerCore * processorCount));
        int meshWorkerCount = Math.Max(
            1,
            (int)(meshWorkersPerCore * processorCount));
        bool streamGeneration =
            GameManager.settings.renderStreamingAllowed &&
            (FlagManager.flags.renderStreamingIfAllowed ?? false);

        return Create(
            textureAtlas,
            GameManager.settings,
            generationWorkerCount,
            meshWorkerCount,
            streamGeneration,
            savePlan);
    }

    internal static NativeGtrtPipeline Create(
        BlockTextureAtlas textureAtlas,
        GameSettings settings,
        int generationWorkerCount,
        int meshWorkerCount,
        bool streamGeneration = false,
        NativeWorldSaveImportPlan? savePlan = null)
    {
        ArgumentNullException.ThrowIfNull(textureAtlas);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            generationWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            meshWorkerCount);

        int diameter = checked(settings.lod1RenderDistance * 2 + 1);
        int requiredPacketCount = checked(
            diameter * diameter * diameter);
        NativeGameSnapshot? game = null;
        NativeGtrtSession? session = null;
        NativeGtrtWorkerPool? workers = null;
        try
        {
            game = NativeGameSnapshot.Create(textureAtlas);
            int sectionCountX = DivideRoundUp(
                settings.chunkMaxX,
                Section.SECTION_SIZE);
            int sectionCountY = DivideRoundUp(
                settings.chunkMaxY,
                Section.SECTION_SIZE);
            int sectionCountZ = DivideRoundUp(
                settings.chunkMaxZ,
                Section.SECTION_SIZE);
            int editableSectionCapacity = checked(
                NativeGtrtSessionLayout.DefaultMaterializedChunkCapacity *
                sectionCountX *
                sectionCountY *
                sectionCountZ);
            int materializedChunkCapacity = checked(
                NativeGtrtSessionLayout.DefaultMaterializedChunkCapacity +
                (savePlan?.ChunkCount ?? 0));
            int materializedSectionCapacity = checked(
                editableSectionCapacity +
                (savePlan?.SectionCount ?? 0));
            int materializedRawSectionCapacity = checked(
                editableSectionCapacity +
                (savePlan?.RawSectionCount ?? 0));
            session = NativeGtrtSession.Create(
                settings,
                game,
                generationWorkerCount,
                meshWorkerCount,
                materializedChunkCapacity,
                materializedSectionCapacity,
                materializedRawSectionCapacity,
                savePlan?.PaletteCount ?? 0,
                savePlan?.PackedWordCount ?? 0);
            savePlan?.Import(session);
            workers = new NativeGtrtWorkerPool(
                session,
                generationWorkerCount,
                meshWorkerCount,
                streamGeneration);
            return new NativeGtrtPipeline(
                game,
                session,
                workers,
                requiredPacketCount);
        }
        catch
        {
            workers?.Dispose();
            session?.Dispose();
            game?.Dispose();
            throw;
        }
    }

    public NativePreUploadPacket Run(long seed)
    {
        if (Interlocked.CompareExchange(
                ref completedRunCount,
                -1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "The native GTRT seed is already published.");
        }

        publishedSeed = seed;
        try
        {
            NativePreUploadPacket packet = RunCore(
                seed,
                centerChunkX: 0,
                centerChunkY: 0,
                centerChunkZ: 0,
                recordInitialEndpoint: true);
            centerChunkX = 0;
            centerChunkY = 0;
            centerChunkZ = 0;
            Volatile.Write(ref completedRunCount, 1);
            return packet;
        }
        catch
        {
            Volatile.Write(ref completedRunCount, int.MinValue);
            throw;
        }
    }

    public NativePreUploadPacket MoveToChunk(
        int centerChunkX,
        int centerChunkY,
        int centerChunkZ)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        int runCount = Volatile.Read(ref completedRunCount);
        if (runCount <= 0 || !packetConsumptionCompleted)
        {
            throw new InvalidOperationException(
                "All current native packets must retire before movement.");
        }
        if (Interlocked.CompareExchange(
                ref completedRunCount,
                -1,
                runCount) != runCount)
        {
            throw new InvalidOperationException(
                "The native GTRT pipeline is already moving.");
        }

        try
        {
            NativePreUploadPacket packet = RunCore(
                publishedSeed,
                centerChunkX,
                centerChunkY,
                centerChunkZ,
                recordInitialEndpoint: false);
            this.centerChunkX = centerChunkX;
            this.centerChunkY = centerChunkY;
            this.centerChunkZ = centerChunkZ;
            Volatile.Write(ref completedRunCount, checked(runCount + 1));
            return packet;
        }
        catch
        {
            Volatile.Write(ref completedRunCount, int.MinValue);
            throw;
        }
    }

    private NativePreUploadPacket RunCore(
        long seed,
        int centerChunkX,
        int centerChunkY,
        int centerChunkZ,
        bool recordInitialEndpoint)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        packetCaptured = false;
        packetConsumptionCompleted = false;
        Volatile.Write(ref packetConsumptionState, 0);
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        workers.Run(
            seed,
            centerChunkX,
            centerChunkY,
            centerChunkZ);
        session.Access(capturePacketAction);
        if (!packetCaptured)
        {
            throw new InvalidOperationException(
                "No native render packet reached the pre-upload boundary.");
        }

        if (recordInitialEndpoint)
        {
            generationToRenderMilliseconds =
                StartupPerformanceRecorder.RecordGenerationToRender();
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        coordinatorManagedAllocationBytes = Math.Max(
            coordinatorManagedAllocationBytes,
            allocated);

        return capturedPacket;
    }

    public long MaximumWorkerManagedAllocationBytes =>
        workers.MaximumManagedAllocationBytes;

    public long CoordinatorManagedAllocationBytes =>
        Volatile.Read(ref coordinatorManagedAllocationBytes);

    public long InitialGenerationMilliseconds =>
        workers.InitialGenerationMilliseconds;

    public long InitialMeshMilliseconds => workers.InitialMeshMilliseconds;

    public int RequiredPacketCount => requiredPacketCount;

    public double? GenerationToRenderMilliseconds =>
        generationToRenderMilliseconds;

    internal bool BeginBlockEdit(
        int worldX,
        int worldY,
        int worldZ,
        ushort blockId)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (pendingEdit)
        {
            throw new InvalidOperationException(
                "The prior native block edit is not resolved.");
        }

        int runCount = Volatile.Read(ref completedRunCount);
        if (runCount <= 0 || !packetConsumptionCompleted)
        {
            throw new InvalidOperationException(
                "All current native packets must retire before an edit.");
        }
        if (Interlocked.CompareExchange(
                ref completedRunCount,
                -1,
                runCount) != runCount)
        {
            throw new InvalidOperationException(
                "The native GTRT pipeline is already changing.");
        }

        pendingWorldX = worldX;
        pendingWorldY = worldY;
        pendingWorldZ = worldZ;
        pendingBlockId = blockId;
        pendingEditActionSucceeded = false;
        pendingEditChanged = false;
        try
        {
            session.Access(editBlockAction);
            if (!pendingEditActionSucceeded)
            {
                Volatile.Write(ref completedRunCount, runCount);
                throw new InvalidOperationException(
                    "The native block edit could not enter storage.");
            }
            if (!pendingEditChanged)
            {
                Volatile.Write(ref completedRunCount, runCount);
                return false;
            }

            pendingEdit = true;
            try
            {
                _ = RunCore(
                    publishedSeed,
                    centerChunkX,
                    centerChunkY,
                    centerChunkZ,
                    recordInitialEndpoint: false);
                Volatile.Write(
                    ref completedRunCount,
                    checked(runCount + 1));
                return true;
            }
            catch (Exception failure)
            {
                Exception? rollbackFailure = RollbackPendingEditCore();
                Volatile.Write(ref completedRunCount, int.MinValue);
                if (rollbackFailure is null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                throw new AggregateException(failure, rollbackFailure);
            }
        }
        catch
        {
            if (Volatile.Read(ref completedRunCount) == -1)
                Volatile.Write(ref completedRunCount, int.MinValue);
            throw;
        }
        finally
        {
            pendingBlockId = 0;
        }
    }

    internal void CommitBlockEdit()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (!pendingEdit || !packetConsumptionCompleted)
        {
            throw new InvalidOperationException(
                "A complete native block edit is not ready to commit.");
        }
        ClearPendingEdit();
    }

    internal void RollbackBlockEdit()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        Exception? failure = RollbackPendingEditCore();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal ushort GetBlock(
        int worldX,
        int worldY,
        int worldZ)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (Volatile.Read(ref completedRunCount) <= 0 || pendingEdit)
        {
            throw new InvalidOperationException(
                "Native world data is not available for a block read.");
        }

        pendingWorldX = worldX;
        pendingWorldY = worldY;
        pendingWorldZ = worldZ;
        pendingReadSucceeded = false;
        session.Access(readBlockAction);
        if (!pendingReadSucceeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(worldX),
                "The block is outside the native resident world.");
        }
        return pendingReadBlockId;
    }

    internal int SaveDirtyChunks(string quadsDirectory)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (Volatile.Read(ref completedRunCount) <= 0 ||
            !packetConsumptionCompleted ||
            pendingEdit)
        {
            throw new InvalidOperationException(
                "Native world data is not ready for a save.");
        }
        return saveExporter.SaveDirtyChunks(quadsDirectory);
    }

    public int ConsumeReadyPackets(
        NativeChunkRenderPacketAction packetConsumer)
    {
        ArgumentNullException.ThrowIfNull(packetConsumer);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (!packetCaptured)
        {
            throw new InvalidOperationException(
                "The native GTRT run is not complete.");
        }
        if (Interlocked.CompareExchange(
                ref packetConsumptionState,
                1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "Native render packets can be consumed only once.");
        }

        pendingPacketConsumer = packetConsumer;
        packetConsumerFailure = null;
        consumedPacketCount = 0;
        retiredPacketCount = 0;
        try
        {
            session.Access(consumePacketsAction);
            if (retiredPacketCount != requiredPacketCount)
            {
                throw new InvalidOperationException(
                    "The native packet scan did not retire every packet.");
            }
            packetConsumptionCompleted = true;
            if (packetConsumerFailure is not null)
                ExceptionDispatchInfo.Capture(packetConsumerFailure).Throw();
            if (consumedPacketCount != requiredPacketCount)
            {
                throw new InvalidOperationException(
                    "The native packet scan did not consume every packet.");
            }
            return consumedPacketCount;
        }
        finally
        {
            pendingPacketConsumer = null;
            packetConsumerFailure = null;
            Volatile.Write(ref packetConsumptionState, 2);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Exception? failure = pendingEdit
            ? RollbackPendingEditCore()
            : null;
        try
        {
            workers.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        finally
        {
            try
            {
                session.Dispose();
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
            finally
            {
                try
                {
                    game.Dispose();
                }
                catch (Exception exception)
                {
                    failure = Combine(failure, exception);
                }
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void CaptureFirstPacket(
        scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        for (int chunkIndex = 0;
             chunkIndex < view.Chunks.Length;
             chunkIndex++)
        {
            NativeChunkRecord chunk = view.Chunks[chunkIndex];
            if (chunk.State != NativeChunkState.PacketReady ||
                !view.TryReadPacket(
                    chunkIndex,
                    out NativePacketReadView packet))
            {
                continue;
            }

            NativeRenderPacketRecord record = packet.Record;
            if (record.OpaqueFaceCount +
                record.TransparentFaceCount == 0)
            {
                continue;
            }
            if (record.OpaqueWordCount != packet.OpaqueWords.Length ||
                record.TransparentWordCount !=
                    packet.TransparentWords.Length)
            {
                view.Fail(
                    NativeGtrtFailureCode.InvalidPacketPublication);
                return;
            }

            capturedPacket = new NativePreUploadPacket(
                record.RenderDataId,
                chunk.ChunkX,
                chunk.ChunkY,
                chunk.ChunkZ,
                record.OpaqueFaceCount,
                record.OpaqueWordCount,
                record.TransparentFaceCount,
                record.TransparentWordCount);
            packetCaptured = true;
            return;
        }
    }

    private void ConsumePackets(scoped NativeLeaseView<byte> owner)
    {
        NativeChunkRenderPacketAction consumer =
            pendingPacketConsumer ??
            throw new InvalidOperationException(
                "The native packet consumer is not set.");
        var view = new NativeGtrtSessionView(owner.AsSpan());
        for (int chunkIndex = 0;
             chunkIndex < view.Chunks.Length;
             chunkIndex++)
        {
            NativeChunkRecord chunk = view.Chunks[chunkIndex];
            if (chunk.State != NativeChunkState.PacketReady ||
                !view.TryActivatePacket(
                    chunkIndex,
                    out NativePacketReadView packet))
            {
                continue;
            }

            try
            {
                NativeRenderPacketRecord record = packet.Record;
                var descriptor = new NativeChunkRenderPacketDescriptor(
                    record.RenderDataId,
                    checked(chunk.ChunkX * view.ChunkSizeX),
                    checked(chunk.ChunkY * view.ChunkSizeY),
                    checked(chunk.ChunkZ * view.ChunkSizeZ),
                    record.RegistryEpoch,
                    record.PublicationEpoch,
                    record.OpaqueFaceCount,
                    record.OpaqueWordCount,
                    record.TransparentFaceCount,
                    record.TransparentWordCount);
                if (packetConsumerFailure is null)
                {
                    try
                    {
                        consumer(
                            in descriptor,
                            packet.OpaqueWords,
                            packet.TransparentWords);
                        consumedPacketCount++;
                    }
                    catch (Exception exception)
                    {
                        packetConsumerFailure = exception;
                    }
                }
            }
            finally
            {
                if (!view.TryRetirePacket(chunkIndex))
                {
                    throw new InvalidOperationException(
                        "The native render packet could not retire.");
                }
                retiredPacketCount++;
            }
        }
    }

    private void EditBlockCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        GetChunkAndLocalCoordinate(
            pendingWorldX,
            view.ChunkSizeX,
            out int chunkX,
            out int localX);
        GetChunkAndLocalCoordinate(
            pendingWorldY,
            view.ChunkSizeY,
            out int chunkY,
            out int localY);
        GetChunkAndLocalCoordinate(
            pendingWorldZ,
            view.ChunkSizeZ,
            out int chunkZ,
            out int localZ);
        int chunkIndex = view.GetChunkIndex(chunkX, chunkY, chunkZ);
        if (chunkIndex < 0 ||
            !NativeGeneratedTerrain.TryGetBlock(
                ref view,
                chunkIndex,
                localX,
                localY,
                localZ,
                out ushort previousBlockId))
        {
            return;
        }
        if (previousBlockId == pendingBlockId)
        {
            pendingEditActionSucceeded = true;
            return;
        }
        if (!view.TryGetBlockDescriptor(pendingBlockId, out _))
            return;

        NativeChunkRecord activeChunk = view.Chunks[chunkIndex];
        pendingPreviousMaterializedChunkIndex =
            activeChunk.MaterializedChunkIndex;
        if (pendingPreviousMaterializedChunkIndex >= 0)
        {
            NativeMaterializedChunkRecord previous =
                view.MaterializedChunks[
                    pendingPreviousMaterializedChunkIndex];
            pendingPreviousRevision = previous.Revision;
            pendingPreviousPersistedRevision =
                previous.PersistedRevision;
            pendingPreviousStorageKind = previous.StorageKind;
        }
        else
        {
            pendingPreviousRevision = 0;
            pendingPreviousPersistedRevision = 0;
            pendingPreviousStorageKind =
                NativeChunkStorageKind.GeneratedProfile;
        }

        if (!NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                localX,
                localY,
                localZ,
                previousBlockId) ||
            !NativeMaterializedTerrain.TryMaterializeCompleteChunk(
                ref view,
                chunkIndex))
        {
            return;
        }

        pendingCurrentMaterializedChunkIndex =
            view.Chunks[chunkIndex].MaterializedChunkIndex;
        if (!NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                localX,
                localY,
                localZ,
                pendingBlockId))
        {
            return;
        }
        pendingPreviousBlockId = previousBlockId;
        pendingEditedChunkX = chunkX;
        pendingEditedChunkY = chunkY;
        pendingEditedChunkZ = chunkZ;
        pendingEditedLocalX = localX;
        pendingEditedLocalY = localY;
        pendingEditedLocalZ = localZ;
        pendingEditChanged = true;
        pendingEditActionSucceeded = true;
    }

    private void RollbackBlockCore(scoped NativeLeaseView<byte> owner)
    {
        pendingRollbackSucceeded = false;
        var view = new NativeGtrtSessionView(owner.AsSpan());
        int chunkIndex = view.GetChunkIndex(
            pendingEditedChunkX,
            pendingEditedChunkY,
            pendingEditedChunkZ);
        if (chunkIndex < 0 ||
            !NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                pendingEditedLocalX,
                pendingEditedLocalY,
                pendingEditedLocalZ,
                pendingPreviousBlockId))
        {
            return;
        }

        int materializedChunkIndex =
            view.Chunks[chunkIndex].MaterializedChunkIndex;
        if (materializedChunkIndex != pendingCurrentMaterializedChunkIndex)
            return;
        ref NativeMaterializedChunkRecord materialized =
            ref view.MaterializedChunks[materializedChunkIndex];
        if (pendingPreviousMaterializedChunkIndex >= 0)
        {
            materialized.StorageKind = pendingPreviousStorageKind;
            materialized.Revision = pendingPreviousRevision;
            materialized.PersistedRevision =
                pendingPreviousPersistedRevision;
        }
        else
        {
            materialized.PersistedRevision = materialized.Revision;
        }

        ref NativeChunkRecord active = ref view.Chunks[chunkIndex];
        active.StorageKind = materialized.StorageKind;
        active.DirtyRevision = materialized.Revision;
        pendingRollbackSucceeded = true;
    }

    private void ReadBlockCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        GetChunkAndLocalCoordinate(
            pendingWorldX,
            view.ChunkSizeX,
            out int chunkX,
            out int localX);
        GetChunkAndLocalCoordinate(
            pendingWorldY,
            view.ChunkSizeY,
            out int chunkY,
            out int localY);
        GetChunkAndLocalCoordinate(
            pendingWorldZ,
            view.ChunkSizeZ,
            out int chunkZ,
            out int localZ);
        int chunkIndex = view.GetChunkIndex(chunkX, chunkY, chunkZ);
        if (chunkIndex < 0 ||
            !NativeGeneratedTerrain.TryGetBlock(
                ref view,
                chunkIndex,
                localX,
                localY,
                localZ,
                out pendingReadBlockId))
        {
            return;
        }
        pendingReadSucceeded = true;
    }

    private Exception? RollbackPendingEditCore()
    {
        if (!pendingEdit)
            return null;

        pendingRollbackSucceeded = false;
        try
        {
            session.Access(rollbackBlockAction);
            if (!pendingRollbackSucceeded)
            {
                Volatile.Write(
                    ref completedRunCount,
                    int.MinValue);
                return new InvalidOperationException(
                    "The native block edit could not roll back.");
            }
            return null;
        }
        catch (Exception exception)
        {
            Volatile.Write(
                ref completedRunCount,
                int.MinValue);
            return exception;
        }
        finally
        {
            ClearPendingEdit();
        }
    }

    private void ClearPendingEdit()
    {
        pendingEdit = false;
        pendingEditChanged = false;
        pendingEditActionSucceeded = false;
        pendingRollbackSucceeded = false;
        pendingPreviousBlockId = 0;
        pendingPreviousMaterializedChunkIndex = -1;
        pendingCurrentMaterializedChunkIndex = -1;
        pendingPreviousRevision = 0;
        pendingPreviousPersistedRevision = 0;
        pendingPreviousStorageKind = default;
    }

    private static void GetChunkAndLocalCoordinate(
        int worldCoordinate,
        int chunkSize,
        out int chunkCoordinate,
        out int localCoordinate)
    {
        chunkCoordinate = worldCoordinate / chunkSize;
        localCoordinate = worldCoordinate % chunkSize;
        if (localCoordinate < 0)
        {
            chunkCoordinate--;
            localCoordinate += chunkSize;
        }
    }

    private static int DivideRoundUp(int value, int divisor) =>
        checked((value + divisor - 1) / divisor);

    private static Exception? Combine(
        Exception? first,
        Exception? second) =>
        first is null
            ? second
            : second is null
                ? first
                : new AggregateException(first, second);
}

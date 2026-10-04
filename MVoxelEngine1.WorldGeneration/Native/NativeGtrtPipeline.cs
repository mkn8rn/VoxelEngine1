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
public sealed class NativeGtrtPipeline : IDisposable
{
    private readonly NativeGameSnapshot game;
    private readonly NativeGtrtSession session;
    private readonly NativeGtrtWorkerPool workers;
    private readonly NativeWorldSaveExporter saveExporter;
    private NativeWorldSaveImportPlan? savePlan;
    private readonly GameSettings settings;
    private readonly NativeLeaseAction<byte> capturePacketAction;
    private readonly NativeLeaseAction<byte> consumePacketsAction;
    private readonly NativeLeaseAction<byte> editBlockAction;
    private readonly NativeLeaseAction<byte> planBlockEditAction;
    private readonly NativeLeaseAction<byte> rollbackBlockAction;
    private readonly NativeLeaseAction<byte> readBlockAction;
    private readonly NativeLeaseAction<byte> inspectPacketsAction;
    private readonly int requiredPacketCount;
    private readonly int chunkSizeX;
    private readonly int chunkSizeY;
    private readonly int chunkSizeZ;
    private readonly int requiredWidth;
    private readonly NativeLeaseAction<byte> commitRunAction;
    private readonly NativeLeaseAction<byte> rollbackRunAction;
    private NativePreUploadPacket previousPacket;
    private NativeStreamingStatistics streamingStatistics;
    private NativeStreamingStatistics previousStatistics;
    private int previousCenterX;
    private int previousCenterY;
    private int previousCenterZ;
    private int previousRunCount;
    private bool runRolledBack;
    private NativePreUploadPacket capturedPacket;
    private NativeChunkRenderPacketAction? pendingPacketConsumer;
    private NativeChunkRenderPacketAction? pendingPacketInspector;
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
    private NativeMaterializedStorageRequirements pendingStorageRequirements;
    private NativeChunkRecord pendingPreviousActiveChunk;
    private NativeMaterializedChunkRecord pendingPreviousMaterializedChunk;
    private NativeMaterializedSectionRecord pendingPreviousSection;
    private int pendingPreviousSectionIndex;
    private int pendingPreviousChunkCount;
    private int pendingPreviousSectionCount;
    private int pendingPreviousRawSectionCount;
    private bool pendingEditPlanned;
    private bool pendingEditMutationStarted;
    private long publishedSeed;
    private long coordinatorManagedAllocationBytes;
    private double? generationToRenderMilliseconds;
    private int disposed;
    private bool inspectingPackets;
    private NativeGtrtPipeline(NativeGameSnapshot game, NativeGtrtSession session, NativeGtrtWorkerPool workers, int requiredPacketCount, GameSettings settings, Action<string, string>? savePublisher, NativeWorldSaveImportPlan? savePlan)
    {
        this.game = game;
        this.session = session;
        this.workers = workers;
        this.settings = settings;
        this.savePlan = savePlan;
        saveExporter = new NativeWorldSaveExporter(session, savePublisher);
        this.requiredPacketCount = requiredPacketCount;
        chunkSizeX = settings.chunkMaxX;
        chunkSizeY = settings.chunkMaxY;
        chunkSizeZ = settings.chunkMaxZ;
        requiredWidth = checked(settings.lod1RenderDistance * 2 + 1);
        capturePacketAction = CaptureFirstPacket;
        consumePacketsAction = ConsumePackets;
        editBlockAction = EditBlockCore;
        planBlockEditAction = PlanBlockEditCore;
        rollbackBlockAction = RollbackBlockCore;
        readBlockAction = ReadBlockCore;
        inspectPacketsAction = InspectPacketsCore;
        commitRunAction = CommitRunCore;
        rollbackRunAction = RollbackRunCore;
    }

    public static NativeGtrtPipeline Create(BlockTextureAtlas textureAtlas) => CreateConfigured(textureAtlas, savePlan: null);
    internal static NativeGtrtPipeline Create(BlockTextureAtlas textureAtlas, NativeWorldSaveImportPlan savePlan) => CreateConfigured(textureAtlas, savePlan);
    private static NativeGtrtPipeline CreateConfigured(BlockTextureAtlas textureAtlas, NativeWorldSaveImportPlan? savePlan)
    {
        ArgumentNullException.ThrowIfNull(textureAtlas);
        FaceGenerationMode mode = FlagManager.flags.faceGenerationMode ?? FaceGenerationMode.Optimized;
        if (mode != FaceGenerationMode.Optimized)
        {
            throw new InvalidOperationException("The native headless GTRT path requires optimized faces.");
        }

        float generationWorkersPerCore = FlagManager.flags.worldGenWorkersPerCoreInitial ?? FlagManager.flags.worldGenWorkersPerCore ?? throw new InvalidOperationException("The world generation worker count is not set.");
        float meshWorkersPerCore = FlagManager.flags.meshRenderWorkersPerCoreInitial ?? FlagManager.flags.meshRenderWorkersPerCore ?? throw new InvalidOperationException("The mesh worker count is not set.");
        int generationWorkerCount = GetWorkerCount(generationWorkersPerCore);
        int meshWorkerCount = GetWorkerCount(meshWorkersPerCore);
        int runtimeGenerationWorkerCount = GetWorkerCount(FlagManager.flags.worldGenWorkersPerCore ?? generationWorkersPerCore);
        int runtimeMeshWorkerCount = GetWorkerCount(FlagManager.flags.meshRenderWorkersPerCore ?? meshWorkersPerCore);
        bool streamGeneration = GameManager.settings.renderStreamingAllowed && (FlagManager.flags.renderStreamingIfAllowed ?? false);
        return Create(textureAtlas, GameManager.settings, generationWorkerCount, meshWorkerCount, streamGeneration, savePlan, runtimeGenerationWorkerCount: runtimeGenerationWorkerCount, runtimeMeshWorkerCount: runtimeMeshWorkerCount);
    }

    internal static int GetWorkerCount(float workersPerCore)
    {
        if (!float.IsFinite(workersPerCore) || workersPerCore < 0)
            throw new ArgumentOutOfRangeException(nameof(workersPerCore), "The worker multiplier must be finite and nonnegative.");
        double count = (double)workersPerCore * Environment.ProcessorCount;
        if (count > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(workersPerCore), "The worker count exceeds the supported range.");
        return Math.Max(1, checked((int)count));
    }

    internal static NativeGtrtPipeline Create(BlockTextureAtlas textureAtlas, GameSettings settings, int generationWorkerCount, int meshWorkerCount, bool streamGeneration = false, NativeWorldSaveImportPlan? savePlan = null, Action<string, string>? savePublisher = null, int? runtimeGenerationWorkerCount = null, int? runtimeMeshWorkerCount = null)
    {
        ArgumentNullException.ThrowIfNull(textureAtlas);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(meshWorkerCount);
        int runtimeGeneration = runtimeGenerationWorkerCount ?? generationWorkerCount;
        int runtimeMesh = runtimeMeshWorkerCount ?? meshWorkerCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeGeneration, nameof(runtimeGenerationWorkerCount));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeMesh, nameof(runtimeMeshWorkerCount));
        int diameter = checked(settings.lod1RenderDistance * 2 + 1);
        int preparedRequiredPacketCount = checked(diameter * diameter * diameter);
        NativeGameSnapshot? preparedGame = null;
        NativeGtrtSession? preparedSession = null;
        NativeGtrtWorkerPool? preparedWorkers = null;
        try
        {
            preparedGame = NativeGameSnapshot.Create(textureAtlas);
            int sectionCountX = DivideRoundUp(settings.chunkMaxX, VoxelSection.Size);
            int sectionCountY = DivideRoundUp(settings.chunkMaxY, VoxelSection.Size);
            int sectionCountZ = DivideRoundUp(settings.chunkMaxZ, VoxelSection.Size);
            int editableSectionCapacity = checked(NativeGtrtSessionLayout.DefaultMaterializedChunkCapacity * sectionCountX * sectionCountY * sectionCountZ);
            int materializedChunkCapacity = checked(NativeGtrtSessionLayout.DefaultMaterializedChunkCapacity + (savePlan?.ChunkCount ?? 0));
            int materializedSectionCapacity = editableSectionCapacity;
            int materializedRawSectionCapacity = NativeGtrtSessionLayout.DefaultMaterializedChunkCapacity;
            preparedSession = NativeGtrtSession.Create(settings, preparedGame, Math.Max(generationWorkerCount, runtimeGeneration), Math.Max(meshWorkerCount, runtimeMesh), materializedChunkCapacity, materializedSectionCapacity, materializedRawSectionCapacity, materializedPaletteCapacity: 0, materializedPackedWordCapacity: 0);
            savePlan?.PrepareLazyImport(preparedSession);
            preparedWorkers = new NativeGtrtWorkerPool(preparedSession, generationWorkerCount, meshWorkerCount, streamGeneration, runtimeGeneration, runtimeMesh);
            return new NativeGtrtPipeline(preparedGame, preparedSession, preparedWorkers, preparedRequiredPacketCount, settings, savePublisher, savePlan);
        }
        catch
        {
            preparedWorkers?.Dispose();
            preparedSession?.Dispose();
            preparedGame?.Dispose();
            throw;
        }
    }

    public NativePreUploadPacket Run(long seed)
    {
        if (Interlocked.CompareExchange(ref completedRunCount, -1, 0) != 0)
        {
            throw new InvalidOperationException("The native GTRT seed is already published.");
        }

        publishedSeed = seed;
        try
        {
            NativePreUploadPacket packet = RunCore(seed, centerChunkX: 0, centerChunkY: 0, centerChunkZ: 0, recordInitialEndpoint: true);
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

    public NativePreUploadPacket MoveToChunk(int centerChunkX, int centerChunkY, int centerChunkZ)
    {
        ValidateNoInspection();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        int runCount = Volatile.Read(ref completedRunCount);
        if (runCount <= 0 || !packetConsumptionCompleted)
        {
            throw new InvalidOperationException("All current native packets must retire before movement.");
        }

        if (Interlocked.CompareExchange(ref completedRunCount, -1, runCount) != runCount)
        {
            throw new InvalidOperationException("The native GTRT pipeline is already moving.");
        }

        RememberCurrentRun(runCount);
        try
        {
            savePlan?.EnsureResidentPayloads(session, centerChunkX, centerChunkY, centerChunkZ);
            NativePreUploadPacket packet = RunStreamingCore(centerChunkX, centerChunkY, centerChunkZ);
            this.centerChunkX = centerChunkX;
            this.centerChunkY = centerChunkY;
            this.centerChunkZ = centerChunkZ;
            Volatile.Write(ref completedRunCount, checked(runCount + 1));
            return packet;
        }
        catch
        {
            RestorePreviousRun();
            if (Volatile.Read(ref completedRunCount) == -1)
                Volatile.Write(ref completedRunCount, runCount);
            throw;
        }
    }

    private void RememberCurrentRun(int runCount)
    {
        previousPacket = capturedPacket;
        previousStatistics = streamingStatistics;
        previousCenterX = centerChunkX;
        previousCenterY = centerChunkY;
        previousCenterZ = centerChunkZ;
        previousRunCount = runCount;
    }

    private void RestorePreviousRun()
    {
        runRolledBack = false;
        session.Access(rollbackRunAction);
        if (!runRolledBack)
            return;
        capturedPacket = previousPacket;
        streamingStatistics = previousStatistics;
        centerChunkX = previousCenterX;
        centerChunkY = previousCenterY;
        centerChunkZ = previousCenterZ;
        packetCaptured = true;
        packetConsumptionCompleted = true;
        Volatile.Write(ref packetConsumptionState, 2);
        Volatile.Write(ref completedRunCount, previousRunCount);
    }

    private static void CommitRunCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        view.CommitStreamingRun();
    }

    private void RollbackRunCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        runRolledBack = view.RollbackStreamingRun();
    }

    internal NativeStreamingStatistics StreamingStatistics => streamingStatistics;

    private NativePreUploadPacket RunStreamingCore(int destinationX, int destinationY, int destinationZ)
    {
        while (true)
        {
            try
            {
                return RunCore(publishedSeed, destinationX, destinationY, destinationZ, recordInitialEndpoint: false);
            }
            catch (InvalidOperationException failure)when (workers.CanGrowPacketStorage)
            {
                try
                {
                    RestorePreviousRun();
                    if (!runRolledBack)
                        throw new InvalidOperationException("Native packet growth requires a completed rollback.",failure);
                    session.ExpandPacketStorage();
                    Volatile.Write(ref completedRunCount, -1);
                }
                catch (Exception growthFailure)
                {
                    throw new AggregateException(failure, growthFailure);
                }
            }
        }
    }

    internal int GetRendererSlot(in NativeChunkRenderPacketDescriptor descriptor) => (NativeGtrtSessionView.FloorMod(descriptor.ChunkWorldX / chunkSizeX, requiredWidth) * requiredWidth + NativeGtrtSessionView.FloorMod(descriptor.ChunkWorldZ / chunkSizeZ, requiredWidth)) * requiredWidth + NativeGtrtSessionView.FloorMod(descriptor.ChunkWorldY / chunkSizeY, requiredWidth);
    private NativePreUploadPacket RunCore(long seed, int centerChunkX, int centerChunkY, int centerChunkZ, bool recordInitialEndpoint)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        packetCaptured = false;
        packetConsumptionCompleted = false;
        Volatile.Write(ref packetConsumptionState, 0);
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        workers.Run(seed, centerChunkX, centerChunkY, centerChunkZ);
        session.Access(capturePacketAction);
        if (!packetCaptured)
        {
            throw new InvalidOperationException("No native render packet reached the pre-upload boundary.");
        }

        if (recordInitialEndpoint)
        {
            generationToRenderMilliseconds = StartupPerformanceRecorder.RecordGenerationToRender();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        coordinatorManagedAllocationBytes = Math.Max(coordinatorManagedAllocationBytes, allocated);
        return capturedPacket;
    }

    public long MaximumWorkerManagedAllocationBytes => workers.MaximumManagedAllocationBytes;
    public long CoordinatorManagedAllocationBytes => Volatile.Read(ref coordinatorManagedAllocationBytes);
    public long InitialGenerationMilliseconds => workers.InitialGenerationMilliseconds;
    public long InitialMeshMilliseconds => workers.InitialMeshMilliseconds;
    internal int ActiveGenerationWorkerCount => workers.ActiveGenerationWorkerCount;
    internal int ActiveMeshWorkerCount => workers.ActiveMeshWorkerCount;
    internal int PreparedWorkerCount => workers.WorkerCount;

    internal void CopyWorkerAllocationSamples(Span<NativeWorkerAllocationSample> destination) => workers.CopyAllocationSamples(destination);
    internal void ObserveSeedPublication(Action observer) => session.ObservePublication(observer);
    internal NativeSessionAllocationMetrics GetAllocationMetrics() => NativeSessionAllocationMetrics.Capture(session);
    internal NativePreUploadPacket DescribePreUpload(in NativeChunkRenderPacketDescriptor descriptor) => new(descriptor.RenderDataId, descriptor.ChunkWorldX / chunkSizeX, descriptor.ChunkWorldY / chunkSizeY, descriptor.ChunkWorldZ / chunkSizeZ, descriptor.OpaqueFaceCount, descriptor.OpaqueWordCount, descriptor.TransparentFaceCount, descriptor.TransparentWordCount);
    public int RequiredPacketCount => requiredPacketCount;
    internal int CompletedRunCount => Volatile.Read(ref completedRunCount);
    public double? GenerationToRenderMilliseconds => generationToRenderMilliseconds;

    internal bool BeginBlockEdit(int worldX, int worldY, int worldZ, ushort blockId)
    {
        ValidateNoInspection();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (pendingEdit)
        {
            throw new InvalidOperationException("The prior native block edit is not resolved.");
        }

        int runCount = Volatile.Read(ref completedRunCount);
        if (runCount <= 0 || !packetConsumptionCompleted)
        {
            throw new InvalidOperationException("All current native packets must retire before an edit.");
        }

        if (Interlocked.CompareExchange(ref completedRunCount, -1, runCount) != runCount)
        {
            throw new InvalidOperationException("The native GTRT pipeline is already changing.");
        }

        pendingWorldX = worldX;
        pendingWorldY = worldY;
        pendingWorldZ = worldZ;
        pendingBlockId = blockId;
        pendingEditActionSucceeded = false;
        pendingEditChanged = false;
        pendingEditPlanned = false;
        pendingEditMutationStarted = false;
        try
        {
            session.Access(planBlockEditAction);
            if (!pendingEditPlanned)
                throw new InvalidOperationException("The native block edit is not valid in the resident world.");
            if (!pendingEditChanged)
            {
                Volatile.Write(ref completedRunCount, runCount);
                return false;
            }

            session.EnsureMaterializedCapacity(pendingStorageRequirements);
            session.Access(editBlockAction);
            if (!pendingEditActionSucceeded)
            {
                Volatile.Write(ref completedRunCount, runCount);
                throw new InvalidOperationException("The native block edit could not enter storage.");
            }

            pendingEdit = true;
            RememberCurrentRun(runCount);
            try
            {
                _ = RunStreamingCore(centerChunkX, centerChunkY, centerChunkZ);
                Volatile.Write(ref completedRunCount, checked(runCount + 1));
                return true;
            }
            catch (Exception failure)
            {
                Exception? rollbackFailure = RollbackPendingEditCore();
                if (rollbackFailure is not null)
                    Volatile.Write(ref completedRunCount, int.MinValue);
                if (rollbackFailure is null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                throw new AggregateException(failure, rollbackFailure);
            }
        }
        catch (Exception failure)
        {
            if (!pendingEdit && pendingEditMutationStarted)
            {
                try
                {
                    session.Access(rollbackBlockAction);
                    if (!pendingRollbackSucceeded)
                        throw new InvalidOperationException("The native edit preparation could not roll back.",failure);
                    pendingEditMutationStarted = false;
                }
                catch (Exception rollbackFailure)
                {
                    Volatile.Write(ref completedRunCount, int.MinValue);
                    throw new AggregateException(failure, rollbackFailure);
                }
            }

            if (Volatile.Read(ref completedRunCount) == -1)
                Volatile.Write(ref completedRunCount, runCount);
            throw;
        }
        finally
        {
            pendingBlockId = 0;
        }
    }

    internal void CommitBlockEdit()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!pendingEdit || !packetConsumptionCompleted)
        {
            throw new InvalidOperationException("A complete native block edit is not ready to commit.");
        }

        session.Access(commitRunAction);
        ClearPendingEdit();
    }

    internal void RollbackBlockEdit()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Exception? failure = RollbackPendingEditCore();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal ushort GetBlock(int worldX, int worldY, int worldZ)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref completedRunCount) <= 0 || pendingEdit)
        {
            throw new InvalidOperationException("Native world data is not available for a block read.");
        }

        pendingWorldX = worldX;
        pendingWorldY = worldY;
        pendingWorldZ = worldZ;
        pendingReadSucceeded = false;
        GetChunkAndLocalCoordinate(worldX, chunkSizeX, out int sourceX, out _);
        GetChunkAndLocalCoordinate(worldY, chunkSizeY, out int sourceY, out _);
        GetChunkAndLocalCoordinate(worldZ, chunkSizeZ, out int sourceZ, out _);
        savePlan?.EnsureBlockPayload(session, sourceX, sourceY, sourceZ, inspectingPackets);
        session.Access(readBlockAction);
        if (!pendingReadSucceeded)
        {
            throw new ArgumentOutOfRangeException(nameof(worldX), "The block is outside the native resident world.");
        }

        return pendingReadBlockId;
    }

    internal int SaveDirtyChunks(string quadsDirectory)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref completedRunCount) <= 0 || !packetConsumptionCompleted || pendingEdit)
        {
            throw new InvalidOperationException("Native world data is not ready for a save.");
        }

        int published;
        savePlan?.VerifyDeferredSourceFiles();
        try
        {
            published = saveExporter.SaveDirtyChunks(quadsDirectory);
        }
        catch
        {
            RefreshDeferredSaveMetadata(quadsDirectory);
            throw;
        }

        if (published != 0)
            RefreshDeferredSaveMetadata(quadsDirectory);
        return published;
    }

    private void RefreshDeferredSaveMetadata(string quadsDirectory)
    {
        if (savePlan is null || !savePlan.HasDeferredChunks)
            return;
        NativeWorldSaveImportPlan refreshed = NativeWorldSaveImportPlan.Create(quadsDirectory, settings);
        refreshed.ReserveSavedMetadata(session);
        savePlan = refreshed;
    }

    public int ConsumeReadyPackets(NativeChunkRenderPacketAction packetConsumer)
    {
        ArgumentNullException.ThrowIfNull(packetConsumer);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!packetCaptured)
        {
            throw new InvalidOperationException("The native GTRT run is not complete.");
        }

        if (Interlocked.CompareExchange(ref packetConsumptionState, 1, 0) != 0)
        {
            throw new InvalidOperationException("Native render packets can be consumed only once.");
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
                throw new InvalidOperationException("The native packet scan did not retire every packet.");
            }

            packetConsumptionCompleted = true;
            ThrowIfPacketConsumerFailed();
            if (consumedPacketCount != requiredPacketCount)
            {
                throw new InvalidOperationException("The native packet scan did not consume every packet.");
            }

            if (!pendingEdit)
                session.Access(commitRunAction);
            return consumedPacketCount;
        }
        catch
        {
            RestorePreviousRun();
            throw;
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
        ValidateNoInspection();
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Exception? failure = pendingEdit ? RollbackPendingEditCore() : null;
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

    public void InspectRenderPackets(NativeChunkRenderPacketAction inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateNoInspection();
        if (Volatile.Read(ref completedRunCount) <= 0 || !packetConsumptionCompleted || pendingEdit)
        {
            throw new InvalidOperationException("Native render data is not ready for inspection.");
        }

        inspectingPackets = true;
        pendingPacketInspector = inspector;
        try
        {
            session.Access(inspectPacketsAction);
        }
        finally
        {
            pendingPacketInspector = null;
            inspectingPackets = false;
        }
    }

    internal void InspectState(NativeLeaseAction<byte> inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateNoInspection();
        if (Volatile.Read(ref completedRunCount) <= 0 || !packetConsumptionCompleted || pendingEdit)
            throw new InvalidOperationException("Native world data is not ready for inspection.");
        inspectingPackets = true;
        try
        {
            session.Access(inspector);
        }
        finally
        {
            inspectingPackets = false;
        }
    }

    private void ValidateNoInspection()
    {
        if (inspectingPackets)
            throw new InvalidOperationException("Native render inspection is already active.");
    }

    private void InspectPacketsCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        NativeChunkRenderPacketAction inspector = pendingPacketInspector!;
        for (int index = 0; index < view.Chunks.Length; index++)
        {
            if (!view.TryInspectRetiredPacket(index, out NativePacketReadView packet))
                continue;
            NativeChunkRecord chunk = view.Chunks[index];
            NativeRenderPacketRecord record = packet.Record;
            var descriptor = new NativeChunkRenderPacketDescriptor(record.RenderDataId, checked(chunk.ChunkX * view.ChunkSizeX), checked(chunk.ChunkY * view.ChunkSizeY), checked(chunk.ChunkZ * view.ChunkSizeZ), record.RegistryEpoch, record.PublicationEpoch, record.OpaqueFaceCount, record.OpaqueWordCount, record.TransparentFaceCount, record.TransparentWordCount);
            inspector(in descriptor, packet.OpaqueWords, packet.TransparentWords);
        }
    }

    private void CaptureFirstPacket(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        streamingStatistics = view.StreamingStatistics;
        for (int chunkIndex = 0; chunkIndex < view.Chunks.Length; chunkIndex++)
        {
            NativeChunkRecord chunk = view.Chunks[chunkIndex];
            scoped NativePacketReadView packet;
            bool readable = chunk.State == NativeChunkState.PacketReady ? view.TryReadPacket(chunkIndex, out packet) : view.TryInspectRetiredPacket(chunkIndex, out packet);
            if (!readable)
            {
                continue;
            }

            NativeRenderPacketRecord record = packet.Record;
            if (record.OpaqueWordCount != packet.OpaqueWords.Length || record.TransparentWordCount != packet.TransparentWords.Length)
            {
                view.Fail(NativeGtrtFailureCode.InvalidPacketPublication);
                return;
            }

            if (packetCaptured && record.OpaqueFaceCount + record.TransparentFaceCount == 0)
                continue;
            capturedPacket = new NativePreUploadPacket(record.RenderDataId, chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ, record.OpaqueFaceCount, record.OpaqueWordCount, record.TransparentFaceCount, record.TransparentWordCount);
            packetCaptured = true;
            if (record.OpaqueFaceCount + record.TransparentFaceCount != 0)
                return;
        }
    }

    private void ThrowIfPacketConsumerFailed()
    {
        if (packetConsumerFailure is not null)
            ExceptionDispatchInfo.Capture(packetConsumerFailure).Throw();
    }

    private void ConsumePackets(scoped NativeLeaseView<byte> owner)
    {
        NativeChunkRenderPacketAction consumer = pendingPacketConsumer ?? throw new InvalidOperationException("The native packet consumer is not set.");
        var view = new NativeGtrtSessionView(owner.AsSpan());
        for (int chunkIndex = 0; chunkIndex < view.Chunks.Length; chunkIndex++)
        {
            NativeChunkRecord chunk = view.Chunks[chunkIndex];
            scoped NativePacketReadView packet = default;
            bool retained = chunk.State == NativeChunkState.Retired;
            bool readable = retained ? view.TryInspectRetiredPacket(chunkIndex, out packet) : chunk.State == NativeChunkState.PacketReady && view.TryActivatePacket(chunkIndex, out packet);
            if (!readable)
            {
                continue;
            }

            Exception? packetFailure = null;
            bool retirementSucceeded = retained;
            try
            {
                NativeRenderPacketRecord record = packet.Record;
                var descriptor = new NativeChunkRenderPacketDescriptor(record.RenderDataId, checked(chunk.ChunkX * view.ChunkSizeX), checked(chunk.ChunkY * view.ChunkSizeY), checked(chunk.ChunkZ * view.ChunkSizeZ), record.RegistryEpoch, record.PublicationEpoch, record.OpaqueFaceCount, record.OpaqueWordCount, record.TransparentFaceCount, record.TransparentWordCount);
                if (packetConsumerFailure is null)
                {
                    try
                    {
                        consumer(in descriptor, packet.OpaqueWords, packet.TransparentWords);
                        consumedPacketCount++;
                    }
                    catch (Exception exception)
                    {
                        packetConsumerFailure = exception;
                    }
                }
            }
            catch (Exception exception)
            {
                packetFailure = exception;
            }
            finally
            {
                retirementSucceeded = retained || view.TryRetirePacket(chunkIndex);
                if (retirementSucceeded)
                    retiredPacketCount++;
            }

            if (!retirementSucceeded)
                throw new InvalidOperationException("The native render packet could not retire.");
            if (packetFailure is not null)
                ExceptionDispatchInfo.Capture(packetFailure).Throw();
        }
    }

    private void PlanBlockEditCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        GetChunkAndLocalCoordinate(pendingWorldX, view.ChunkSizeX, out int chunkX, out int localX);
        GetChunkAndLocalCoordinate(pendingWorldY, view.ChunkSizeY, out int chunkY, out int localY);
        GetChunkAndLocalCoordinate(pendingWorldZ, view.ChunkSizeZ, out int chunkZ, out int localZ);
        int chunkIndex = view.GetChunkIndex(chunkX, chunkY, chunkZ);
        if (chunkIndex < 0 || !NativeGeneratedTerrain.TryGetBlock(ref view, chunkIndex, localX, localY, localZ, out ushort previousBlockId))
        {
            return;
        }

        if (previousBlockId == pendingBlockId)
        {
            pendingEditPlanned = true;
            return;
        }

        if (!view.TryGetBlockDescriptor(pendingBlockId, out _))
            return;
        pendingEditedChunkX = chunkX;
        pendingEditedChunkY = chunkY;
        pendingEditedChunkZ = chunkZ;
        pendingEditedLocalX = localX;
        pendingEditedLocalY = localY;
        pendingEditedLocalZ = localZ;
        pendingPreviousBlockId = previousBlockId;
        pendingEditPlanned = NativeMaterializedTerrain.TryGetEditStorageRequirements(ref view, chunkIndex, localX, localY, localZ, out pendingStorageRequirements);
        pendingEditChanged = pendingEditPlanned;
    }

    private void EditBlockCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        int chunkX = pendingEditedChunkX;
        int chunkY = pendingEditedChunkY;
        int chunkZ = pendingEditedChunkZ;
        int localX = pendingEditedLocalX;
        int localY = pendingEditedLocalY;
        int localZ = pendingEditedLocalZ;
        int chunkIndex = view.GetChunkIndex(chunkX, chunkY, chunkZ);
        NativeChunkRecord activeChunk = view.Chunks[chunkIndex];
        pendingPreviousActiveChunk = activeChunk;
        pendingPreviousChunkCount = view.State.MaterializedChunkCount;
        pendingPreviousSectionCount = view.State.MaterializedSectionCount;
        pendingPreviousRawSectionCount = view.State.MaterializedRawSectionCount;
        pendingPreviousMaterializedChunkIndex = activeChunk.MaterializedChunkIndex;
        pendingPreviousSectionIndex = -1;
        if (pendingPreviousMaterializedChunkIndex >= 0)
        {
            pendingPreviousMaterializedChunk = view.MaterializedChunks[pendingPreviousMaterializedChunkIndex];
            int section = NativeMaterializedTerrain.GetSectionIndex(ref view, localX, localY, localZ);
            pendingPreviousSectionIndex = view.MaterializedSectionMaps[pendingPreviousMaterializedChunk.SectionMapOffset + section];
            if (pendingPreviousSectionIndex >= 0)
                pendingPreviousSection = view.MaterializedSections[pendingPreviousSectionIndex];
        }

        pendingEditMutationStarted = true;
        if (!NativeMaterializedTerrain.TrySetBlock(ref view, chunkIndex, localX, localY, localZ, pendingPreviousBlockId) || !NativeMaterializedTerrain.TryMaterializeCompleteChunk(ref view, chunkIndex))
        {
            return;
        }

        pendingCurrentMaterializedChunkIndex = view.Chunks[chunkIndex].MaterializedChunkIndex;
        if (!NativeMaterializedTerrain.TrySetBlock(ref view, chunkIndex, localX, localY, localZ, pendingBlockId))
        {
            return;
        }

        view.InvalidateEditedMeshes(chunkX, chunkY, chunkZ, localX, localY, localZ);
        pendingEditChanged = true;
        pendingEditActionSucceeded = true;
    }

    private void RollbackBlockCore(scoped NativeLeaseView<byte> owner)
    {
        pendingRollbackSucceeded = false;
        var view = new NativeGtrtSessionView(owner.AsSpan());
        int chunkIndex = view.GetChunkIndex(pendingEditedChunkX, pendingEditedChunkY, pendingEditedChunkZ);
        if (chunkIndex < 0)
            return;
        int materializedChunkIndex = pendingPreviousMaterializedChunkIndex >= 0 ? pendingPreviousMaterializedChunkIndex : pendingPreviousChunkCount;
        if (pendingPreviousMaterializedChunkIndex >= 0)
        {
            if (pendingPreviousSectionIndex >= 0)
            {
                if (pendingPreviousSection.StorageKind == NativeSectionStorageKind.Raw)
                {
                    int voxelIndex = pendingPreviousSection.RawVoxelOffset + NativeMaterializedTerrain.GetSectionLocalIndex(pendingEditedLocalX, pendingEditedLocalY, pendingEditedLocalZ);
                    view.MaterializedRawVoxels[voxelIndex] = pendingPreviousBlockId;
                }

                view.MaterializedSections[pendingPreviousSectionIndex] = pendingPreviousSection;
            }

            view.MaterializedChunks[materializedChunkIndex] = pendingPreviousMaterializedChunk;
            Span<int> maps = view.MaterializedSectionMaps.Slice(pendingPreviousMaterializedChunk.SectionMapOffset, view.SectionsPerChunk);
            foreach (ref int entry in maps)
                if (entry >= pendingPreviousSectionCount)
                    entry = -1;
        }
        else
        {
            if (view.State.MaterializedChunkCount > pendingPreviousChunkCount)
                view.RemoveMaterializedChunkIndex(materializedChunkIndex);
            view.MaterializedChunks[materializedChunkIndex] = default;
            view.MaterializedSectionMaps.Slice(materializedChunkIndex * view.SectionsPerChunk, view.SectionsPerChunk).Fill(-1);
        }

        view.State.MaterializedChunkCount = pendingPreviousChunkCount;
        view.State.MaterializedSectionCount = pendingPreviousSectionCount;
        view.State.MaterializedRawSectionCount = pendingPreviousRawSectionCount;
        view.State.FailureCode = 0;
        view.Chunks[chunkIndex] = pendingPreviousActiveChunk;
        foreach (ref NativeChunkRecord chunk in view.Chunks)
            chunk.Flags &= ~(int)NativeChunkFlags.MeshInvalidated;
        pendingRollbackSucceeded = true;
    }

    private void ReadBlockCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        GetChunkAndLocalCoordinate(pendingWorldX, view.ChunkSizeX, out int chunkX, out int localX);
        GetChunkAndLocalCoordinate(pendingWorldY, view.ChunkSizeY, out int chunkY, out int localY);
        GetChunkAndLocalCoordinate(pendingWorldZ, view.ChunkSizeZ, out int chunkZ, out int localZ);
        int residentY = view.State.CenterChunkY;
        int chunkIndex = view.GetChunkIndex(chunkX, residentY, chunkZ);
        if (chunkIndex < 0 || !NativeGeneratedTerrain.TryGetBlock(ref view, chunkIndex, localX, checked(localY + (chunkY - residentY) * view.ChunkSizeY), localZ, out pendingReadBlockId))
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
            RestorePreviousRun();
            session.Access(rollbackBlockAction);
            if (!pendingRollbackSucceeded)
            {
                Volatile.Write(ref completedRunCount, int.MinValue);
                return new InvalidOperationException("The native block edit could not roll back.");
            }

            return null;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref completedRunCount, int.MinValue);
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
        pendingEditPlanned = false;
        pendingEditMutationStarted = false;
        pendingRollbackSucceeded = false;
        pendingPreviousBlockId = 0;
        pendingPreviousMaterializedChunkIndex = -1;
        pendingCurrentMaterializedChunkIndex = -1;
        pendingPreviousActiveChunk = default;
        pendingPreviousMaterializedChunk = default;
        pendingPreviousSection = default;
    }

    private static void GetChunkAndLocalCoordinate(int worldCoordinate, int chunkSize, out int chunkCoordinate, out int localCoordinate)
    {
        chunkCoordinate = worldCoordinate / chunkSize;
        localCoordinate = worldCoordinate % chunkSize;
        if (localCoordinate < 0)
        {
            chunkCoordinate--;
            localCoordinate += chunkSize;
        }
    }

    private static int DivideRoundUp(int value, int divisor) => checked((value + divisor - 1) / divisor);
    private static Exception? Combine(Exception? first, Exception? second) => first is null ? second : second is null ? first : new AggregateException(first, second);
}

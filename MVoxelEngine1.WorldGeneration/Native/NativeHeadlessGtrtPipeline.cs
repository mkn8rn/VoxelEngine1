using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
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
    private readonly NativeLeaseAction<byte> capturePacketAction;
    private readonly NativeLeaseAction<byte> consumePacketsAction;
    private readonly int requiredPacketCount;
    private NativePreUploadPacket capturedPacket;
    private NativeChunkRenderPacketAction? pendingPacketConsumer;
    private bool packetCaptured;
    private int consumedPacketCount;
    private int packetConsumptionState;
    private int completedRunCount;
    private bool packetConsumptionCompleted;
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
        this.requiredPacketCount = requiredPacketCount;
        capturePacketAction = CaptureFirstPacket;
        consumePacketsAction = ConsumePackets;
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
            int materializedChunkCapacity = Math.Max(
                NativeGtrtSessionLayout.DefaultMaterializedChunkCapacity,
                savePlan?.ChunkCount ?? 0);
            int materializedSectionCapacity = Math.Max(
                NativeGtrtSessionLayout.DefaultMaterializedSectionCapacity,
                savePlan?.SectionCount ?? 0);
            int materializedRawSectionCapacity = Math.Max(
                NativeGtrtSessionLayout.DefaultMaterializedSectionCapacity,
                savePlan?.RawSectionCount ?? 0);
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
        consumedPacketCount = 0;
        try
        {
            session.Access(consumePacketsAction);
            if (consumedPacketCount != requiredPacketCount)
            {
                throw new InvalidOperationException(
                    "The native packet scan did not consume every packet.");
            }
            packetConsumptionCompleted = true;
            return consumedPacketCount;
        }
        finally
        {
            pendingPacketConsumer = null;
            Volatile.Write(ref packetConsumptionState, 2);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            workers.Dispose();
        }
        finally
        {
            try
            {
                session.Dispose();
            }
            finally
            {
                game.Dispose();
            }
        }
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
                consumer(
                    in descriptor,
                    packet.OpaqueWords,
                    packet.TransparentWords);
                consumedPacketCount++;
            }
            finally
            {
                if (!view.TryRetirePacket(chunkIndex))
                {
                    throw new InvalidOperationException(
                        "The native render packet could not retire.");
                }
            }
        }
    }
}

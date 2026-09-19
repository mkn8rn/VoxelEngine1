using System.Runtime.ExceptionServices;
using MVoxelEngine1.Graphics;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using OpenTK.Graphics.OpenGL4;

namespace MVoxelEngine1.WorldGeneration.Native;

internal delegate INativeChunkRenderer? NativeChunkRendererFactory(
    in NativeChunkRenderPacketDescriptor descriptor,
    ReadOnlySpan<uint> opaqueWords,
    ReadOnlySpan<uint> transparentWords);

public sealed class NativeWorld : IDisposable, IPlayerChunkPositionSink
{
    private static readonly NativeChunkRendererFactory OpenGlRendererFactory =
        CreateOpenGlRenderer;

    private readonly NativeGtrtPipeline pipeline;
    private readonly NativeChunkRendererFactory rendererFactory;
    private readonly NativeChunkRenderPacketAction uploadPacketAction;
    private readonly int ownerThreadId;
    private readonly string? quadsDirectory;
    private INativeChunkRenderer?[] currentRenderers;
    private INativeChunkRenderer?[] stagingRenderers;
    private (int cx, int cy, int cz) playerChunkPosition;
    private int stagingCount;
    private int disposed;

    private NativeWorld(
        NativeGtrtPipeline pipeline,
        long seed,
        NativeChunkRendererFactory rendererFactory,
        string? quadsDirectory)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(rendererFactory);

        this.pipeline = pipeline;
        this.rendererFactory = rendererFactory;
        this.quadsDirectory = quadsDirectory;
        ownerThreadId = Environment.CurrentManagedThreadId;
        currentRenderers = new INativeChunkRenderer?[pipeline.RequiredPacketCount];
        stagingRenderers = new INativeChunkRenderer?[pipeline.RequiredPacketCount];
        uploadPacketAction = UploadPacket;

        try
        {
            pipeline.Run(seed);
            PublishReadyPackets();
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure =
                ReleaseAfterConstructionFailure();
            if (cleanupFailure is null)
                ExceptionDispatchInfo.Capture(failure).Throw();

            throw new AggregateException(failure, cleanupFailure);
        }
    }

    public static NativeWorld CreateOpenGl(BlockTextureAtlas textureAtlas)
    {
        ArgumentNullException.ThrowIfNull(textureAtlas);

        var loader = new WorldLoader();
        loader.ChooseWorld(
            FlagManager.flags.worldName,
            FlagManager.flags.seed);

        string quadsDirectory = Path.Combine(
            loader.currentWorldSaveDirectory,
            loader.RegionID.ToString(),
            "quads");
        NativeWorldSaveImportPlan savePlan =
            NativeWorldSaveImportPlan.Create(
                quadsDirectory,
                GameManager.settings);
        NativeGtrtPipeline pipeline =
            NativeGtrtPipeline.Create(textureAtlas, savePlan);
        return CreateOwned(
            pipeline,
            loader.seed,
            OpenGlRendererFactory,
            quadsDirectory);
    }

    internal static NativeWorld CreateForTesting(
        NativeGtrtPipeline pipeline,
        long seed,
        NativeChunkRendererFactory rendererFactory,
        string? quadsDirectory = null) =>
        CreateOwned(pipeline, seed, rendererFactory, quadsDirectory);

    public (int cx, int cy, int cz) PlayerChunkPosition
    {
        get
        {
            ValidateOwner();
            return playerChunkPosition;
        }
        set
        {
            ValidateOwner();
            if (value == playerChunkPosition)
                return;

            pipeline.MoveToChunk(value.cx, value.cy, value.cz);
            bool bankPublished = false;
            try
            {
                PublishReadyPackets(
                    commitBlockEdit: false,
                    out bankPublished);
                playerChunkPosition = value;
            }
            catch
            {
                if (bankPublished)
                    playerChunkPosition = value;
                throw;
            }
        }
    }

    internal int RendererSlotCount => currentRenderers.Length;

    public ushort GetBlock(int worldX, int worldY, int worldZ)
    {
        ValidateOwner();
        return pipeline.GetBlock(worldX, worldY, worldZ);
    }

    public bool SetBlock(
        int worldX,
        int worldY,
        int worldZ,
        ushort blockId)
    {
        ValidateOwner();
        if (!pipeline.BeginBlockEdit(
                worldX,
                worldY,
                worldZ,
                blockId))
        {
            return false;
        }

        bool bankPublished = false;
        try
        {
            PublishReadyPackets(
                commitBlockEdit: true,
                out bankPublished);
            return true;
        }
        catch (Exception failure)
        {
            if (bankPublished)
                throw;

            Exception? rollbackFailure = null;
            try
            {
                pipeline.RollbackBlockEdit();
            }
            catch (Exception exception)
            {
                rollbackFailure = exception;
            }

            if (rollbackFailure is null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            throw new AggregateException(failure, rollbackFailure);
        }
    }

    public int Save()
    {
        ValidateOwner();
        if (quadsDirectory is null)
        {
            throw new InvalidOperationException(
                "The native world does not have a save directory.");
        }
        return pipeline.SaveDirtyChunks(quadsDirectory);
    }

    public void Render(ShaderProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        ValidateOwner();
        ChunkRender.ProcessPendingDeletes();

        GL.DepthMask(true);
        for (int index = 0; index < currentRenderers.Length; index++)
            currentRenderers[index]?.RenderOpaque(program);

        GL.DepthMask(false);
        for (int index = 0; index < currentRenderers.Length; index++)
            currentRenderers[index]?.RenderTransparent(program);
        GL.DepthMask(true);
    }

    public void Dispose()
    {
        ValidateOwnerThread();
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Exception? failure = null;
        if (quadsDirectory is not null)
        {
            try
            {
                _ = pipeline.SaveDirtyChunks(quadsDirectory);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        failure = Combine(
            failure,
            ReleaseRendererSet(currentRenderers));
        failure = Combine(
            failure,
            ReleaseRendererSet(stagingRenderers));

        try
        {
            pipeline.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void PublishReadyPackets()
    {
        PublishReadyPackets(
            commitBlockEdit: false,
            out _);
    }

    private void PublishReadyPackets(
        bool commitBlockEdit,
        out bool bankPublished)
    {
        bankPublished = false;
        stagingCount = 0;
        try
        {
            int consumed = pipeline.ConsumeReadyPackets(uploadPacketAction);
            if (consumed != stagingRenderers.Length ||
                stagingCount != stagingRenderers.Length)
            {
                throw new InvalidOperationException(
                    "The native renderer bank did not receive every packet.");
            }

            if (commitBlockEdit)
                pipeline.CommitBlockEdit();

            INativeChunkRenderer?[] previous = currentRenderers;
            currentRenderers = stagingRenderers;
            stagingRenderers = previous;
            bankPublished = true;
            Exception? releaseFailure =
                ReleaseRendererSet(stagingRenderers);
            if (releaseFailure is not null)
                ExceptionDispatchInfo.Capture(releaseFailure).Throw();
        }
        catch (Exception failure)
        {
            Exception? releaseFailure =
                ReleaseRendererSet(stagingRenderers);
            if (releaseFailure is null)
                ExceptionDispatchInfo.Capture(failure).Throw();

            throw new AggregateException(failure, releaseFailure);
        }
        finally
        {
            stagingCount = 0;
        }
    }

    private void UploadPacket(
        in NativeChunkRenderPacketDescriptor descriptor,
        ReadOnlySpan<uint> opaqueWords,
        ReadOnlySpan<uint> transparentWords)
    {
        if ((uint)stagingCount >= (uint)stagingRenderers.Length)
        {
            throw new InvalidOperationException(
                "The native renderer bank received too many packets.");
        }

        stagingRenderers[stagingCount++] = rendererFactory(
            in descriptor,
            opaqueWords,
            transparentWords);
    }

    private static INativeChunkRenderer? CreateOpenGlRenderer(
        in NativeChunkRenderPacketDescriptor descriptor,
        ReadOnlySpan<uint> opaqueWords,
        ReadOnlySpan<uint> transparentWords) =>
        ChunkRender.UploadNative(
            in descriptor,
            opaqueWords,
            transparentWords);

    private static NativeWorld CreateOwned(
        NativeGtrtPipeline pipeline,
        long seed,
        NativeChunkRendererFactory rendererFactory,
        string? quadsDirectory)
    {
        try
        {
            return new NativeWorld(
                pipeline,
                seed,
                rendererFactory,
                quadsDirectory);
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    private Exception? ReleaseAfterConstructionFailure()
    {
        Interlocked.Exchange(ref disposed, 1);
        Exception? failure = ReleaseRendererSet(currentRenderers);
        failure = Combine(
            failure,
            ReleaseRendererSet(stagingRenderers));
        try
        {
            pipeline.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        return failure;
    }

    private static Exception? ReleaseRendererSet(
        INativeChunkRenderer?[] renderers)
    {
        Exception? failure = null;
        for (int index = 0; index < renderers.Length; index++)
        {
            INativeChunkRenderer? renderer = renderers[index];
            renderers[index] = null;
            if (renderer is null)
                continue;

            try
            {
                renderer.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        return failure;
    }

    private static Exception? Combine(
        Exception? first,
        Exception? second) =>
        first is null
            ? second
            : second is null
                ? first
                : new AggregateException(first, second);

    private void ValidateOwner()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        ValidateOwnerThread();
    }

    private void ValidateOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
        {
            throw new InvalidOperationException(
                "The native world must stay on its creating thread.");
        }
    }
}

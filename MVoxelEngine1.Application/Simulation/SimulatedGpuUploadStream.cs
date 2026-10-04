using System.Text.Json;
using System.Buffers;
using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.WorldGeneration.Native;
using OpenTK.Mathematics;

namespace MVoxelEngine1.Application.Simulation
{
    internal sealed class SimulatedGpuUploadStream : IAsyncDisposable
    {
        private const int RecordQueueCapacity = 4;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private readonly record struct ChunkIdentity(int ChunkX, int ChunkY, int ChunkZ, float WorldOriginX, float WorldOriginY, float WorldOriginZ);
        private readonly record struct ActiveChunkCapture(ChunkIdentity Chunk, long? RenderDataId, bool OpenGlUploaded);
        private sealed record CameraCapture(Vector3 Position, Vector3 Front, Vector3 Up, Matrix4 Model, Matrix4 View, Matrix4 Projection, int PlayerChunkX, int PlayerChunkY, int PlayerChunkZ);
        private abstract record StreamRecord;
        private sealed record QueuedRecord(long Sequence, long RetainedPayloadBytes, StreamRecord Record);
        private sealed record UploadRecord(long Sequence, byte[] Payload) : StreamRecord;
        private sealed record DeletionRecord(long FrameIndex, long RenderDataId, ChunkIdentity Chunk) : StreamRecord;
        private sealed record RenderFrameRecord(long FrameIndex, double SimulationElapsedSeconds, double WallElapsedSeconds, double DeltaSeconds, PlayerInputKeys Input, CameraCapture Camera, int ActiveChunkCount, long UploadsThisFrame, long[] OpaqueDrawRenderDataIds, long[] TransparentDrawRenderDataIds) : StreamRecord;
        private sealed record SnapshotRecord(int SnapshotIndex, string Name, long FrameIndex, double SimulationElapsedSeconds, CameraCapture Camera, ActiveChunkCapture[] ActiveChunks) : StreamRecord;
        private sealed record InputBoundaryRecord(string Type, int StepIndex, TimedPlayerInputStep Step, double SimulationElapsedSeconds, CameraCapture Camera) : StreamRecord;
        private sealed record CompletionRecord(double SimulationElapsedSeconds, double WallElapsedSeconds, long FrameCount, long UploadCount, long DeletionCount, int SnapshotCount) : StreamRecord;
        private readonly NativeWorld world;
        private readonly Player player;
        private readonly int windowWidth;
        private readonly int windowHeight;
        private readonly string finalOutputPath;
        private readonly string temporaryOutputPath;
        private readonly FileStream fileStream;
        private readonly Utf8JsonWriter writer;
        private readonly Channel<QueuedRecord> records;
        private readonly SemaphoreSlim retainedRecordSlots;
        private readonly CancellationTokenSource writerFailureCancellation = new();
        private readonly Task writerTask;
        private readonly int writerDelayMilliseconds;
        private readonly int? writerFailAfterRecords;
        private readonly Dictionary<long, ChunkIdentity> uploadedRenderData = new();
        private HashSet<long> activeRenderData = new();
        private ExceptionDispatchInfo? writerFailure;
        private long nextSequence;
        private long writtenRecordCount;
        private int retainedRecordCount;
        private int peakRetainedRecordCount;
        private long retainedPayloadBytes;
        private long peakRetainedPayloadBytes;
        private long frameCount;
        private long uploadCount;
        private long deletionCount;
        private int snapshotCount;
        private bool completionQueued;
        private bool outputResourcesDisposed;
        private bool finalOutputPublished;
        private bool disposed;
        private int validatedRevision = -1;
        public SimulatedGpuUploadStream(string outputPath, string inputScript, int frameRate, BlockTextureAtlas textureAtlas, NativeWorld world, Player player, int windowWidth, int windowHeight, int writerDelayMilliseconds, int? writerFailAfterRecords)
        {
            this.world = world;
            this.player = player;
            this.windowWidth = windowWidth;
            this.windowHeight = windowHeight;
            this.writerDelayMilliseconds = writerDelayMilliseconds;
            this.writerFailAfterRecords = writerFailAfterRecords;
            finalOutputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(finalOutputPath) ?? throw new InvalidOperationException("The simulated GPU output directory is not valid.");
            Directory.CreateDirectory(outputDirectory);
            if (File.Exists(finalOutputPath))
                throw new IOException($"The simulated GPU output already exists: {finalOutputPath}");
            temporaryOutputPath = Path.Combine(outputDirectory, $".{Path.GetFileName(finalOutputPath)}.{Guid.NewGuid():N}.incomplete");
            fileStream = new FileStream(temporaryOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1_048_576, FileOptions.SequentialScan);
            writer = new Utf8JsonWriter(fileStream, new JsonWriterOptions { Indented = false });
            records = Channel.CreateBounded<QueuedRecord>(new BoundedChannelOptions(RecordQueueCapacity) { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
            retainedRecordSlots = new SemaphoreSlim(RecordQueueCapacity, RecordQueueCapacity);
            try
            {
                WriteSessionHeader(inputScript, frameRate, textureAtlas);
                writerTask = Task.Factory.StartNew(WriteRecords, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                try
                {
                    DisposeOutputResources();
                }
                finally
                {
                    DeleteTemporaryOutput();
                    writerFailureCancellation.Dispose();
                    retainedRecordSlots.Dispose();
                }

                throw;
            }
        }

        public SimulatedRenderFrameState RenderFrame(long frameIndex, double simulationElapsedSeconds, double wallElapsedSeconds, double deltaSeconds, PlayerInputKeys input)
        {
            if (FlagManager.flags.faceGenerationMode == FaceGenerationMode.Reference && validatedRevision != world.Revision)
            {
                WorldFaceManifest reference = WorldFaceManifestBuilder.Capture(world, FlagManager.flags.game!, FlagManager.flags.seed!.Value, FaceGenerationMode.Reference);
                WorldFaceManifest optimized = WorldFaceManifestBuilder.Capture(world, FlagManager.flags.game!, FlagManager.flags.seed!.Value, FaceGenerationMode.Optimized);
                if (!string.Equals(reference.Faces.Sha256, optimized.Faces.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Native streaming faces differ from the reference authority.");
                validatedRevision = world.Revision;
            }

            long uploadsBeforeFrame = uploadCount;
            var chunks = new List<NativeChunkRenderPacketDescriptor>();
            var currentRenderData = new HashSet<long>();
            world.InspectRenderPackets((in NativeChunkRenderPacketDescriptor descriptor, ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent) =>
            {
                chunks.Add(descriptor);
                currentRenderData.Add(descriptor.RenderDataId);
                EnsureUploadQueued(frameIndex, in descriptor, opaque, transparent);
            });
            foreach (long renderDataId in activeRenderData)
            {
                if (currentRenderData.Contains(renderDataId))
                    continue;
                if (!uploadedRenderData.TryGetValue(renderDataId, out ChunkIdentity chunk))
                    continue;
                QueueRecord(new DeletionRecord(frameIndex, renderDataId, chunk));
                uploadedRenderData.Remove(renderDataId);
                deletionCount++;
            }

            activeRenderData = currentRenderData;
            QueueRecord(new RenderFrameRecord(frameIndex, simulationElapsedSeconds, wallElapsedSeconds, deltaSeconds, input, CaptureCamera(), chunks.Count, uploadCount - uploadsBeforeFrame, CaptureDrawList(chunks, transparent: false), CaptureDrawList(chunks, transparent: true)));
            frameCount++;
            return new SimulatedRenderFrameState
            {
                FrameIndex = frameIndex,
                OpaquePassChunks = chunks,
                TransparentPassChunks = chunks
            };
        }

        public void WriteSnapshot(string name, double simulationElapsedSeconds, SimulatedRenderFrameState frame)
        {
            ActiveChunkCapture[] chunks = frame.TransparentPassChunks.Select(chunk => new ActiveChunkCapture(CaptureChunkIdentity(chunk), chunk.RenderDataId, false)).ToArray();
            QueueRecord(new SnapshotRecord(snapshotCount, name, frame.FrameIndex, simulationElapsedSeconds, CaptureCamera(), chunks));
            snapshotCount++;
        }

        public void WriteInputBoundary(string type, int stepIndex, TimedPlayerInputStep step, double simulationElapsedSeconds)
        {
            QueueRecord(new InputBoundaryRecord(type, stepIndex, step, simulationElapsedSeconds, CaptureCamera()));
        }

        public async Task CompleteAsync(double simulationElapsedSeconds, double wallElapsedSeconds)
        {
            if (completionQueued)
                throw new InvalidOperationException("The simulated GPU output is already complete.");
            QueueRecord(new CompletionRecord(simulationElapsedSeconds, wallElapsedSeconds, frameCount, uploadCount, deletionCount, snapshotCount));
            completionQueued = true;
            records.Writer.TryComplete();
            try
            {
                await writerTask.ConfigureAwait(false);
                ThrowIfWriterFailed();
                if (writtenRecordCount != nextSequence)
                {
                    throw new InvalidDataException($"The simulated GPU writer recorded {writtenRecordCount} of {nextSequence} queued records.");
                }

                PublishFinalOutput();
            }
            finally
            {
                if (!finalOutputPublished)
                {
                    try
                    {
                        DisposeOutputResources();
                    }
                    finally
                    {
                        DeleteTemporaryOutput();
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            disposed = true;
            Exception? disposalFailure = null;
            try
            {
                if (!completionQueued)
                {
                    records.Writer.TryComplete();
                    try
                    {
                        await writerTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        disposalFailure = ex;
                    }
                }
            }
            finally
            {
                writerFailureCancellation.Cancel();
                records.Writer.TryComplete();
                try
                {
                    try
                    {
                        DisposeOutputResources();
                    }
                    catch (Exception ex)when (disposalFailure is not null)
                    {
                        disposalFailure = new AggregateException(disposalFailure, ex);
                    }
                    catch (Exception ex)
                    {
                        disposalFailure = ex;
                    }
                    finally
                    {
                        if (!finalOutputPublished)
                            DeleteTemporaryOutput();
                    }
                }
                finally
                {
                    writerFailureCancellation.Dispose();
                    retainedRecordSlots.Dispose();
                }
            }

            if (disposalFailure is not null)
                ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }

        private void WriteRecords()
        {
            try
            {
                while (records.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (records.Reader.TryRead(out QueuedRecord? queued))
                    {
                        try
                        {
                            if (writerDelayMilliseconds > 0)
                                Thread.Sleep(writerDelayMilliseconds);
                            if (writerFailAfterRecords.HasValue && writtenRecordCount >= writerFailAfterRecords.Value)
                            {
                                throw new IOException($"The simulated GPU writer failure was requested after {writerFailAfterRecords.Value} records.");
                            }

                            switch (queued.Record)
                            {
                                case UploadRecord upload:
                                    WriteUploadRecord(upload, queued.Sequence);
                                    break;
                                case DeletionRecord deletion:
                                    WriteDeletionRecord(deletion, queued.Sequence);
                                    break;
                                case RenderFrameRecord frame:
                                    WriteRenderFrameRecord(frame, queued.Sequence);
                                    break;
                                case SnapshotRecord snapshot:
                                    WriteSnapshotRecord(snapshot, queued.Sequence);
                                    break;
                                case InputBoundaryRecord inputBoundary:
                                    WriteInputBoundaryRecord(inputBoundary, queued.Sequence);
                                    break;
                                case CompletionRecord completion:
                                    WriteCompletionRecord(completion, queued.Sequence);
                                    break;
                                default:
                                    throw new InvalidOperationException("The simulated GPU stream record is not valid.");
                            }

                            writer.Flush();
                            writtenRecordCount++;
                        }
                        finally
                        {
                            ReleaseRecordRetention(queued);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref writerFailure, ExceptionDispatchInfo.Capture(ex), null);
                writerFailureCancellation.Cancel();
                records.Writer.TryComplete(ex);
                while (records.Reader.TryRead(out QueuedRecord? abandoned))
                    ReleaseRecordRetention(abandoned);
                throw;
            }
        }

        private void EnsureUploadQueued(long frameIndex, in NativeChunkRenderPacketDescriptor data, ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> transparent)
        {
            if (uploadedRenderData.ContainsKey(data.RenderDataId))
                return;
            if (PackedFaceRectangle.CountLogicalFaces(opaque) != data.OpaqueFaceCount || PackedFaceRectangle.CountLogicalFaces(transparent) != data.TransparentFaceCount)
                throw new InvalidDataException("Native upload rectangle counts are inconsistent.");
            ChunkIdentity identity = CaptureChunkIdentity(data);
            var buffer = new ArrayBufferWriter<byte>();
            using (var capture = new Utf8JsonWriter(buffer))
            {
                capture.WriteStartObject();
                capture.WriteString("type", "simulatedGpuUpload");
                capture.WriteNumber("sequence", nextSequence);
                capture.WriteNumber("frameIndex", frameIndex);
                capture.WriteNumber("renderDataId", data.RenderDataId);
                capture.WriteBoolean("actualGpuUploadPerformed", false);
                capture.WriteString("faceGenerationMode", "Optimized");
                capture.WriteStartObject("chunkIndex");
                capture.WriteNumber("x", identity.ChunkX);
                capture.WriteNumber("y", identity.ChunkY);
                capture.WriteNumber("z", identity.ChunkZ);
                capture.WriteEndObject();
                WriteVector(capture, "worldOrigin", data.ChunkWorldX, data.ChunkWorldY, data.ChunkWorldZ);
                WriteVector(capture, "shaderChunkPosition", data.ChunkWorldX + 1, data.ChunkWorldY + 1, data.ChunkWorldZ + 1);
                capture.WriteBoolean("fullyOccluded", data.IsEmpty);
                capture.WriteNumber("opaqueFaceCount", data.OpaqueFaceCount);
                capture.WriteNumber("opaqueRectangleCount", data.OpaqueRectangleCount);
                capture.WriteNumber("transparentFaceCount", data.TransparentFaceCount);
                capture.WriteNumber("transparentRectangleCount", data.TransparentRectangleCount);
                WriteNativeFaces(capture, "opaqueFaces", in data, opaque, false);
                WriteNativeFaces(capture, "transparentFaces", in data, transparent, true);
                capture.WriteEndObject();
                capture.Flush();
            }

            // Only diagnostic JSON leaves this callback. Mesh words remain in
            // the borrowed native packet, and never enter a second mesh owner.
            QueueRecord(new UploadRecord(nextSequence, buffer.WrittenSpan.ToArray()));
            uploadedRenderData.Add(data.RenderDataId, identity);
            uploadCount++;
        }

        private void WriteNativeFaces(Utf8JsonWriter capture, string name, in NativeChunkRenderPacketDescriptor data, ReadOnlySpan<uint> words, bool transparent)
        {
            capture.WriteStartArray(name);
            var reader = new PackedFaceRectangleReader(words);
            while (reader.MoveNext())
            {
                int x = checked(data.ChunkWorldX + reader.X);
                int y = checked(data.ChunkWorldY + reader.Y);
                int z = checked(data.ChunkWorldZ + reader.Z);
                (int dx, int dy, int dz) = GetFaceNormal(reader.Direction);
                ushort source = world.GetBlock(x, y, z);
                ushort neighbor = world.GetBlock(x + dx, y + dy, z + dz);
                capture.WriteStartObject();
                capture.WriteString("renderPass", transparent ? "transparent" : "opaque");
                WriteVector(capture, "offset", reader.X, reader.Y, reader.Z);
                capture.WriteNumber("tileIndex", reader.TileIndex);
                capture.WriteNumber("faceDirection", reader.Direction);
                capture.WriteString("faceName", GetFaceName(reader.Direction));
                WriteVector(capture, "voxelWorld", x, y, z);
                capture.WriteNumber("blockId", source);
                WriteBlockName(capture, "blockName", source);
                WriteVector(capture, "neighborWorldAtUpload", x + dx, y + dy, z + dz);
                capture.WriteNumber("neighborBlockIdAtUpload", neighbor);
                WriteBlockName(capture, "neighborBlockNameAtUpload", neighbor);
                capture.WriteEndObject();
            }

            capture.WriteEndArray();
        }

        private static void WriteVector(Utf8JsonWriter capture, string name, int x, int y, int z)
        {
            capture.WriteStartArray(name);
            capture.WriteNumberValue(x);
            capture.WriteNumberValue(y);
            capture.WriteNumberValue(z);
            capture.WriteEndArray();
        }

        private static void WriteBlockName(Utf8JsonWriter capture, string name, ushort id)
        {
            if (TerrainLoader.allBlockTypesByIds.TryGetValue(id, out string? blockName))
                capture.WriteString(name, blockName);
            else
                capture.WriteNull(name);
        }

        private void QueueRecord(StreamRecord record)
        {
            if (completionQueued)
                throw new InvalidOperationException("The simulated GPU output stream is closed.");
            ThrowIfWriterFailed();
            try
            {
                retainedRecordSlots.Wait(writerFailureCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                ThrowIfWriterFailed();
                throw new InvalidOperationException("The simulated GPU output stream is closed.");
            }

            long retainedBytes = EstimateRetainedPayloadBytes(record);
            var queued = new QueuedRecord(Interlocked.Increment(ref nextSequence) - 1, retainedBytes, record);
            int currentCount = Interlocked.Increment(ref retainedRecordCount);
            long currentBytes = Interlocked.Add(ref retainedPayloadBytes, retainedBytes);
            UpdateMaximum(ref peakRetainedRecordCount, currentCount);
            UpdateMaximum(ref peakRetainedPayloadBytes, currentBytes);
            bool retained = true;
            try
            {
                ThrowIfWriterFailed();
                if (!records.Writer.TryWrite(queued))
                {
                    ThrowIfWriterFailed();
                    throw new InvalidOperationException("The simulated GPU output stream is closed.");
                }

                retained = false;
            }
            finally
            {
                if (retained)
                    ReleaseRecordRetention(queued);
            }
        }

        private void ReleaseRecordRetention(QueuedRecord queued)
        {
            try
            {
            // Diagnostic records contain no native lease or mesh retention.
            }
            finally
            {
                Interlocked.Add(ref retainedPayloadBytes, -queued.RetainedPayloadBytes);
                Interlocked.Decrement(ref retainedRecordCount);
                retainedRecordSlots.Release();
            }
        }

        private void ThrowIfWriterFailed()
        {
            ExceptionDispatchInfo? failure = Volatile.Read(ref writerFailure);
            if (failure is not null)
                throw new InvalidOperationException("The simulated GPU output writer failed.", failure.SourceException);
        }

        private CameraCapture CaptureCamera()
        {
            (int cx, int cy, int cz) = world.PlayerChunkPosition;
            return new CameraCapture(player.camera.position, player.camera.front, player.camera.up, Matrix4.Identity, player.camera.GetViewMatrix(), Camera.GetProjectionMatrix((float)windowWidth / windowHeight), cx, cy, cz);
        }

        private static ChunkIdentity CaptureChunkIdentity(NativeChunkRenderPacketDescriptor data) => new(data.ChunkWorldX / GameManager.settings.chunkMaxX, data.ChunkWorldY / GameManager.settings.chunkMaxY, data.ChunkWorldZ / GameManager.settings.chunkMaxZ, data.ChunkWorldX, data.ChunkWorldY, data.ChunkWorldZ);
        private static long[] CaptureDrawList(IReadOnlyList<NativeChunkRenderPacketDescriptor> chunks, bool transparent)
        {
            var ids = new List<long>();
            foreach (NativeChunkRenderPacketDescriptor data in chunks)
                if ((transparent ? data.TransparentFaceCount : data.OpaqueFaceCount) > 0)
                    ids.Add(data.RenderDataId);
            return ids.ToArray();
        }

        private void WriteSessionHeader(string inputScript, int frameRate, BlockTextureAtlas textureAtlas)
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("mode", "simulatedGpuUpload");
            writer.WriteString("createdUtc", DateTimeOffset.UtcNow);
            writer.WriteBoolean("windowCreated", false);
            writer.WriteBoolean("openGlCallsAllowed", false);
            writer.WriteNumber("actualGpuUploadCount", 0);
            writer.WriteString("game", FlagManager.flags.game);
            writer.WriteNumber("seed", FlagManager.flags.seed!.Value);
            writer.WriteString("faceGenerationMode", "Optimized");
            writer.WriteString("validationMode", (FlagManager.flags.faceGenerationMode ?? FaceGenerationMode.Optimized).ToString());
            writer.WriteString("worldImplementation", "Native");
            writer.WriteNumber("windowConstructionCount", 0);
            writer.WriteString("worldId", world.ID);
            writer.WriteString("regionId", world.RegionID);
            writer.WriteString("inputScript", inputScript);
            writer.WriteNumber("frameRate", frameRate);
            writer.WriteNumber("playerMovementSpeed", Player.MovementSpeed);
            writer.WriteNumber("windowWidth", windowWidth);
            writer.WriteNumber("windowHeight", windowHeight);
            writer.WriteNumber("recordQueueCapacity", RecordQueueCapacity);
            writer.WriteString("recordQueueFullPolicy", "wait");
            writer.WriteBoolean("silentRecordLossAllowed", false);
            writer.WriteBoolean("atomicFinalPublication", true);
            writer.WriteNumber("writerDelayMilliseconds", writerDelayMilliseconds);
            if (writerFailAfterRecords.HasValue)
                writer.WriteNumber("writerFailAfterRecords", writerFailAfterRecords.Value);
            else
                writer.WriteNull("writerFailAfterRecords");
            writer.WriteStartObject("chunkDimensions");
            writer.WriteNumber("x", GameManager.settings.chunkMaxX);
            writer.WriteNumber("y", GameManager.settings.chunkMaxY);
            writer.WriteNumber("z", GameManager.settings.chunkMaxZ);
            writer.WriteEndObject();
            writer.WriteStartObject("textureAtlas");
            writer.WriteNumber("width", textureAtlas.atlasWidth);
            writer.WriteNumber("height", textureAtlas.atlasHeight);
            writer.WriteNumber("tilesX", textureAtlas.tilesX);
            writer.WriteNumber("tilesY", textureAtlas.tilesY);
            writer.WriteEndObject();
            WriteUploadGeometry();
            writer.WriteStartArray("events");
            writer.Flush();
        }

        private void WriteUploadRecord(UploadRecord record, long sequence)
        {
            if (record.Sequence != sequence)
                throw new InvalidDataException("Native diagnostic upload sequence changed.");
            writer.WriteRawValue(record.Payload, skipInputValidation: true);
        }

        private void WriteDeletionRecord(DeletionRecord record, long sequence)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "simulatedGpuDeletion");
            writer.WriteNumber("sequence", sequence);
            writer.WriteNumber("frameIndex", record.FrameIndex);
            writer.WriteNumber("renderDataId", record.RenderDataId);
            writer.WriteBoolean("actualOpenGlDeletionPerformed", false);
            WriteChunkIndex(record.Chunk);
            writer.WriteEndObject();
        }

        private void WriteRenderFrameRecord(RenderFrameRecord record, long sequence)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "renderFrame");
            writer.WriteNumber("sequence", sequence);
            writer.WriteNumber("frameIndex", record.FrameIndex);
            writer.WriteNumber("simulationElapsedSeconds", record.SimulationElapsedSeconds);
            writer.WriteNumber("wallElapsedSeconds", record.WallElapsedSeconds);
            writer.WriteNumber("deltaSeconds", record.DeltaSeconds);
            WriteInputKeys(record.Input);
            WriteCamera(record.Camera);
            WritePlayerChunk(record.Camera);
            writer.WriteNumber("activeChunkCount", record.ActiveChunkCount);
            writer.WriteNumber("simulatedGpuUploadsThisFrame", record.UploadsThisFrame);
            writer.WriteNumber("actualGpuUploadsThisFrame", 0);
            WriteLongArray("opaqueDrawRenderDataIds", record.OpaqueDrawRenderDataIds);
            WriteLongArray("transparentDrawRenderDataIds", record.TransparentDrawRenderDataIds);
            writer.WriteEndObject();
        }

        private void WriteSnapshotRecord(SnapshotRecord record, long sequence)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "snapshot");
            writer.WriteNumber("sequence", sequence);
            writer.WriteNumber("snapshotIndex", record.SnapshotIndex);
            writer.WriteString("name", record.Name);
            writer.WriteNumber("frameIndex", record.FrameIndex);
            writer.WriteNumber("simulationElapsedSeconds", record.SimulationElapsedSeconds);
            WriteCamera(record.Camera);
            WritePlayerChunk(record.Camera);
            writer.WriteStartArray("activeChunks");
            foreach (ActiveChunkCapture chunk in record.ActiveChunks)
            {
                writer.WriteStartObject();
                WriteChunkIndex(chunk.Chunk);
                WriteVector("worldOrigin", chunk.Chunk.WorldOriginX, chunk.Chunk.WorldOriginY, chunk.Chunk.WorldOriginZ);
                if (chunk.RenderDataId.HasValue)
                    writer.WriteNumber("renderDataId", chunk.RenderDataId.Value);
                else
                    writer.WriteNull("renderDataId");
                writer.WriteBoolean("openGlUploaded", chunk.OpenGlUploaded);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private void WriteInputBoundaryRecord(InputBoundaryRecord record, long sequence)
        {
            writer.WriteStartObject();
            writer.WriteString("type", record.Type);
            writer.WriteNumber("sequence", sequence);
            writer.WriteNumber("stepIndex", record.StepIndex);
            WriteInputKeys(record.Step.Keys);
            writer.WriteNumber("durationSeconds", record.Step.DurationSeconds);
            writer.WriteNumber("simulationElapsedSeconds", record.SimulationElapsedSeconds);
            WriteCamera(record.Camera);
            WritePlayerChunk(record.Camera);
            writer.WriteEndObject();
        }

        private void WriteCompletionRecord(CompletionRecord record, long sequence)
        {
            writer.WriteEndArray();
            writer.WriteStartObject("summary");
            writer.WriteNumber("completionSequence", sequence);
            writer.WriteNumber("streamRecordCount", sequence + 1);
            writer.WriteNumber("recordQueueCapacity", RecordQueueCapacity);
            writer.WriteNumber("peakRetainedRecordCount", Volatile.Read(ref peakRetainedRecordCount));
            writer.WriteNumber("peakRetainedRecordPayloadBytes", Volatile.Read(ref peakRetainedPayloadBytes));
            writer.WriteBoolean("silentRecordLossAllowed", false);
            writer.WriteNumber("simulationElapsedSeconds", record.SimulationElapsedSeconds);
            writer.WriteNumber("wallElapsedSeconds", record.WallElapsedSeconds);
            writer.WriteNumber("renderFrameCount", record.FrameCount);
            writer.WriteNumber("simulatedGpuUploadCount", record.UploadCount);
            writer.WriteNumber("simulatedGpuDeletionCount", record.DeletionCount);
            writer.WriteNumber("snapshotCount", record.SnapshotCount);
            writer.WriteNumber("actualGpuUploadCount", 0);
            writer.WriteBoolean("windowCreated", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private void WriteCamera(CameraCapture camera)
        {
            writer.WriteStartObject("camera");
            WriteVector("position", camera.Position.X, camera.Position.Y, camera.Position.Z);
            WriteVector("front", camera.Front.X, camera.Front.Y, camera.Front.Z);
            WriteVector("up", camera.Up.X, camera.Up.Y, camera.Up.Z);
            WriteMatrix("model", camera.Model);
            WriteMatrix("view", camera.View);
            WriteMatrix("projection", camera.Projection);
            writer.WriteEndObject();
        }

        private void WritePlayerChunk(CameraCapture camera)
        {
            writer.WriteStartObject("playerChunk");
            writer.WriteNumber("x", camera.PlayerChunkX);
            writer.WriteNumber("y", camera.PlayerChunkY);
            writer.WriteNumber("z", camera.PlayerChunkZ);
            writer.WriteEndObject();
        }

        private void WriteInputKeys(PlayerInputKeys input)
        {
            writer.WriteStartArray("inputKeys");
            foreach (string name in TimedPlayerInputScript.GetKeyNames(input))
                writer.WriteStringValue(name);
            writer.WriteEndArray();
        }

        private void WriteUploadGeometry()
        {
            writer.WriteStartObject("uploadGeometry");
            writer.WriteStartArray("quadPositions");
            foreach (ref readonly byte value in ChunkRender.QuadPositionUploadData.Span)
                writer.WriteNumberValue(value);
            writer.WriteEndArray();
            writer.WriteStartArray("quadIndices");
            foreach (ref readonly ushort value in ChunkRender.QuadIndexUploadData.Span)
                writer.WriteNumberValue(value);
            writer.WriteEndArray();
            WriteVector("shaderChunkPositionAdjustment", 1, 1, 1);
            writer.WriteStartArray("faceDirections");
            WriteFaceDirection(0, "LEFT", -1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0);
            WriteFaceDirection(1, "RIGHT", 1, 0, 0, 1, 0, 1, 0, 0, -1, 0, 1, 0);
            WriteFaceDirection(2, "BOTTOM", 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            WriteFaceDirection(3, "TOP", 0, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, -1);
            WriteFaceDirection(4, "BACK", 0, 0, -1, 1, 0, 0, -1, 0, 0, 0, 1, 0);
            WriteFaceDirection(5, "FRONT", 0, 0, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private void WriteFaceDirection(byte id, string name, int normalX, int normalY, int normalZ, int originX, int originY, int originZ, int uX, int uY, int uZ, int vX, int vY, int vZ)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("name", name);
            WriteVector("normal", normalX, normalY, normalZ);
            WriteVector("shaderLocalOrigin", originX, originY, originZ);
            WriteVector("shaderU", uX, uY, uZ);
            WriteVector("shaderV", vX, vY, vZ);
            writer.WriteEndObject();
        }

        private void WriteChunkIndex(ChunkIdentity chunk)
        {
            writer.WriteStartObject("chunkIndex");
            writer.WriteNumber("x", chunk.ChunkX);
            writer.WriteNumber("y", chunk.ChunkY);
            writer.WriteNumber("z", chunk.ChunkZ);
            writer.WriteEndObject();
        }

        private void WriteLongArray(string propertyName, IEnumerable<long> values)
        {
            writer.WriteStartArray(propertyName);
            foreach (long value in values)
                writer.WriteNumberValue(value);
            writer.WriteEndArray();
        }

        private void WriteMatrix(string propertyName, Matrix4 matrix)
        {
            writer.WriteStartArray(propertyName);
            WriteMatrixRow(matrix.M11, matrix.M12, matrix.M13, matrix.M14);
            WriteMatrixRow(matrix.M21, matrix.M22, matrix.M23, matrix.M24);
            WriteMatrixRow(matrix.M31, matrix.M32, matrix.M33, matrix.M34);
            WriteMatrixRow(matrix.M41, matrix.M42, matrix.M43, matrix.M44);
            writer.WriteEndArray();
        }

        private void WriteMatrixRow(float x, float y, float z, float w)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(x);
            writer.WriteNumberValue(y);
            writer.WriteNumberValue(z);
            writer.WriteNumberValue(w);
            writer.WriteEndArray();
        }

        private void WriteVector(string propertyName, float x, float y, float z)
        {
            writer.WriteStartArray(propertyName);
            writer.WriteNumberValue(x);
            writer.WriteNumberValue(y);
            writer.WriteNumberValue(z);
            writer.WriteEndArray();
        }

        private void WriteBlockName(string propertyName, ushort blockId)
        {
            if (TerrainLoader.allBlockTypesByIds.TryGetValue(blockId, out string? name))
                writer.WriteString(propertyName, name);
            else
                writer.WriteNull(propertyName);
        }

        private void PublishFinalOutput()
        {
            writer.Flush();
            fileStream.Flush(flushToDisk: true);
            DisposeOutputResources();
            File.Move(temporaryOutputPath, finalOutputPath);
            finalOutputPublished = true;
        }

        private void DisposeOutputResources()
        {
            if (outputResourcesDisposed)
                return;
            outputResourcesDisposed = true;
            Exception? failure = null;
            try
            {
                writer.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                fileStream.Dispose();
            }
            catch (Exception ex)when (failure is not null)
            {
                failure = new AggregateException(failure, ex);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void DeleteTemporaryOutput()
        {
            if (File.Exists(temporaryOutputPath))
                File.Delete(temporaryOutputPath);
        }

        private static long EstimateRetainedPayloadBytes(StreamRecord record)
        {
            const long RecordOverheadEstimate = 256;
            return record switch
            {
                UploadRecord upload => checked(RecordOverheadEstimate + upload.Payload.Length),
                RenderFrameRecord frame => checked(RecordOverheadEstimate + frame.OpaqueDrawRenderDataIds.Length * sizeof(long) + frame.TransparentDrawRenderDataIds.Length * sizeof(long)),
                SnapshotRecord snapshot => checked(RecordOverheadEstimate + snapshot.ActiveChunks.Length * 64L),
                _ => RecordOverheadEstimate
            };
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            int observed = Volatile.Read(ref maximum);
            while (candidate > observed)
            {
                int previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }

        private static void UpdateMaximum(ref long maximum, long candidate)
        {
            long observed = Volatile.Read(ref maximum);
            while (candidate > observed)
            {
                long previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }

        private static (int dx, int dy, int dz) GetFaceNormal(byte direction) => direction switch
        {
            0 => (-1, 0, 0),
            1 => (1, 0, 0),
            2 => (0, -1, 0),
            3 => (0, 1, 0),
            4 => (0, 0, -1),
            5 => (0, 0, 1),
            _ => throw new InvalidDataException($"Face direction {direction} is invalid.")};
        private static string GetFaceName(byte direction) => direction switch
        {
            0 => "LEFT",
            1 => "RIGHT",
            2 => "BOTTOM",
            3 => "TOP",
            4 => "BACK",
            5 => "FRONT",
            _ => throw new InvalidDataException($"Face direction {direction} is invalid.")};
    }
}

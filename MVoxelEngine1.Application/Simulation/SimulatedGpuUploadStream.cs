using System.Text.Json;
using System.Threading.Channels;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.WorldGeneration;
using OpenTK.Mathematics;

namespace MVoxelEngine1.Application.Simulation
{
    internal sealed class SimulatedRenderFrameState
    {
        public required long FrameIndex { get; init; }

        public required IReadOnlyList<WorldRenderChunk> OpaquePassChunks { get; init; }

        public required IReadOnlyList<WorldRenderChunk> TransparentPassChunks { get; init; }
    }

    internal sealed class SimulatedGpuUploadStream : IAsyncDisposable
    {
        private readonly record struct ChunkIdentity(
            int ChunkX,
            int ChunkY,
            int ChunkZ,
            float WorldOriginX,
            float WorldOriginY,
            float WorldOriginZ);

        private readonly record struct ActiveChunkCapture(
            ChunkIdentity Chunk,
            long? RenderDataId,
            bool OpenGlUploaded);

        private sealed record CameraCapture(
            Vector3 Position,
            Vector3 Front,
            Vector3 Up,
            Matrix4 Model,
            Matrix4 View,
            Matrix4 Projection,
            int PlayerChunkX,
            int PlayerChunkY,
            int PlayerChunkZ);

        private sealed record FaceDiagnostics(
            ushort[] BlockIds,
            ushort[] NeighborBlockIds);

        private abstract record StreamRecord;

        private sealed record UploadRecord(
            long FrameIndex,
            ChunkIdentity Chunk,
            ChunkRenderUploadData Data,
            FaceDiagnostics OpaqueDiagnostics,
            FaceDiagnostics TransparentDiagnostics) : StreamRecord;

        private sealed record DeletionRecord(
            long FrameIndex,
            long RenderDataId,
            ChunkIdentity Chunk) : StreamRecord;

        private sealed record RenderFrameRecord(
            long FrameIndex,
            double SimulationElapsedSeconds,
            double WallElapsedSeconds,
            double DeltaSeconds,
            PlayerInputKeys Input,
            CameraCapture Camera,
            int ActiveChunkCount,
            long UploadsThisFrame,
            long[] OpaqueDrawRenderDataIds,
            long[] TransparentDrawRenderDataIds) : StreamRecord;

        private sealed record SnapshotRecord(
            int SnapshotIndex,
            string Name,
            long FrameIndex,
            double SimulationElapsedSeconds,
            CameraCapture Camera,
            ActiveChunkCapture[] ActiveChunks) : StreamRecord;

        private sealed record InputBoundaryRecord(
            string Type,
            int StepIndex,
            TimedPlayerInputStep Step,
            double SimulationElapsedSeconds,
            CameraCapture Camera) : StreamRecord;

        private sealed record CompletionRecord(
            double SimulationElapsedSeconds,
            double WallElapsedSeconds,
            long FrameCount,
            long UploadCount,
            long DeletionCount,
            int SnapshotCount) : StreamRecord;

        private readonly World world;
        private readonly Player player;
        private readonly int windowWidth;
        private readonly int windowHeight;
        private readonly FileStream fileStream;
        private readonly Utf8JsonWriter writer;
        private readonly Channel<StreamRecord> records;
        private readonly Task writerTask;
        private readonly Dictionary<long, ChunkIdentity> uploadedRenderData = new();
        private HashSet<long> activeRenderData = new();
        private long frameCount;
        private long uploadCount;
        private long deletionCount;
        private int snapshotCount;
        private bool completionQueued;

        public SimulatedGpuUploadStream(
            string outputPath,
            string inputScript,
            int frameRate,
            BlockTextureAtlas textureAtlas,
            World world,
            Player player,
            int windowWidth,
            int windowHeight)
        {
            this.world = world;
            this.player = player;
            this.windowWidth = windowWidth;
            this.windowHeight = windowHeight;

            string fullOutputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(fullOutputPath)
                ?? throw new InvalidOperationException("The simulated GPU output directory is not valid.");
            Directory.CreateDirectory(outputDirectory);

            fileStream = new FileStream(
                fullOutputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                1_048_576,
                FileOptions.SequentialScan);
            writer = new Utf8JsonWriter(fileStream, new JsonWriterOptions { Indented = false });
            records = Channel.CreateUnbounded<StreamRecord>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

            WriteSessionHeader(inputScript, frameRate, textureAtlas);
            writerTask = Task.Factory.StartNew(
                WriteRecords,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public SimulatedRenderFrameState RenderFrame(
            long frameIndex,
            double simulationElapsedSeconds,
            double wallElapsedSeconds,
            double deltaSeconds,
            PlayerInputKeys input)
        {
            IReadOnlyList<WorldRenderChunk> opaqueChunks = world.CaptureActiveRenderChunks();
            long uploadsBeforeFrame = uploadCount;
            var renderDataActiveDuringFrame = new HashSet<long>(activeRenderData);
            foreach (WorldRenderChunk chunk in opaqueChunks)
            {
                EnsureUploadQueued(frameIndex, chunk);
                if (chunk.UploadData is not null)
                    renderDataActiveDuringFrame.Add(chunk.UploadData.RenderDataId);
            }

            IReadOnlyList<WorldRenderChunk> transparentChunks = world.CaptureActiveRenderChunks();
            foreach (WorldRenderChunk chunk in transparentChunks)
                EnsureUploadQueued(frameIndex, chunk);

            var currentRenderData = new HashSet<long>();
            foreach (WorldRenderChunk chunk in transparentChunks)
            {
                if (chunk.UploadData is not null)
                    currentRenderData.Add(chunk.UploadData.RenderDataId);
            }

            foreach (long renderDataId in renderDataActiveDuringFrame)
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
            QueueRecord(new RenderFrameRecord(
                frameIndex,
                simulationElapsedSeconds,
                wallElapsedSeconds,
                deltaSeconds,
                input,
                CaptureCamera(),
                transparentChunks.Count,
                uploadCount - uploadsBeforeFrame,
                CaptureDrawList(opaqueChunks, transparent: false),
                CaptureDrawList(transparentChunks, transparent: true)));
            frameCount++;

            return new SimulatedRenderFrameState
            {
                FrameIndex = frameIndex,
                OpaquePassChunks = opaqueChunks,
                TransparentPassChunks = transparentChunks
            };
        }

        public void WriteSnapshot(
            string name,
            double simulationElapsedSeconds,
            SimulatedRenderFrameState frame)
        {
            ActiveChunkCapture[] chunks = frame.TransparentPassChunks
                .Select(chunk => new ActiveChunkCapture(
                    CaptureChunkIdentity(chunk),
                    chunk.UploadData?.RenderDataId,
                    chunk.IsOpenGlUploaded))
                .ToArray();
            QueueRecord(new SnapshotRecord(
                snapshotCount,
                name,
                frame.FrameIndex,
                simulationElapsedSeconds,
                CaptureCamera(),
                chunks));
            snapshotCount++;
        }

        public void WriteInputBoundary(
            string type,
            int stepIndex,
            TimedPlayerInputStep step,
            double simulationElapsedSeconds)
        {
            QueueRecord(new InputBoundaryRecord(
                type,
                stepIndex,
                step,
                simulationElapsedSeconds,
                CaptureCamera()));
        }

        public async Task CompleteAsync(double simulationElapsedSeconds, double wallElapsedSeconds)
        {
            if (completionQueued)
                throw new InvalidOperationException("The simulated GPU output is already complete.");

            QueueRecord(new CompletionRecord(
                simulationElapsedSeconds,
                wallElapsedSeconds,
                frameCount,
                uploadCount,
                deletionCount,
                snapshotCount));
            completionQueued = true;
            records.Writer.Complete();
            await writerTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (!completionQueued)
            {
                records.Writer.TryComplete();
                await writerTask;
            }

            writer.Dispose();
            fileStream.Dispose();
        }

        private void WriteRecords()
        {
            while (records.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (records.Reader.TryRead(out StreamRecord? record))
                {
                    switch (record)
                    {
                        case UploadRecord upload:
                            WriteUploadRecord(upload);
                            break;
                        case DeletionRecord deletion:
                            WriteDeletionRecord(deletion);
                            break;
                        case RenderFrameRecord frame:
                            WriteRenderFrameRecord(frame);
                            break;
                        case SnapshotRecord snapshot:
                            WriteSnapshotRecord(snapshot);
                            break;
                        case InputBoundaryRecord inputBoundary:
                            WriteInputBoundaryRecord(inputBoundary);
                            break;
                        case CompletionRecord completion:
                            WriteCompletionRecord(completion);
                            break;
                        default:
                            throw new InvalidOperationException("The simulated GPU stream record is not valid.");
                    }

                    writer.Flush();
                }
            }
        }

        private void EnsureUploadQueued(long frameIndex, WorldRenderChunk chunk)
        {
            ChunkRenderUploadData? data = chunk.UploadData;
            if (data is null || uploadedRenderData.ContainsKey(data.RenderDataId))
                return;
            if (chunk.IsOpenGlUploaded)
                throw new InvalidOperationException("Headless render data was uploaded through OpenGL.");

            ValidateUploadData(data);
            ChunkIdentity identity = CaptureChunkIdentity(chunk);
            var record = new UploadRecord(
                frameIndex,
                identity,
                data,
                CaptureFaceDiagnostics(data, chunk, transparent: false),
                CaptureFaceDiagnostics(data, chunk, transparent: true));
            uploadedRenderData.Add(data.RenderDataId, identity);
            uploadCount++;
            QueueRecord(record);
        }

        private FaceDiagnostics CaptureFaceDiagnostics(
            ChunkRenderUploadData data,
            WorldRenderChunk chunk,
            bool transparent)
        {
            int count = transparent ? data.TransparentFaceCount : data.OpaqueFaceCount;
            ReadOnlySpan<byte> offsets = transparent
                ? data.TransparentOffsets.Span
                : data.OpaqueOffsets.Span;
            ReadOnlySpan<byte> directions = transparent
                ? data.TransparentFaceDirections.Span
                : data.OpaqueFaceDirections.Span;
            var blockIds = new ushort[count];
            var neighborBlockIds = new ushort[count];
            int originX = checked((int)data.ChunkWorldX);
            int originY = checked((int)data.ChunkWorldY);
            int originZ = checked((int)data.ChunkWorldZ);
            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;

            for (int index = 0; index < count; index++)
            {
                int localX = offsets[index * 3];
                int localY = offsets[index * 3 + 1];
                int localZ = offsets[index * 3 + 2];
                (int dx, int dy, int dz) = GetFaceNormal(directions[index]);
                int neighborX = localX + dx;
                int neighborY = localY + dy;
                int neighborZ = localZ + dz;
                blockIds[index] = chunk.GetBlockLocal(localX, localY, localZ);
                neighborBlockIds[index] =
                    neighborX >= 0 && neighborX < maxX &&
                    neighborY >= 0 && neighborY < maxY &&
                    neighborZ >= 0 && neighborZ < maxZ
                        ? chunk.GetBlockLocal(neighborX, neighborY, neighborZ)
                        : world.GetBlock(
                            originX + neighborX,
                            originY + neighborY,
                            originZ + neighborZ);
            }

            return new FaceDiagnostics(blockIds, neighborBlockIds);
        }

        private void QueueRecord(StreamRecord record)
        {
            if (completionQueued || !records.Writer.TryWrite(record))
                throw new InvalidOperationException("The simulated GPU output stream is closed.");
            if (writerTask.IsFaulted)
                throw new InvalidOperationException("The simulated GPU output writer failed.", writerTask.Exception);
        }

        private CameraCapture CaptureCamera()
        {
            (int cx, int cy, int cz) = world.PlayerChunkPosition;
            return new CameraCapture(
                player.camera.position,
                player.camera.front,
                player.camera.up,
                Matrix4.Identity,
                player.camera.GetViewMatrix(),
                player.camera.GetProjectionMatrix((float)windowWidth / windowHeight),
                cx,
                cy,
                cz);
        }

        private static ChunkIdentity CaptureChunkIdentity(WorldRenderChunk chunk) => new(
            chunk.ChunkX,
            chunk.ChunkY,
            chunk.ChunkZ,
            chunk.WorldOriginX,
            chunk.WorldOriginY,
            chunk.WorldOriginZ);

        private static long[] CaptureDrawList(
            IReadOnlyList<WorldRenderChunk> chunks,
            bool transparent)
        {
            var renderDataIds = new List<long>(chunks.Count);
            foreach (WorldRenderChunk chunk in chunks)
            {
                ChunkRenderUploadData? data = chunk.UploadData;
                if (data is null || data.FullyOccluded)
                    continue;

                int faceCount = transparent ? data.TransparentFaceCount : data.OpaqueFaceCount;
                if (faceCount > 0)
                    renderDataIds.Add(data.RenderDataId);
            }

            return renderDataIds.ToArray();
        }

        private void WriteSessionHeader(
            string inputScript,
            int frameRate,
            BlockTextureAtlas textureAtlas)
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("mode", "simulatedGpuUpload");
            writer.WriteString("createdUtc", DateTimeOffset.UtcNow);
            writer.WriteBoolean("windowCreated", false);
            writer.WriteBoolean("openGlCallsAllowed", false);
            writer.WriteNumber("actualGpuUploadCount", 0);
            writer.WriteString("game", FlagManager.flags.game);
            writer.WriteNumber("seed", FlagManager.flags.seed!.Value);
            writer.WriteString("worldId", world.ID);
            writer.WriteString("regionId", world.RegionID);
            writer.WriteString("inputScript", inputScript);
            writer.WriteNumber("frameRate", frameRate);
            writer.WriteNumber("playerMovementSpeed", Player.MovementSpeed);
            writer.WriteNumber("windowWidth", windowWidth);
            writer.WriteNumber("windowHeight", windowHeight);

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

        private void WriteUploadRecord(UploadRecord record)
        {
            ChunkRenderUploadData data = record.Data;
            writer.WriteStartObject();
            writer.WriteString("type", "simulatedGpuUpload");
            writer.WriteNumber("frameIndex", record.FrameIndex);
            writer.WriteNumber("renderDataId", data.RenderDataId);
            writer.WriteBoolean("actualGpuUploadPerformed", false);
            WriteChunkIndex(record.Chunk);
            WriteVector("worldOrigin", data.ChunkWorldX, data.ChunkWorldY, data.ChunkWorldZ);
            WriteVector("shaderChunkPosition", data.ChunkWorldX + 1, data.ChunkWorldY + 1, data.ChunkWorldZ + 1);
            writer.WriteBoolean("fullyOccluded", data.FullyOccluded);
            writer.WriteNumber("opaqueFaceCount", data.OpaqueFaceCount);
            writer.WriteNumber("transparentFaceCount", data.TransparentFaceCount);
            WriteFaces("opaqueFaces", data, record.OpaqueDiagnostics, transparent: false);
            WriteFaces("transparentFaces", data, record.TransparentDiagnostics, transparent: true);
            writer.WriteEndObject();
        }

        private void WriteDeletionRecord(DeletionRecord record)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "simulatedGpuDeletion");
            writer.WriteNumber("frameIndex", record.FrameIndex);
            writer.WriteNumber("renderDataId", record.RenderDataId);
            writer.WriteBoolean("actualOpenGlDeletionPerformed", false);
            WriteChunkIndex(record.Chunk);
            writer.WriteEndObject();
        }

        private void WriteRenderFrameRecord(RenderFrameRecord record)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "renderFrame");
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

        private void WriteSnapshotRecord(SnapshotRecord record)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "snapshot");
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
                WriteVector(
                    "worldOrigin",
                    chunk.Chunk.WorldOriginX,
                    chunk.Chunk.WorldOriginY,
                    chunk.Chunk.WorldOriginZ);
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

        private void WriteInputBoundaryRecord(InputBoundaryRecord record)
        {
            writer.WriteStartObject();
            writer.WriteString("type", record.Type);
            writer.WriteNumber("stepIndex", record.StepIndex);
            WriteInputKeys(record.Step.Keys);
            writer.WriteNumber("durationSeconds", record.Step.DurationSeconds);
            writer.WriteNumber("simulationElapsedSeconds", record.SimulationElapsedSeconds);
            WriteCamera(record.Camera);
            WritePlayerChunk(record.Camera);
            writer.WriteEndObject();
        }

        private void WriteCompletionRecord(CompletionRecord record)
        {
            writer.WriteEndArray();
            writer.WriteStartObject("summary");
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

        private void WriteFaces(
            string propertyName,
            ChunkRenderUploadData data,
            FaceDiagnostics diagnostics,
            bool transparent)
        {
            int count = transparent ? data.TransparentFaceCount : data.OpaqueFaceCount;
            ReadOnlySpan<byte> offsets = transparent
                ? data.TransparentOffsets.Span
                : data.OpaqueOffsets.Span;
            ReadOnlySpan<uint> tileIndices = transparent
                ? data.TransparentTileIndices.Span
                : data.OpaqueTileIndices.Span;
            ReadOnlySpan<byte> faceDirections = transparent
                ? data.TransparentFaceDirections.Span
                : data.OpaqueFaceDirections.Span;
            int originX = checked((int)data.ChunkWorldX);
            int originY = checked((int)data.ChunkWorldY);
            int originZ = checked((int)data.ChunkWorldZ);

            writer.WriteStartArray(propertyName);
            for (int index = 0; index < count; index++)
            {
                int localX = offsets[index * 3];
                int localY = offsets[index * 3 + 1];
                int localZ = offsets[index * 3 + 2];
                byte direction = faceDirections[index];
                (int dx, int dy, int dz) = GetFaceNormal(direction);
                int worldX = originX + localX;
                int worldY = originY + localY;
                int worldZ = originZ + localZ;
                ushort blockId = diagnostics.BlockIds[index];
                ushort neighborBlockId = diagnostics.NeighborBlockIds[index];

                writer.WriteStartObject();
                WriteVector("offset", localX, localY, localZ);
                writer.WriteNumber("tileIndex", tileIndices[index]);
                writer.WriteNumber("faceDirection", direction);
                writer.WriteString("faceName", GetFaceName(direction));
                WriteVector("voxelWorld", worldX, worldY, worldZ);
                writer.WriteNumber("blockId", blockId);
                WriteBlockName("blockName", blockId);
                WriteVector("neighborWorldAtUpload", worldX + dx, worldY + dy, worldZ + dz);
                writer.WriteNumber("neighborBlockIdAtUpload", neighborBlockId);
                WriteBlockName("neighborBlockNameAtUpload", neighborBlockId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
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
            foreach (byte value in ChunkRender.QuadPositionUploadData.Span)
                writer.WriteNumberValue(value);
            writer.WriteEndArray();
            writer.WriteStartArray("quadIndices");
            foreach (ushort value in ChunkRender.QuadIndexUploadData.Span)
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

        private void WriteFaceDirection(
            byte id,
            string name,
            int normalX,
            int normalY,
            int normalZ,
            int originX,
            int originY,
            int originZ,
            int uX,
            int uY,
            int uZ,
            int vX,
            int vY,
            int vZ)
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

        private static void ValidateUploadData(ChunkRenderUploadData data)
        {
            if (data.OpaqueOffsets.Length != data.OpaqueFaceCount * 3 ||
                data.OpaqueTileIndices.Length != data.OpaqueFaceCount ||
                data.OpaqueFaceDirections.Length != data.OpaqueFaceCount)
            {
                throw new InvalidDataException($"Opaque render data {data.RenderDataId} has inconsistent buffer lengths.");
            }

            if (data.TransparentOffsets.Length != data.TransparentFaceCount * 3 ||
                data.TransparentTileIndices.Length != data.TransparentFaceCount ||
                data.TransparentFaceDirections.Length != data.TransparentFaceCount)
            {
                throw new InvalidDataException($"Transparent render data {data.RenderDataId} has inconsistent buffer lengths.");
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
            _ => throw new InvalidDataException($"Face direction {direction} is invalid.")
        };

        private static string GetFaceName(byte direction) => direction switch
        {
            0 => "LEFT",
            1 => "RIGHT",
            2 => "BOTTOM",
            3 => "TOP",
            4 => "BACK",
            5 => "FRONT",
            _ => throw new InvalidDataException($"Face direction {direction} is invalid.")
        };
    }
}

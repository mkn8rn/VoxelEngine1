using MVoxelEngine1.Graphics;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Graphics.Terrain;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.WorldGeneration.Terrain;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace MVoxelEngine1.WorldGeneration
{
    public partial class World : IDisposable
    {
        public Guid ID { get; private set; }
        public Guid RegionID { get; private set; }
        public WorldLoader loader { get; private set; }

        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> activeChunks = new(); // track ready to render chunks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> unbuiltChunks = new(); // track generated but not yet built chunks
        // passive chunks (generated but outside LoD1, e.g. +1 ring) kept resident but not scheduled for mesh build.
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> passiveChunks = new();

        // Each value identifies the newest neighbor change that requires a rebuild.
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), long> dirtyChunks = new();
        private long nextDirtyRevision;

        // Track chunks that have been cancelled (scheduled then later deemed too far before gen/build)
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> cancelledChunks = new();

        // Cancellation token sources per pipeline (scheduling kept separate so we can recycle gen/build workers)
        private CancellationTokenSource schedulingCts;          // drives scheduling worker lifetime
        private CancellationTokenSource generationCts;          // drives current generation worker set
        private CancellationTokenSource meshBuildCts;           // drives current mesh build worker set

        private readonly FaceGenerationMode faceGenerationMode;
        private readonly ReaderWriterLockSlim renderStateLock =
            new(LockRecursionPolicy.SupportsRecursion);

        private sealed class RenderStateScope : IDisposable
        {
            private ReaderWriterLockSlim? stateLock;
            private readonly bool write;

            public RenderStateScope(
                ReaderWriterLockSlim stateLock,
                bool write)
            {
                this.stateLock = stateLock;
                this.write = write;
            }

            public void Dispose()
            {
                ReaderWriterLockSlim? currentLock = Interlocked.Exchange(
                    ref stateLock,
                    null);
                if (currentLock is null)
                    return;

                if (write)
                    currentLock.ExitWriteLock();
                else
                    currentLock.ExitReadLock();
            }
        }

        // Asynchronous scheduling pipeline
        private int chunkScheduleWorkerCount = 1; 
        private Task[] schedulingWorkers;

        // Asynchronous generation pipeline (current active workers)
        private int generationWorkerCount; // current (may change after staged init)
        private Task[] generationWorkers;
        private BlockingCollection<Vector3> chunkPositionQueue; // gen tasks (LoD1 + active rings)
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> chunkGenSchedule = new(); // track enqueued but not yet generated
        private readonly InitialGenerationCompletionGate initialGenerationCompletion = new();

        // Buffer (pre-generation) queue: chunks beyond LoD1 up to buffer distance; saved then released
        private BlockingCollection<Vector3> bufferChunkPositionQueue; // buffer gen tasks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> bufferGenSchedule = new();

        // Asynchronous mesh build pipeline
        private int meshBuildWorkerCount; // current (may change after staged init)
        private Task[] meshBuildWorkers;
        private BlockingCollection<(int cx,int cy,int cz)> meshBuildQueue; // build tasks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> meshBuildSchedule = new(); // track chunks scheduled for build

        // Player current chunk position (external systems can set this). For now not wired to real player.
        private volatile int playerChunkX;
        private volatile int playerChunkY;
        private volatile int playerChunkZ;

        // Tracks the currently permitted buffer radius
        private int currentBufferRadius; 

        // current position of player in chunk coords
        public (int cx, int cy, int cz) PlayerChunkPosition
        {
            get => (playerChunkX, playerChunkY, playerChunkZ);
            set { playerChunkX = value.cx; playerChunkY = value.cy; playerChunkZ = value.cz; }
        }

        // ---------------- Intra-quad parallel generation state ----------------
        private sealed class BatchGenerationState
        {
            public readonly ConcurrentQueue<(int cx,int cz)> Columns = new();
            public readonly ConcurrentQueue<(int cx, int cy, int cz)> RegisteredChunks = new();
            public int RemainingColumns; // decremented per column processed (approximate; may over-decrement if duplicates skipped)
            public int ActiveWorkers; // number of workers currently draining queue
            public volatile bool Initialized; // set true once columns seeded
        }


        // ---------------- QUAD STORAGE (16x16 chunk column groups) ----------------
        // A quad groups chunks for all vertical layers sharing a 16x16 (cx,cz) footprint.
        // When any chunk in a quad is requested (load or generation), the whole quad
        // is loaded (from quad file if present) or generated on-demand over time.
        private readonly ConcurrentDictionary<(int bx, int bz), Quadrant> loadedBatches = new();

        // Track quads currently being generated. Value holds queue/state instead of a simple byte now.
        private readonly ConcurrentDictionary<(int bx,int bz), BatchGenerationState> generatingBatches = new();

        public World()
        {
            Console.WriteLine("World manager initializing.");

            float proc = Environment.ProcessorCount;
            // Final (steady-state) worker counts must be present
            if (FlagManager.flags.worldGenWorkersPerCore is null)
                throw new InvalidOperationException("World generation workers per core flag is not set.");
            if (FlagManager.flags.meshRenderWorkersPerCore is null)
                throw new InvalidOperationException("Mesh render workers per core flag is not set.");
            // Initial counts may be null (default to final)
            if (FlagManager.flags.worldGenWorkersPerCoreInitial is null)
                Console.WriteLine("Warning: worldGenWorkersPerCoreInitial flag is not set or invalid. Defaulting to worldGenWorkersPerCore");
            if (FlagManager.flags.meshRenderWorkersPerCoreInitial is null)
                Console.WriteLine("Warning: meshRenderWorkersPerCoreInitial flag is not set or invalid. Defaulting to worldGenWorkersPerCore");

            loader = new WorldLoader();
            loader.ChooseWorld(FlagManager.flags.worldName, FlagManager.flags.seed);
            ID = loader.ID;
            RegionID = loader.RegionID;
            Console.WriteLine("World data loaded.");

            faceGenerationMode = FlagManager.flags.faceGenerationMode ?? FaceGenerationMode.Optimized;
            Console.WriteLine($"Face generation mode: {faceGenerationMode}.");
            chunkPositionQueue = new BlockingCollection<Vector3>(new ConcurrentQueue<Vector3>());
            bufferChunkPositionQueue = new BlockingCollection<Vector3>(new ConcurrentQueue<Vector3>());
            meshBuildQueue = new BlockingCollection<(int cx, int cy, int cz)>(new ConcurrentQueue<(int, int, int)>());
            schedulingCts = new CancellationTokenSource();
            Console.WriteLine("World resources initialized.");

            bool streamGeneration = FlagManager.flags.renderStreamingIfAllowed ?? throw new InvalidOperationException("Render streaming flag is not set.");

            Console.WriteLine($"Initializing region: {RegionID}");

            // Establish initial buffer radius BEFORE starting scheduling worker so it does not schedule full runtime buffer prematurely
            currentBufferRadius = GameManager.settings.chunkGenerationBufferInitial; // start with initial pregen horizon

            // Always start scheduling first
            InitializeScheduling();

            if (!streamGeneration)
            {
                // --- Staged non-streaming load ---
                int initialGen = (int)((FlagManager.flags.worldGenWorkersPerCoreInitial ?? FlagManager.flags.worldGenWorkersPerCore!.Value) * proc);
                int initialMesh = (int)((FlagManager.flags.meshRenderWorkersPerCoreInitial ?? FlagManager.flags.meshRenderWorkersPerCore!.Value) * proc);
                int finalGen = (int)(FlagManager.flags.worldGenWorkersPerCore.Value * proc);
                int finalMesh = (int)(FlagManager.flags.meshRenderWorkersPerCore.Value * proc);

                // 1. Initial world generation workers
                StartGenerationWorkers(initialGen);
                EnqueueInitialChunkPositions();
                EnqueueInitialBufferChunkPositions();
                WaitForInitialChunkGeneration();
                StopGenerationWorkers();

                // 2. Build initial LoD1 render data after generation completes.
                BuildInitialChunkRenders(initialMesh);

                // 3. Start steady-state workers (may be same counts; restart for clarity per spec)
                StartGenerationWorkers(finalGen);
                StartMeshBuildWorkers(finalMesh);
                // Promote buffer radius to runtime value and schedule remainder
                currentBufferRadius = GameManager.settings.chunkGenerationBufferRuntime;
                EnqueueRuntimeBufferChunkPositions();
            }
            else
            {
                // Streaming mode: single steady-state startup with final counts.
                int finalGen = (int)(FlagManager.flags.worldGenWorkersPerCore.Value * proc);
                int finalMesh = (int)(FlagManager.flags.meshRenderWorkersPerCore.Value * proc);
                StartGenerationWorkers(finalGen);
                StartMeshBuildWorkers(finalMesh);
                EnqueueInitialChunkPositions();
                // In streaming we immediately switch to runtime buffer horizon
                currentBufferRadius = GameManager.settings.chunkGenerationBufferRuntime;
                EnqueueRuntimeBufferChunkPositions();
            }
        }

        // -------------------- Worker lifecycle helpers --------------------
        private void StartGenerationWorkers(int count)
        {
            if (count <= 0) return;
            generationCts = new CancellationTokenSource();
            generationWorkerCount = count;
            generationWorkers = new Task[generationWorkerCount];
            Console.WriteLine($"[World] Starting {generationWorkerCount} generation workers.");
            for (int i = 0; i < generationWorkerCount; i++)
            {
                generationWorkers[i] = Task.Run(() => ChunkGenerationWorker(generationCts.Token));
            }
        }
        private void StopGenerationWorkers()
        {
            if (generationCts == null) return;
            try
            {
                generationCts.Cancel();
                Task.WaitAll(generationWorkers, TimeSpan.FromSeconds(2));
            }
            catch { }
            finally
            {
                generationCts.Dispose();
                generationCts = null;
                generationWorkers = Array.Empty<Task>();
                Console.WriteLine("[World] Generation workers stopped.");
            }
        }

        private void StartMeshBuildWorkers(int count)
        {
            if (count <= 0) return;
            meshBuildCts = new CancellationTokenSource();
            meshBuildWorkerCount = count;
            meshBuildWorkers = new Task[meshBuildWorkerCount];
            Console.WriteLine($"[World] Starting {meshBuildWorkerCount} mesh build workers.");
            for (int i = 0; i < meshBuildWorkerCount; i++)
            {
                meshBuildWorkers[i] = Task.Run(() => MeshBuildWorker(meshBuildCts.Token));
            }
        }
        private void StopMeshBuildWorkers()
        {
            if (meshBuildCts == null) return;
            try
            {
                meshBuildCts.Cancel();
                Task.WaitAll(meshBuildWorkers, TimeSpan.FromSeconds(2));
            }
            catch { }
            finally
            {
                meshBuildCts.Dispose();
                meshBuildCts = null;
                meshBuildWorkers = Array.Empty<Task>();
                Console.WriteLine("[World] Mesh build workers stopped.");
            }
        }

        // -------------------- Initial load waits --------------------
        private void WaitForInitialChunkGeneration()
        {
            Console.WriteLine("[World] Waiting for initial chunk + buffer generation...");
            StartupPerformanceRecorder.BeginInitialGeneration();

            initialGenerationCompletion.WaitUntilComplete(
                IsInitialGenerationComplete);

            long elapsedMilliseconds = StartupPerformanceRecorder.CompleteInitialGeneration();
            Console.WriteLine($"[World] Initial generation complete in {elapsedMilliseconds} ms. (Generated chunks: {unbuiltChunks.Count + activeChunks.Count})");
        }

        private void RemoveGenerationSchedule(
            ConcurrentDictionary<(int cx, int cy, int cz), byte> schedule,
            (int cx, int cy, int cz) key)
        {
            if (schedule.TryRemove(key, out _) && schedule.IsEmpty)
                initialGenerationCompletion.NotifyCollectionBecameEmpty();
        }

        private bool IsInitialGenerationComplete()
        {
            using IDisposable stateScope = AcquireRenderStateReadScope();
            return chunkGenSchedule.IsEmpty &&
                   bufferGenSchedule.IsEmpty &&
                   generatingBatches.IsEmpty;
        }

        private void RemoveGeneratingBatch((int bx, int bz) key)
        {
            if (generatingBatches.TryRemove(key, out _) && generatingBatches.IsEmpty)
                initialGenerationCompletion.NotifyCollectionBecameEmpty();
        }

        public IDisposable AcquireRenderStateReadScope()
        {
            renderStateLock.EnterReadLock();
            return new RenderStateScope(
                renderStateLock,
                write: false);
        }

        private IDisposable AcquireRenderStateWriteScope()
        {
            renderStateLock.EnterWriteLock();
            return new RenderStateScope(
                renderStateLock,
                write: true);
        }

        private void BuildInitialChunkRenders(int maximumParallelism)
        {
            Console.WriteLine("[World] Building initial chunk meshes in parallel.");
            StartupPerformanceRecorder.BeginInitialChunkMeshBuild();
            (int cx, int cy, int cz)[] targetSet = unbuiltChunks.Keys.ToArray();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maximumParallelism)
            };

            Parallel.ForEach(targetSet, options, key =>
            {
                try
                {
                    if (!unbuiltChunks.TryGetValue(key, out Chunk? chunk))
                        return;

                    ReferenceNeighborBlockPlanes? referenceNeighbors = null;
                    bool neighborsReady = faceGenerationMode == FaceGenerationMode.Reference
                        ? TryCreateReferenceNeighborBlockPlanes(
                            key,
                            out referenceNeighbors)
                        : TryPrepareOptimizedNeighbors(key, chunk);
                    if (!neighborsReady)
                    {
                        throw new InvalidOperationException(
                            $"Required render neighbors are missing for chunk {key}.");
                    }

                    long buildStart = StartupPerformanceRecorder.IsRunning
                        ? Stopwatch.GetTimestamp()
                        : 0;
                    ChunkRender? renderer = chunk.CreateRender(
                        faceGenerationMode,
                        referenceNeighbors);
                    chunk.PublishRender(renderer);
                    if (faceGenerationMode == FaceGenerationMode.Reference)
                        ValidateReferenceRenderData(key, chunk);
                    if (buildStart != 0)
                    {
                        StartupPerformanceRecorder.RecordFirstChunkBuild(
                            Stopwatch.GetElapsedTime(buildStart));
                    }

                    activeChunks[key] = chunk;
                    unbuiltChunks.TryRemove(key, out _);
                    dirtyChunks.TryRemove(key, out _);
                }
                catch (Exception ex)
                {
                    RecordMeshBuildFailure(key, ex);
                    throw;
                }
                finally
                {
                    meshBuildSchedule.TryRemove(key, out _);
                }
            });

            while (meshBuildQueue.TryTake(out _))
            {
            }
            meshBuildSchedule.Clear();
            ThrowIfMeshBuildFailed();
            long elapsedMilliseconds = StartupPerformanceRecorder.CompleteInitialChunkMeshBuild();
            Console.WriteLine(
                $"[World] Chunk mesh build complete in {elapsedMilliseconds} ms. " +
                $"(Built chunks: {targetSet.Length})");
        }

        public void Render(ShaderProgram program)
        {
            ThrowIfMeshBuildFailed();
            // We really should not be handling GL calls here, but for now it's simplest.
            // Maybe I'll make a world render atlas at some point

            RenderCurrentChunks(program);
        }

        private void RenderCurrentChunks(ShaderProgram program)
        {
            using IDisposable stateScope = AcquireRenderStateReadScope();
            ChunkRender[] currentRenderers = activeChunks
                .Where(pair => !dirtyChunks.ContainsKey(pair.Key))
                .Select(pair => pair.Value.chunkRender)
                .Where(renderer => renderer is not null)
                .Cast<ChunkRender>()
                .ToArray();

            GL.DepthMask(true);
            foreach (ChunkRender renderer in currentRenderers)
                renderer.RenderOpaque(program);

            GL.DepthMask(false);
            foreach (ChunkRender renderer in currentRenderers)
                renderer.RenderTransparent(program);
            GL.DepthMask(true);
        }

        private void InitializeScheduling()
        {
            Console.WriteLine($"[World] Initializing scheduling workers...");
            schedulingWorkers = new Task[chunkScheduleWorkerCount];
            for (int i = 0; i < chunkScheduleWorkerCount; i++)
            {
                schedulingWorkers[i] = Task.Run(() => ChunkSchedulingWorker(schedulingCts.Token));
            }
            Console.WriteLine($"[World] Initialized {schedulingWorkers.Length} scheduling workers.");
        }

        // -------------------- Generation Worker --------------------
        private void ChunkGenerationWorker(CancellationToken token)
        {
            string chunkSaveDirectory = Path.Combine(loader.currentWorldSaveDirectory, loader.currentWorldSavedChunksSubDirectory);
            Vector3 lastPos = default;
            try
            {
                var queues = new[] { chunkPositionQueue, bufferChunkPositionQueue };
                while (!token.IsCancellationRequested)
                {
                    int taken = BlockingCollection<Vector3>.TryTakeFromAny(queues, out var pos, 100, token);
                    if (taken < 0) continue; // timeout
                    lastPos = pos;
                    try
                    {
                        bool isBuffer = (taken == 1); // index 1 = buffer queue
                        if (isBuffer && !chunkPositionQueue.IsCompleted && chunkGenSchedule.Count > 0)
                        {
                            // Active work has priority; put buffer request back and continue
                            bufferChunkPositionQueue.Add(pos, token);
                            continue;
                        }
                        int sizeX = GameManager.settings.chunkMaxX;
                        int sizeY = GameManager.settings.chunkMaxY;
                        int sizeZ = GameManager.settings.chunkMaxZ;
                        int cx = (int)Math.Floor(pos.X / sizeX);
                        int cy = (int)Math.Floor(pos.Y / sizeY);
                        int cz = (int)Math.Floor(pos.Z / sizeZ);
                        var key = (cx, cy, cz);
                        long regionLimit = GameManager.settings.regionWidthInChunks;
                        if (Math.Abs(cx) > regionLimit || Math.Abs(cy) > regionLimit || Math.Abs(cz) > regionLimit)
                        { RemoveGenerationSchedule(isBuffer ? bufferGenSchedule : chunkGenSchedule, key); continue; }

                        var (bx, bz) = Quadrant.GetBatchIndices(cx, cz);
                        bool batchExists = loadedBatches.TryGetValue((bx, bz), out var existingBatch);

                        int lodDist = GameManager.settings.lod1RenderDistance;
                        int playerCxSnapshot = playerChunkX; int playerCySnapshot = playerChunkY; int playerCzSnapshot = playerChunkZ;
                        int activeRadiusPlusOne = lodDist + 1;
                        int batchMinCx = bx * Quadrant.QUAD_SIZE;
                        int batchMinCz = bz * Quadrant.QUAD_SIZE;
                        int batchMaxCx = batchMinCx + Quadrant.QUAD_SIZE - 1;
                        int batchMaxCz = batchMinCz + Quadrant.QUAD_SIZE - 1;
                        bool intersects = !(batchMaxCx < playerCxSnapshot - activeRadiusPlusOne || batchMinCx > playerCxSnapshot + activeRadiusPlusOne || batchMaxCz < playerCzSnapshot - activeRadiusPlusOne || batchMinCz > playerCzSnapshot + activeRadiusPlusOne);
                        if (!intersects)
                        { RemoveGenerationSchedule(isBuffer ? bufferGenSchedule : chunkGenSchedule, key); continue; }

                        // If the quad exists and already contains this chunk instance, ensure it is re-registered in world dictionaries
                        if (batchExists && existingBatch.TryGetChunk(cx, cy, cz, out var existing))
                        {
                            bool insideCore = Math.Abs(cx - playerCxSnapshot) <= lodDist && Math.Abs(cz - playerCzSnapshot) <= lodDist && Math.Abs(cy - playerCySnapshot) <= lodDist;
                            bool insidePlusOne = Math.Abs(cx - playerCxSnapshot) <= lodDist + 1 && Math.Abs(cz - playerCzSnapshot) <= lodDist + 1 && Math.Abs(cy - playerCySnapshot) <= lodDist;
                            bool registered = false;

                            if (!activeChunks.ContainsKey(key) && !unbuiltChunks.ContainsKey(key) && !passiveChunks.ContainsKey(key))
                            {
                                using IDisposable stateScope =
                                    AcquireRenderStateWriteScope();
                                bool alreadyRegistered =
                                    activeChunks.ContainsKey(key) ||
                                    unbuiltChunks.ContainsKey(key) ||
                                    passiveChunks.ContainsKey(key);
                                if (!alreadyRegistered)
                                {
                                    MarkRenderNeighborsDirtyForTopologyChange(key);
                                    if (insideCore)
                                    {
                                        unbuiltChunks[key] = existing;
                                        registered = true;
                                    }
                                    else if (insidePlusOne)
                                    {
                                        passiveChunks[key] = existing;
                                        registered = true;
                                    }
                                    if (registered)
                                        MarkRenderNeighborsDirtyForTopologyChange(key);
                                }
                            }
                            if (insideCore)
                            {
                                EnqueueMeshBuild(key, markDirty: false);
                            }
                            RemoveGenerationSchedule(isBuffer ? bufferGenSchedule : chunkGenSchedule, key);
                            continue;
                        }

                        // Quick skip if chunk already materialized in dictionaries
                        if (activeChunks.ContainsKey(key) || unbuiltChunks.ContainsKey(key) || passiveChunks.ContainsKey(key))
                        { RemoveGenerationSchedule(isBuffer ? bufferGenSchedule : chunkGenSchedule, key); continue; }

                        // Acquire or create generation state for this quad
                        var state = generatingBatches.GetOrAdd((bx, bz), _ => new BatchGenerationState());
                        // Seed columns if first initializer
                        if (!state.Initialized)
                        {
                            lock (state)
                            {
                                if (!state.Initialized)
                                {
                                    int startCx = Math.Max(batchMinCx, playerCxSnapshot - activeRadiusPlusOne);
                                    int endCx = Math.Min(batchMaxCx, playerCxSnapshot + activeRadiusPlusOne);
                                    int startCz = Math.Max(batchMinCz, playerCzSnapshot - activeRadiusPlusOne);
                                    int endCz = Math.Min(batchMaxCz, playerCzSnapshot + activeRadiusPlusOne);
                                    int seeded = 0;
                                    for (int gcx = startCx; gcx <= endCx; gcx++)
                                        for (int gcz = startCz; gcz <= endCz; gcz++) { state.Columns.Enqueue((gcx, gcz)); seeded++; }
                                    state.RemainingColumns = seeded; state.Initialized = true;
                                }
                            }
                        }

                        // Worker joins this quad: allocate / get quad object
                        var batch = existingBatch ?? GetOrCreateBatch(bx, bz);

                        // New quad-centric generation: drain column queue invoking GenerateOrLoadColumn
                        Interlocked.Increment(ref state.ActiveWorkers);
                        int verticalRange = lodDist; // reuse heuristic

                        Quadrant.ChunkRegistrar registrar = (chunkKey, chunkInstance, insideLod1) =>
                        {
                            // Skip if already recorded (race safety)
                            if (activeChunks.ContainsKey(chunkKey) || unbuiltChunks.ContainsKey(chunkKey) || passiveChunks.ContainsKey(chunkKey)) return;
                            using IDisposable stateScope =
                                AcquireRenderStateWriteScope();
                            if (activeChunks.ContainsKey(chunkKey) ||
                                unbuiltChunks.ContainsKey(chunkKey) ||
                                passiveChunks.ContainsKey(chunkKey))
                            {
                                return;
                            }
                            MarkRenderNeighborsDirtyForTopologyChange(chunkKey);
                            if (insideLod1)
                                unbuiltChunks[chunkKey] = chunkInstance;
                            else
                                passiveChunks[chunkKey] = chunkInstance;
                            MarkRenderNeighborsDirtyForTopologyChange(chunkKey);
                            state.RegisteredChunks.Enqueue(chunkKey);
                            // Remove scheduling markers for this specific chunk if present
                            RemoveGenerationSchedule(chunkGenSchedule, chunkKey);
                            RemoveGenerationSchedule(bufferGenSchedule, chunkKey);
                        };

                        while (!token.IsCancellationRequested && state.Columns.TryDequeue(out var column))
                        {
                            batch.GenerateOrLoadColumn(column.cx, column.cz,
                                playerCxSnapshot, playerCySnapshot, playerCzSnapshot,
                                lodDist, verticalRange, regionLimit, loader.seed, chunkSaveDirectory,
                                sizeX, sizeY, sizeZ,
                                registrar);
                            Interlocked.Decrement(ref state.RemainingColumns);
                        }

                        int remainingWorkers = Interlocked.Decrement(ref state.ActiveWorkers);
                        if (remainingWorkers == 0 && state.Columns.IsEmpty && state.RemainingColumns <= 0)
                        {
                            RemoveGeneratingBatch((bx, bz));
                            while (state.RegisteredChunks.TryDequeue(out var registered))
                                MarkRenderNeighborsDirty(registered);

                            ScheduleVisibleChunksInBatch(bx, bz);
                        }

                        RemoveGenerationSchedule(isBuffer ? bufferGenSchedule : chunkGenSchedule, key);
                    }
                    catch (Exception exIter)
                    { Console.WriteLine($"[World] Quad-oriented generation error at pos={lastPos}: {exIter}"); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"Chunk generation worker fatal error (lastPos={lastPos}): {ex}"); }
        }

        private void MeshBuildWorker(CancellationToken token)
        {
            try
            {
                foreach (var key in meshBuildQueue.GetConsumingEnumerable(token))
                {
                    if (token.IsCancellationRequested) break;

                    using IDisposable stateScope =
                        AcquireRenderStateWriteScope();

                    int lodDist = GameManager.settings.lod1RenderDistance;
                    int verticalRange = lodDist;
                    bool insideCore = Math.Abs(key.cx - playerChunkX) <= lodDist && Math.Abs(key.cz - playerChunkZ) <= lodDist && Math.Abs(key.cy - playerChunkY) <= verticalRange;
                    bool insidePlusOne = Math.Abs(key.cx - playerChunkX) <= lodDist + 1 && Math.Abs(key.cz - playerChunkZ) <= lodDist + 1 && Math.Abs(key.cy - playerChunkY) <= verticalRange;
                    if (!insidePlusOne)
                    {
                        // Fully out of interest -> discard
                        unbuiltChunks.TryRemove(key, out _);
                        meshBuildSchedule.TryRemove(key, out _);
                        dirtyChunks.TryRemove(key, out _);
                        continue;
                    }

                    if (!unbuiltChunks.TryGetValue(key, out var ch))
                    {
                        if (!activeChunks.TryGetValue(key, out ch))
                        {
                            // If chunk is passive (in +1 ring) and got scheduled by race, skip quietly
                            passiveChunks.TryGetValue(key, out ch);
                            if (ch == null)
                            {
                                meshBuildSchedule.TryRemove(key, out _);
                                continue;
                            }
                        }
                    }

                    bool permitDirtyRetry = true;
                    bool deferredForMissingNeighbor = false;
                    long consumedDirtyRevision = 0;
                    try
                    {
                        if (!insideCore)
                        {
                            // Demote to passive if we drifted out of core radius before build.
                            if (unbuiltChunks.TryRemove(key, out var demote))
                            {
                                passiveChunks[key] = demote;
                            }
                            dirtyChunks.TryRemove(key, out _);
                            continue;
                        }

                        bool active = activeChunks.ContainsKey(key);
                        if (active && !dirtyChunks.TryGetValue(key, out consumedDirtyRevision))
                        {
                            // This chunk has current render data.
                            continue;
                        }
                        if (!active)
                            dirtyChunks.TryGetValue(key, out consumedDirtyRevision);

                        ReferenceNeighborBlockPlanes? referenceNeighbors = null;
                        if (faceGenerationMode == FaceGenerationMode.Reference)
                        {
                            if (!TryCreateReferenceNeighborBlockPlanes(
                                    key,
                                    out referenceNeighbors))
                            {
                                deferredForMissingNeighbor = true;
                                continue;
                            }
                        }
                        else if (!TryPrepareOptimizedNeighbors(key, ch))
                        {
                            deferredForMissingNeighbor = true;
                            continue;
                        }

                        long buildStart = StartupPerformanceRecorder.IsRunning ? Stopwatch.GetTimestamp() : 0;
                        ChunkRender? renderer = ch.CreateRender(
                            faceGenerationMode,
                            referenceNeighbors);
                        ch.PublishRender(renderer);
                        if (faceGenerationMode == FaceGenerationMode.Reference)
                            ValidateReferenceRenderData(key, ch);
                        if (buildStart != 0)
                            StartupPerformanceRecorder.RecordFirstChunkBuild(Stopwatch.GetElapsedTime(buildStart));

                        if (unbuiltChunks.TryGetValue(key, out Chunk? builtChunk))
                        {
                            activeChunks[key] = builtChunk;
                            unbuiltChunks.TryRemove(key, out _);
                        }
                        if (consumedDirtyRevision != 0)
                            TryRemoveDirtyRevision(key, consumedDirtyRevision);
                    }
                    catch (Exception ex)
                    {
                        permitDirtyRetry = false;
                        RecordMeshBuildFailure(key, ex);
                        Console.WriteLine($"Mesh build error for chunk {key}: {ex.Message}");
                    }
                    finally
                    {
                        meshBuildSchedule.TryRemove(key, out _);
                        if (permitDirtyRetry &&
                            dirtyChunks.TryGetValue(key, out long pendingDirtyRevision) &&
                            (!deferredForMissingNeighbor ||
                             pendingDirtyRevision != consumedDirtyRevision))
                        {
                            EnqueueMeshBuild(key, markDirty: false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("Mesh build worker error: " + ex.Message);
            }
        }

        private static readonly (int dx, int dy, int dz)[] NeighborDirs = new (int, int, int)[]
        {
            (-1,0,0),(1,0,0),(0,-1,0),(0,1,0),(0,0,-1),(0,0,1)
        };

        private void MarkRenderNeighborsDirtyForTopologyChange(
            (int cx, int cy, int cz) key)
        {
            MarkRenderNeighborsDirty(key);
        }

        // Helpers to snapshot neighbor planes so the renderer reads stable, per-build copies.
        private static ushort[] SnapshotUShorts(ushort[] src) => src == null ? null : (ushort[])src.Clone();
        private static ulong[] SnapshotULongs(ulong[] src) => src == null ? null : (ulong[])src.Clone();

        private bool TryPrepareOptimizedNeighbors(
            (int cx, int cy, int cz) key,
            Chunk chunk)
        {
            if (!TryGetRequiredRenderNeighbor((key.cx - 1, key.cy, key.cz), out _) ||
                !TryGetRequiredRenderNeighbor((key.cx + 1, key.cy, key.cz), out _) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy - 1, key.cz), out _) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy + 1, key.cz), out _) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy, key.cz - 1), out _) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy, key.cz + 1), out _))
            {
                return false;
            }

            TryMarkBuriedByNeighbors(key, chunk);
            PopulateNeighborFaceFlags(key, chunk);
            return true;
        }

        private void PopulateNeighborFaceFlags((int cx, int cy, int cz) key, Chunk ch)
        {
            // Reset all neighbor-derived state first to avoid stale references causing false seam suppression.
            if (ch == null) return;

            TryGetChunk((key.cx - 1, key.cy, key.cz), out var left);
            TryGetChunk((key.cx + 1, key.cy, key.cz), out var right);
            TryGetChunk((key.cx, key.cy - 1, key.cz), out var down);
            TryGetChunk((key.cx, key.cy + 1, key.cz), out var up);
            TryGetChunk((key.cx, key.cy, key.cz - 1), out var back);
            TryGetChunk((key.cx, key.cy, key.cz + 1), out var front);


            ch.NeighborNegXFaceSolidPosX = false;
            ch.NeighborPosXFaceSolidNegX = false;
            ch.NeighborNegYFaceSolidPosY = false;
            ch.NeighborPosYFaceSolidNegY = false;
            ch.NeighborNegZFaceSolidPosZ = false;
            ch.NeighborPosZFaceSolidNegZ = false;

            ch.NeighborPlaneNegXFace = null;
            ch.NeighborPlanePosXFace = null;
            ch.NeighborPlaneNegYFace = null;
            ch.NeighborPlanePosYFace = null;
            ch.NeighborPlaneNegZFace = null;
            ch.NeighborPlanePosZFace = null;

            ch.NeighborTransparentPlaneNegXFace = null;
            ch.NeighborTransparentPlanePosXFace = null;
            ch.NeighborTransparentPlaneNegYFace = null;
            ch.NeighborTransparentPlanePosYFace = null;
            ch.NeighborTransparentPlaneNegZFace = null;
            ch.NeighborTransparentPlanePosZFace = null;

            if (left != null)
            {
                ch.NeighborNegXFaceSolidPosX = left.FaceSolidPosX;
                ch.NeighborPlaneNegXFace = SnapshotULongs(left.PlanePosX);                 // opaque snapshot
                ch.NeighborTransparentPlaneNegXFace = SnapshotUShorts(left.TransparentPlanePosX); // transparent snapshot
            }
            if (right != null)
            {
                ch.NeighborPosXFaceSolidNegX = right.FaceSolidNegX;
                ch.NeighborPlanePosXFace = SnapshotULongs(right.PlaneNegX);
                ch.NeighborTransparentPlanePosXFace = SnapshotUShorts(right.TransparentPlaneNegX);
            }
            if (down != null)
            {
                ch.NeighborNegYFaceSolidPosY = down.FaceSolidPosY;
                ch.NeighborPlaneNegYFace = SnapshotULongs(down.PlanePosY);
                ch.NeighborTransparentPlaneNegYFace = SnapshotUShorts(down.TransparentPlanePosY);
            }
            if (up != null)
            {
                ch.NeighborPosYFaceSolidNegY = up.FaceSolidNegY;
                ch.NeighborPlanePosYFace = SnapshotULongs(up.PlaneNegY);
                ch.NeighborTransparentPlanePosYFace = SnapshotUShorts(up.TransparentPlaneNegY);
            }
            if (back != null)
            {
                ch.NeighborNegZFaceSolidPosZ = back.FaceSolidPosZ;
                ch.NeighborPlaneNegZFace = SnapshotULongs(back.PlanePosZ);
                ch.NeighborTransparentPlaneNegZFace = SnapshotUShorts(back.TransparentPlanePosZ);
            }
            if (front != null)
            {
                ch.NeighborPosZFaceSolidNegZ = front.FaceSolidNegZ;
                ch.NeighborPlanePosZFace = SnapshotULongs(front.PlaneNegZ);
                ch.NeighborTransparentPlanePosZFace = SnapshotUShorts(front.TransparentPlaneNegZ);
            }
        }

        private bool TryCreateReferenceNeighborBlockPlanes(
            (int cx, int cy, int cz) key,
            out ReferenceNeighborBlockPlanes? planes)
        {
            if (!TryGetRequiredRenderNeighbor((key.cx - 1, key.cy, key.cz), out var left) ||
                !TryGetRequiredRenderNeighbor((key.cx + 1, key.cy, key.cz), out var right) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy - 1, key.cz), out var down) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy + 1, key.cz), out var up) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy, key.cz - 1), out var back) ||
                !TryGetRequiredRenderNeighbor((key.cx, key.cy, key.cz + 1), out var front))
            {
                planes = null;
                return false;
            }

            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;

            planes = new ReferenceNeighborBlockPlanes(
                SnapshotXPlane(left, maxX - 1, maxY, maxZ),
                SnapshotXPlane(right, 0, maxY, maxZ),
                SnapshotYPlane(down, maxY - 1, maxX, maxZ),
                SnapshotYPlane(up, 0, maxX, maxZ),
                SnapshotZPlane(back, maxZ - 1, maxX, maxY),
                SnapshotZPlane(front, 0, maxX, maxY));
            return true;
        }

        private bool TryGetRequiredRenderNeighbor(
            (int cx, int cy, int cz) key,
            out Chunk? chunk)
        {
            if (TryGetChunk(key, out Chunk existing))
            {
                chunk = existing;
                return true;
            }

            chunk = null;
            int lodDistance = GameManager.settings.lod1RenderDistance;
            long regionLimit = GameManager.settings.regionWidthInChunks;
            bool insideRegion = Math.Abs(key.cx) <= regionLimit &&
                                Math.Abs(key.cy) <= regionLimit &&
                                Math.Abs(key.cz) <= regionLimit;
            bool expectedInMemory = Math.Abs(key.cx - playerChunkX) <= lodDistance + 1 &&
                                    Math.Abs(key.cz - playerChunkZ) <= lodDistance + 1 &&
                                    Math.Abs(key.cy - playerChunkY) <= lodDistance;
            return !insideRegion || !expectedInMemory;
        }

        private static ReferenceBlockPlane SnapshotXPlane(
            Chunk? chunk,
            int x,
            int maxY,
            int maxZ)
        {
            if (chunk is null || chunk.AllAirChunk)
                return ReferenceBlockPlane.Uniform(0);
            if (chunk.AllOneBlockChunk)
                return ReferenceBlockPlane.Uniform(chunk.AllOneBlockBlockId);

            var result = new ushort[checked(maxY * maxZ)];
            for (int z = 0; z < maxZ; z++)
            {
                for (int y = 0; y < maxY; y++)
                    result[z * maxY + y] = chunk.GetBlockLocal(x, y, z);
            }

            return ReferenceBlockPlane.FromBlocks(result);
        }

        private static ReferenceBlockPlane SnapshotYPlane(
            Chunk? chunk,
            int y,
            int maxX,
            int maxZ)
        {
            if (chunk is null || chunk.AllAirChunk)
                return ReferenceBlockPlane.Uniform(0);
            if (chunk.AllOneBlockChunk)
                return ReferenceBlockPlane.Uniform(chunk.AllOneBlockBlockId);

            var result = new ushort[checked(maxX * maxZ)];
            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                    result[x * maxZ + z] = chunk.GetBlockLocal(x, y, z);
            }

            return ReferenceBlockPlane.FromBlocks(result);
        }

        private static ReferenceBlockPlane SnapshotZPlane(
            Chunk? chunk,
            int z,
            int maxX,
            int maxY)
        {
            if (chunk is null || chunk.AllAirChunk)
                return ReferenceBlockPlane.Uniform(0);
            if (chunk.AllOneBlockChunk)
                return ReferenceBlockPlane.Uniform(chunk.AllOneBlockBlockId);

            var result = new ushort[checked(maxX * maxY)];
            for (int x = 0; x < maxX; x++)
            {
                for (int y = 0; y < maxY; y++)
                    result[x * maxY + y] = chunk.GetBlockLocal(x, y, z);
            }

            return ReferenceBlockPlane.FromBlocks(result);
        }

        private bool TryGetChunk((int cx,int cy,int cz) key, out Chunk chunk)
        {
            if (activeChunks.TryGetValue(key, out chunk)) return true;
            if (unbuiltChunks.TryGetValue(key, out chunk)) return true;
            if (passiveChunks.TryGetValue(key, out chunk)) return true; // corrected capitalization
            chunk = null; return false;
        }

        // Background worker that continually ensures required chunks around player are scheduled.
        private void ChunkSchedulingWorker(CancellationToken token)
        {
            int lastCenterCx = int.MinValue;
            int lastCenterCy = int.MinValue;
            int lastCenterCz = int.MinValue;
            int sleepMs = 50;
            while (!token.IsCancellationRequested)
            {
                var (pcx, pcy, pcz) = PlayerChunkPosition;
                bool moved = pcx != lastCenterCx || pcy != lastCenterCy || pcz != lastCenterCz;
                if (moved)
                {
                    lastCenterCx = pcx; lastCenterCy = pcy; lastCenterCz = pcz;
                    Console.WriteLine($"[World] Player moved to chunk ({pcx}, {pcy}, {pcz}), scheduling surrounding chunks.");
                    try
                    {
                        ScheduleChunksAroundPlayer(pcx, pcy, pcz);
                        UnloadFarChunks(pcx, pcy, pcz);
                        MarkRenderActiveChunksDirty();
                        PruneOutOfRangeBufferChunks(pcx, pcy, pcz);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[World] Chunk scheduling error: {ex.Message}");
                    }
                }
                // Periodic save check (lightweight)
                MaybePeriodicSave();
                Thread.Sleep(sleepMs);
            }
        }

        public void Dispose()
        {
            try
            {
                schedulingCts?.Cancel();
                generationCts?.Cancel();
                meshBuildCts?.Cancel();
                chunkPositionQueue?.CompleteAdding();
                bufferChunkPositionQueue?.CompleteAdding();
                meshBuildQueue?.CompleteAdding();
                if (generationWorkers != null)
                {
                    Task.WaitAll(generationWorkers, TimeSpan.FromSeconds(2));
                }
                if (meshBuildWorkers != null)
                {
                    Task.WaitAll(meshBuildWorkers, TimeSpan.FromSeconds(2));
                }
                if (schedulingWorkers != null)
                {
                    Task.WaitAll(schedulingWorkers, TimeSpan.FromSeconds(2));
                }
            }
            catch { }
            finally
            {
                // Force save all dirty quads on shutdown
                SaveAllBatches(force:true);
                schedulingCts?.Dispose();
                generationCts?.Dispose();
                meshBuildCts?.Dispose();
                chunkPositionQueue?.Dispose();
                bufferChunkPositionQueue?.Dispose();
                meshBuildQueue?.Dispose();
                renderStateLock.Dispose();
                bool generationWorkersStopped =
                    generationWorkers is null ||
                    generationWorkers.All(worker => worker.IsCompleted);
                bool schedulingWorkersStopped =
                    schedulingWorkers is null ||
                    schedulingWorkers.All(worker => worker.IsCompleted);
                if (generationWorkersStopped && schedulingWorkersStopped)
                    initialGenerationCompletion.Dispose();
            }
        }

        // Manual save entry point
        public void ManualSaveWorld() => SaveAllBatches(force:true);

        // Periodic save tick
        private void MaybePeriodicSave()
        {
            if (DateTime.UtcNow - lastFullWorldSave < TimeSpan.FromMinutes(worldSaveIntervalMinutes)) return;
            SaveAllBatches(force:false);
        }

        public ushort GetBlock(int wx, int wy, int wz)
        {
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;

            int cx = FloorDiv(wx, sizeX);
            int cy = FloorDiv(wy, sizeY);
            int cz = FloorDiv(wz, sizeZ);

            var key = (cx, cy, cz);
            if (!unbuiltChunks.TryGetValue(key, out var chunk))
            {
                if (!activeChunks.TryGetValue(key, out chunk))
                {
                    if (!passiveChunks.TryGetValue(key, out chunk))
                        return (ushort)BaseBlockType.Empty;
                }
            }

            int localX = wx - cx * sizeX;
            int localY = wy - cy * sizeY;
            int localZ = wz - cz * sizeZ;

            return chunk.GetBlockLocal(localX, localY, localZ);
        }

        private static int FloorDiv(int a, int b)
        {
            return (int)Math.Floor((double)a / b);
        }

        private static (int cx, int cy, int cz) ChunkIndexKey(int baseX, int baseY, int baseZ)
        {
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;

            int cx = FloorDiv(baseX, sizeX);
            int cy = FloorDiv(baseY, sizeY);
            int cz = FloorDiv(baseZ, sizeZ);
            return (cx, cy, cz);
        }

        // helper for neighbor-based chunk burial detection
        private void TryMarkBuriedByNeighbors((int cx, int cy, int cz) key, Chunk ch)
        {
            // Only perform on initial build attempt; if chunk already active we skip.
            if (!unbuiltChunks.ContainsKey(key)) return;

            var leftKey = (key.cx - 1, key.cy, key.cz);
            var rightKey = (key.cx + 1, key.cy, key.cz);
            var downKey = (key.cx, key.cy - 1, key.cz);
            var upKey = (key.cx, key.cy + 1, key.cz);
            var backKey = (key.cx, key.cy, key.cz - 1); // negative Z
            var frontKey = (key.cx, key.cy, key.cz + 1); // positive Z

            if (!TryGetChunk(leftKey, out var left)) return;
            if (!TryGetChunk(rightKey, out var right)) return;
            if (!TryGetChunk(downKey, out var down)) return;
            if (!TryGetChunk(upKey, out var up)) return;
            if (!TryGetChunk(backKey, out var back)) return;
            if (!TryGetChunk(frontKey, out var front)) return;

            // Opposing faces: our -X must be solid and neighbor's +X solid, etc.
            // Also ensure all our faces solid (prevents skipping if we have any exposed face ourselves).
            if (ch.FaceSolidNegX && ch.FaceSolidPosX && ch.FaceSolidNegY && ch.FaceSolidPosY && ch.FaceSolidNegZ && ch.FaceSolidPosZ &&
                left.FaceSolidPosX && right.FaceSolidNegX &&
                down.FaceSolidPosY && up.FaceSolidNegY &&
                back.FaceSolidPosZ && front.FaceSolidNegZ)
            {
                ch.SetNeighborBuried();
            }
        }

        // Returns existing quad or creates placeholder (without populating chunks yet).
        private Quadrant GetOrCreateBatch(int bx, int bz)
        {
            return loadedBatches.GetOrAdd((bx, bz), key => new Quadrant(key.bx, key.bz));
        }

        // Compute quad indices from chunk indices.
        private static (int bx, int bz) BatchKeyFromChunk(int cx, int cz) => Quadrant.GetBatchIndices(cx, cz);

        // Ensure the quad containing (cx,cz) is loaded from disk (if present) into memory.
        // If already loaded, no-op. Returns true if target chunk present after call.
        private bool EnsureBatchLoadedForChunk(int cx, int cy, int cz)
        {
            var (bx, bz) = BatchKeyFromChunk(cx, cz);
            if (loadedBatches.ContainsKey((bx, bz)))
            {
                return activeChunks.ContainsKey((cx, cy, cz)) || unbuiltChunks.ContainsKey((cx, cy, cz)) || passiveChunks.ContainsKey((cx, cy, cz));
            }
            // Attempt to load quad file
            var chunk = LoadBatchForChunk(cx, cy, cz); // will populate quad + dictionaries if file exists
            return chunk != null;
        }

        // Schedules mesh builds for all chunks in a quad that fall inside the current active LoD radius.
        private void ScheduleVisibleChunksInBatch(int bx, int bz)
        {
            using IDisposable stateScope =
                AcquireRenderStateWriteScope();
            int lodDist = GameManager.settings.lod1RenderDistance;
            // Determine center (player) chunk
            var (pcx, pcy, pcz) = PlayerChunkPosition;
            // Iterate horizontal footprint
            int baseCx = bx * Quadrant.QUAD_SIZE;
            int baseCz = bz * Quadrant.QUAD_SIZE;
            for (int lx = 0; lx < Quadrant.QUAD_SIZE; lx++)
            {
                int cx = baseCx + lx;
                if (Math.Abs(cx - pcx) > lodDist + 1) continue; // +1 ring always kept in memory
                for (int lz = 0; lz < Quadrant.QUAD_SIZE; lz++)
                {
                    int cz = baseCz + lz;
                    if (Math.Abs(cz - pcz) > lodDist + 1) continue;
                    // Iterate vertical rows inside LoD vertical window
                    int verticalRange = lodDist; // reuse existing heuristic
                    for (int cy = pcy - verticalRange; cy <= pcy + verticalRange; cy++)
                    {
                        var key = (cx, cy, cz);
                        if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key))
                        {
                            // If chunk not yet scheduled for mesh build and inside LoD1 (not just +1 ring) schedule it.
                            if (Math.Abs(cx - pcx) <= lodDist && Math.Abs(cz - pcz) <= lodDist && Math.Abs(cy - pcy) <= verticalRange)
                            {
                                EnqueueMeshBuild(key, markDirty: false);
                            }
                        }
                        else if (passiveChunks.ContainsKey(key))
                        {
                            // Promotion condition: passive chunk is now inside LoD1
                            if (Math.Abs(cx - pcx) <= lodDist && Math.Abs(cz - pcz) <= lodDist && Math.Abs(cy - pcy) <= verticalRange)
                            {
                                if (passiveChunks.TryRemove(key, out var promoted))
                                {
                                    unbuiltChunks[key] = promoted;
                                    EnqueueMeshBuild(key, markDirty: false);
                                }
                            }
                        }
                    }
                }
            }
        }
        private void EnsureBatchesForActiveArea_Rehydrate(int centerCx,int centerCz)
        {
            int lodDist = GameManager.settings.lod1RenderDistance + 1; // +1 ring per new design
            for (int dx = -lodDist; dx <= lodDist; dx++)
            {
                for (int dz = -lodDist; dz <= lodDist; dz++)
                {
                    int cx = centerCx + dx;
                    int cz = centerCz + dz;
                    var (bx,bz) = Quadrant.GetBatchIndices(cx, cz);
                    // Touch quad (forces placeholder creation or load if file exists)
                    if (!loadedBatches.ContainsKey((bx,bz)))
                    {
                        if (BatchFileExists(bx,bz))
                        {
                            LoadBatchForChunk(cx, centerCz, cz); // vertical index not needed for batch load
                        }
                        else
                        {
                            // If no quad file, will be populated lazily as chunks generate.
                            GetOrCreateBatch(bx,bz);
                        }
                    }
                    // Ensure any already-loaded quad schedules visible chunks for mesh build upon entering area
                    ScheduleVisibleChunksInBatch(bx,bz);
                }
            }
        }
    }
}

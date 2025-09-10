using MVGE_GFX;
using MVGE_INF.Managers;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using MVGE_INF.Loaders;
using MVGE_GEN.Terrain;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Models.Generation;

namespace MVGE_GEN
{
    public partial class WorldResources : IDisposable
    {
        public Guid ID { get; private set; }
        public Guid RegionID { get; private set; }
        public WorldLoader loader { get; private set; }

        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> activeChunks = new(); // track ready to render chunks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> unbuiltChunks = new(); // track generated but not yet built chunks
        // passive chunks (generated but outside LoD1, e.g. +1 ring) kept resident but not scheduled for mesh build.
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> passiveChunks = new();

        // Track chunks marked dirty (needing rebuild) so we can coalesce multiple requests before building
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> dirtyChunks = new();

        // Track chunks that have been cancelled (scheduled then later deemed too far before gen/build)
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> cancelledChunks = new();

        // Cancellation token sources per pipeline (scheduling kept separate so we can recycle gen/build workers)
        private CancellationTokenSource schedulingCts;          // drives scheduling worker lifetime
        private CancellationTokenSource generationCts;          // drives current generation worker set
        private CancellationTokenSource meshBuildCts;           // drives current mesh build worker set

        // World block accessor delegates
        private Func<int, int, int, ushort> worldBlockAccessor;

        // Asynchronous scheduling pipeline
        private int chunkScheduleWorkerCount = 1; 
        private Task[] schedulingWorkers;

        // Asynchronous generation pipeline (current active workers)
        private int generationWorkerCount; // current (may change after staged init)
        private Task[] generationWorkers;
        private BlockingCollection<Vector3> chunkPositionQueue; // gen tasks (LoD1 + active rings)
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> chunkGenSchedule = new(); // track enqueued but not yet generated

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

        // ---------------- Intra-batch parallel generation state ----------------
        private sealed class BatchGenerationState
        {
            public readonly ConcurrentQueue<(int cx,int cz)> Columns = new();
            public int RemainingColumns; // decremented per column processed (approximate; may over-decrement if duplicates skipped)
            public int ActiveWorkers; // number of workers currently draining queue
            public volatile bool Initialized; // set true once columns seeded
        }


        // ---------------- BATCH STORAGE (32x32 horizontal groups) ----------------
        // A batch groups chunks for all vertical layers sharing a 32x32 (cx,cz) footprint.
        // When any chunk in a batch is requested (load or generation), the whole batch
        // is loaded (from batch file if present) or generated on-demand over time.
        private readonly ConcurrentDictionary<(int bx, int bz), Quadrant> loadedBatches = new();

        // Track batches currently being generated. Value holds queue/state instead of a simple byte now.
        private readonly ConcurrentDictionary<(int bx,int bz), BatchGenerationState> generatingBatches = new();

        public WorldResources()
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
            loader.ChooseWorld();
            ID = loader.ID;
            RegionID = loader.RegionID;
            Console.WriteLine("World data loaded.");

            worldBlockAccessor = GetBlock;
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

                // 2. Initial mesh build workers (after gen complete) - only for active (LoD1) chunks
                StartMeshBuildWorkers(initialMesh);
                // Chunks were already enqueued for build during generation
                WaitForInitialChunkRenderBuild();
                StopMeshBuildWorkers();

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
            var sw = Stopwatch.StartNew();

            // Progress loop now also waits for any in‑flight batch (generatingBatches)
            while (chunkGenSchedule.Count > 0 || bufferGenSchedule.Count > 0 || generatingBatches.Count > 0)
            {
                int remainingActive = chunkGenSchedule.Count;
                int remainingBuffer = bufferGenSchedule.Count;
                int inflightBatches = generatingBatches.Count;
                int generatedChunks = unbuiltChunks.Count + activeChunks.Count; // passive excluded from initial LoD1 mesh requirement
                Console.WriteLine($"[World] Initial generation progress: scheduledActive={remainingActive}, scheduledBuffer={remainingBuffer}, inFlightBatches={inflightBatches}, generatedChunks={generatedChunks}");
                Thread.Sleep(500);
            }
            sw.Stop();
            Console.WriteLine($"[World] Initial generation complete in {sw.ElapsedMilliseconds} ms. (Generated chunks: {unbuiltChunks.Count + activeChunks.Count})");
        }

        private void WaitForInitialChunkRenderBuild()
        {
            Console.WriteLine("[World] Building chunk meshes asynchronously...");
            var sw = Stopwatch.StartNew();
            // Snapshot target set at start to avoid negative progress when late chunks arrive. (Only LoD1 unbuilt chunks considered)
            var targetSet = new HashSet<(int cx,int cy,int cz)>(unbuiltChunks.Keys);
            int initialTotal = targetSet.Count;
            int lastLogRemaining = -1;
            while (true)
            {
                int remaining = 0;
                foreach (var key in targetSet)
                {
                    if (unbuiltChunks.ContainsKey(key)) remaining++;
                }
                // Exit once all initial targets either built (moved to active) or removed AND mesh build queue drained for those targets.
                if (meshBuildSchedule.Count == 0 && remaining == 0)
                    break;
                if (remaining != lastLogRemaining)
                {
                    int built = initialTotal - remaining;
                    Console.WriteLine($"[World] Chunk mesh build: {built}/{initialTotal}, remaining: {remaining}");
                    lastLogRemaining = remaining;
                }
                Thread.Sleep(500);
            }
            sw.Stop();
            Console.WriteLine($"[World] Chunk mesh build complete in {sw.ElapsedMilliseconds} ms.");
        }

        public void Render(ShaderProgram program)
        {
            foreach (var chunk in activeChunks.Values) chunk.Render(program);
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
                        { if (isBuffer) bufferGenSchedule.TryRemove(key, out _); else chunkGenSchedule.TryRemove(key, out _); continue; }

                        var (bx, bz) = Quadrant.GetBatchIndices(cx, cz);
                        bool batchExists = loadedBatches.TryGetValue((bx, bz), out var existingBatch);

                        // Quick skip if chunk already materialized
                        if (batchExists && (existingBatch.TryGetChunk(cx, cy, cz, out _) || activeChunks.ContainsKey(key) || unbuiltChunks.ContainsKey(key) || passiveChunks.ContainsKey(key)))
                        { if (isBuffer) bufferGenSchedule.TryRemove(key, out _); else chunkGenSchedule.TryRemove(key, out _); continue; }

                        int lodDist = GameManager.settings.lod1RenderDistance;
                        int playerCxSnapshot = playerChunkX; int playerCySnapshot = playerChunkY; int playerCzSnapshot = playerChunkZ;
                        int activeRadiusPlusOne = lodDist + 1;
                        int batchMinCx = bx * Quadrant.QUAD_SIZE;
                        int batchMinCz = bz * Quadrant.QUAD_SIZE;
                        int batchMaxCx = batchMinCx + Quadrant.QUAD_SIZE - 1;
                        int batchMaxCz = batchMinCz + Quadrant.QUAD_SIZE - 1;
                        bool intersects = !(batchMaxCx < playerCxSnapshot - activeRadiusPlusOne || batchMinCx > playerCxSnapshot + activeRadiusPlusOne || batchMaxCz < playerCzSnapshot - activeRadiusPlusOne || batchMinCz > playerCzSnapshot + activeRadiusPlusOne);
                        if (!intersects)
                        { if (isBuffer) bufferGenSchedule.TryRemove(key, out _); else chunkGenSchedule.TryRemove(key, out _); continue; }

                        // Acquire or create generation state for this batch
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

                        // Worker joins this batch: allocate / get batch object
                        var batch = existingBatch ?? GetOrCreateBatch(bx, bz);

                        // New batch-centric generation: drain column queue invoking batch.GenerateOrLoadColumn
                        Interlocked.Increment(ref state.ActiveWorkers);
                        int verticalRange = lodDist; // reuse heuristic

                        Quadrant.ChunkRegistrar registrar = (chunkKey, chunkInstance, insideLod1) =>
                        {
                            // Skip if already recorded (race safety)
                            if (activeChunks.ContainsKey(chunkKey) || unbuiltChunks.ContainsKey(chunkKey) || passiveChunks.ContainsKey(chunkKey)) return;
                            if (insideLod1) unbuiltChunks[chunkKey] = chunkInstance; else passiveChunks[chunkKey] = chunkInstance;
                            // Remove scheduling markers for this specific chunk if present
                            chunkGenSchedule.TryRemove(chunkKey, out _);
                            bufferGenSchedule.TryRemove(chunkKey, out _);
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
                        { generatingBatches.TryRemove((bx, bz), out _); ScheduleVisibleChunksInBatch(bx, bz); }

                        if (isBuffer) bufferGenSchedule.TryRemove(key, out _); else chunkGenSchedule.TryRemove(key, out _);
                    }
                    catch (Exception exIter)
                    { Console.WriteLine($"[World] Batch-oriented generation error at pos={lastPos}: {exIter}"); }
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

                    meshBuildSchedule.TryRemove(key, out _);

                    if (!unbuiltChunks.TryGetValue(key, out var ch))
                    {
                        if (!activeChunks.TryGetValue(key, out ch))
                        {
                            // If chunk is passive (in +1 ring) and got scheduled by race, skip quietly
                            passiveChunks.TryGetValue(key, out ch);
                            if (ch == null) continue;
                        }
                    }

                    try
                    {
                        if (!insideCore)
                        {
                            // Demote to passive if we drifted out of core radius before build.
                            if (unbuiltChunks.TryRemove(key, out var demote))
                            {
                                passiveChunks[key] = demote;
                            }
                            continue;
                        }

                        TryMarkBuriedByNeighbors(key, ch);
                        PopulateNeighborFaceFlags(key, ch);

                        if (activeChunks.ContainsKey(key) && !dirtyChunks.ContainsKey(key))
                        {
                            // Nothing to do; skip rebuilding
                            continue;
                        }

                        ch.BuildRender(worldBlockAccessor);

                        if (unbuiltChunks.TryRemove(key, out var builtChunk))
                        {
                            activeChunks[key] = builtChunk;
                        }
                        dirtyChunks.TryRemove(key, out _);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Mesh build error for chunk {key}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("Mesh build worker error: " + ex.Message);
            }
        }

        private static readonly (int dx,int dy,int dz)[] NeighborDirs = new (int,int,int)[]
        {
            (-1,0,0),(1,0,0),(0,-1,0),(0,1,0),(0,0,-1),(0,0,1)
        };

        private bool HasAnySolidOnBoundary(Chunk chunk, (int dx,int dy,int dz) dir)
        {
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            ushort empty = (ushort)BaseBlockType.Empty;

            if (dir.dx != 0)
            {
                int x = dir.dx < 0 ? 0 : sizeX - 1; // plane x=0 or x=max-1
                for (int y = 0; y < sizeY; y++)
                    for (int z = 0; z < sizeZ; z++)
                        if (chunk.GetBlockLocal(x, y, z) != empty) return true;
                return false;
            }
            if (dir.dy != 0)
            {
                int y = dir.dy < 0 ? 0 : sizeY - 1;
                for (int x = 0; x < sizeX; x++)
                    for (int z = 0; z < sizeZ; z++)
                        if (chunk.GetBlockLocal(x, y, z) != empty) return true;
                return false;
            }
            if (dir.dz != 0)
            {
                int z = dir.dz < 0 ? 0 : sizeZ - 1;
                for (int x = 0; x < sizeX; x++)
                    for (int y = 0; y < sizeY; y++)
                        if (chunk.GetBlockLocal(x, y, z) != empty) return true;
                return false;
            }
            return false;
        }

        private void PopulateNeighborFaceFlags((int cx,int cy,int cz) key, Chunk ch)
        {
            // Attempt to set neighbor face solidity flags. Missing neighbors -> leave false.
            if (ch == null) return;
            TryGetChunk((key.cx - 1, key.cy, key.cz), out var left);
            TryGetChunk((key.cx + 1, key.cy, key.cz), out var right);
            TryGetChunk((key.cx, key.cy - 1, key.cz), out var down);
            TryGetChunk((key.cx, key.cy + 1, key.cz), out var up);
            TryGetChunk((key.cx, key.cy, key.cz - 1), out var back);
            TryGetChunk((key.cx, key.cy, key.cz + 1), out var front);
            if (left != null)
            {
                ch.NeighborNegXFaceSolidPosX = left.FaceSolidPosX;
                ch.NeighborPlaneNegXFace = left.PlanePosX; // neighbor +X face
            }
            if (right != null)
            {
                ch.NeighborPosXFaceSolidNegX = right.FaceSolidNegX;
                ch.NeighborPlanePosXFace = right.PlaneNegX;
            }
            if (down != null)
            {
                ch.NeighborNegYFaceSolidPosY = down.FaceSolidPosY;
                ch.NeighborPlaneNegYFace = down.PlanePosY;
            }
            if (up != null)
            {
                ch.NeighborPosYFaceSolidNegY = up.FaceSolidNegY;
                ch.NeighborPlanePosYFace = up.PlaneNegY;
            }
            if (back != null)
            {
                ch.NeighborNegZFaceSolidPosZ = back.FaceSolidPosZ;
                ch.NeighborPlaneNegZFace = back.PlanePosZ;
            }
            if (front != null)
            {
                ch.NeighborPosZFaceSolidNegZ = front.FaceSolidNegZ;
                ch.NeighborPlanePosZFace = front.PlaneNegZ;
            }
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
                // Force save all dirty batches on shutdown
                SaveAllBatches(force:true);
                schedulingCts?.Dispose();
                generationCts?.Dispose();
                meshBuildCts?.Dispose();
                chunkPositionQueue?.Dispose();
                bufferChunkPositionQueue?.Dispose();
                meshBuildQueue?.Dispose();
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

            // We require all six neighbors to be present (either generated but unbuilt, or already active).
            static bool GetChunk((int cx, int cy, int cz) k,
                                 ConcurrentDictionary<(int, int, int), Chunk> active,
                                 ConcurrentDictionary<(int, int, int), Chunk> pending,
                                 out Chunk result)
            {
                if (active.TryGetValue(k, out result)) return true;
                if (pending.TryGetValue(k, out result)) return true;
                result = null; return false;
            }

            var leftKey = (key.cx - 1, key.cy, key.cz);
            var rightKey = (key.cx + 1, key.cy, key.cz);
            var downKey = (key.cx, key.cy - 1, key.cz);
            var upKey = (key.cx, key.cy + 1, key.cz);
            var backKey = (key.cx, key.cy, key.cz - 1); // negative Z
            var frontKey = (key.cx, key.cy, key.cz + 1); // positive Z

            if (!GetChunk(leftKey, activeChunks, unbuiltChunks, out var left)) return;
            if (!GetChunk(rightKey, activeChunks, unbuiltChunks, out var right)) return;
            if (!GetChunk(downKey, activeChunks, unbuiltChunks, out var down)) return;
            if (!GetChunk(upKey, activeChunks, unbuiltChunks, out var up)) return;
            if (!GetChunk(backKey, activeChunks, unbuiltChunks, out var back)) return;
            if (!GetChunk(frontKey, activeChunks, unbuiltChunks, out var front)) return;

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

        // Returns existing batch or creates placeholder (without populating chunks yet).
        private Quadrant GetOrCreateBatch(int bx, int bz)
        {
            return loadedBatches.GetOrAdd((bx, bz), key => new Quadrant(key.bx, key.bz));
        }

        // Compute batch indices from chunk indices.
        private static (int bx, int bz) BatchKeyFromChunk(int cx, int cz) => Quadrant.GetBatchIndices(cx, cz);

        // Ensure the batch containing (cx,cz) is loaded from disk (if present) into memory.
        // If already loaded, no-op. Returns true if target chunk present after call.
        private bool EnsureBatchLoadedForChunk(int cx, int cy, int cz)
        {
            var (bx, bz) = BatchKeyFromChunk(cx, cz);
            if (loadedBatches.ContainsKey((bx, bz)))
            {
                return activeChunks.ContainsKey((cx, cy, cz)) || unbuiltChunks.ContainsKey((cx, cy, cz)) || passiveChunks.ContainsKey((cx, cy, cz));
            }
            // Attempt to load batch file
            var chunk = LoadBatchForChunk(cx, cy, cz); // will populate batch + dictionaries if file exists
            return chunk != null;
        }

        // Schedules mesh builds for all chunks in a batch that fall inside the current active LoD radius.
        private void ScheduleVisibleChunksInBatch(int bx, int bz)
        {
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
    }
}

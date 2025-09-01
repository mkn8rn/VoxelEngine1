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

        // Track chunks marked dirty (needing rebuild) so we can coalesce multiple requests before building
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> dirtyChunks = new();

        // Track chunks that have been cancelled (scheduled then later deemed too far before gen/build)
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> cancelledChunks = new();

        // Cancellation token sources per pipeline (scheduling kept separate so we can recycle gen/build workers)
        private CancellationTokenSource schedulingCts;          // drives scheduling worker lifetime
        private CancellationTokenSource generationCts;          // drives current generation worker set
        private CancellationTokenSource meshBuildCts;           // drives current mesh build worker set

        // Cache heightmaps for (baseX, baseZ) columns across vertical stacks
        private readonly ConcurrentDictionary<(int baseX, int baseZ), float[,]> heightmapCache = new();

        // World block accessor delegates
        private Func<int, int, int, ushort> worldBlockAccessor;

        // Asynchronous scheduling pipeline
        private int chunkScheduleWorkerCount = 1; 
        private Task[] schedulingWorkers;

        // Asynchronous generation pipeline (current active workers)
        private int generationWorkerCount; // current (may change after staged init)
        private Task[] generationWorkers;
        private BlockingCollection<Vector3> chunkPositionQueue; // gen tasks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> chunkGenSchedule = new(); // track enqueued but not yet generated

        // Asynchronous mesh build pipeline
        private int meshBuildWorkerCount; // current (may change after staged init)
        private Task[] meshBuildWorkers;
        private BlockingCollection<(int cx,int cy,int cz)> meshBuildQueue; // build tasks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> meshBuildSchedule = new(); // track chunks scheduled for build

        // Player current chunk position (external systems can set this). For now not wired to real player.
        private volatile int playerChunkX;
        private volatile int playerChunkY;
        private volatile int playerChunkZ;
        public (int cx, int cy, int cz) PlayerChunkPosition
        {
            get => (playerChunkX, playerChunkY, playerChunkZ);
            set { playerChunkX = value.cx; playerChunkY = value.cy; playerChunkZ = value.cz; }
        }

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
            meshBuildQueue = new BlockingCollection<(int cx, int cy, int cz)>(new ConcurrentQueue<(int, int, int)>());
            schedulingCts = new CancellationTokenSource();
            Console.WriteLine("World resources initialized.");

            bool streamGeneration = FlagManager.flags.renderStreamingIfAllowed ?? throw new InvalidOperationException("Render streaming flag is not set.");

            Console.WriteLine($"Initializing region: {RegionID}");

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
                WaitForInitialChunkGeneration();
                StopGenerationWorkers();

                // 2. Initial mesh build workers (after gen complete)
                StartMeshBuildWorkers(initialMesh);
                // Chunks were already enqueued for build during generation
                WaitForInitialChunkRenderBuild();
                StopMeshBuildWorkers();

                // 3. Start steady-state workers (may be same counts; restart for clarity per spec)
                StartGenerationWorkers(finalGen);
                StartMeshBuildWorkers(finalMesh);
            }
            else
            {
                // Streaming mode: single steady-state startup with final counts.
                int finalGen = (int)(FlagManager.flags.worldGenWorkersPerCore.Value * proc);
                int finalMesh = (int)(FlagManager.flags.meshRenderWorkersPerCore.Value * proc);
                StartGenerationWorkers(finalGen);
                StartMeshBuildWorkers(finalMesh);
                EnqueueInitialChunkPositions();
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

        // -------------------- Existing waits (unchanged logic) --------------------
        private void WaitForInitialChunkGeneration()
        {
            Console.WriteLine("[World] Waiting for initial chunk generation...");
            var sw = Stopwatch.StartNew();

            int done = unbuiltChunks.Count;
            int remaining = chunkGenSchedule.Count;
            int total = chunkGenSchedule.Count + done;

            while (chunkGenSchedule.Count > 0)
            {
                done = unbuiltChunks.Count;
                remaining = chunkGenSchedule.Count;

                Console.WriteLine($"[World] Initial chunk generation: {done}/{total}, remaining: {remaining}");
                Thread.Sleep(500);
            }
            sw.Stop();
            Console.WriteLine($"[World] Initial chunk generation complete: {total} chunks in {sw.ElapsedMilliseconds} ms.");
        }

        private void WaitForInitialChunkRenderBuild()
        {
            Console.WriteLine("[World] Building chunk meshes asynchronously...");
            var sw = Stopwatch.StartNew();
            int initialTotal = unbuiltChunks.Count; // number of chunks scheduled for initial mesh builds
            while (meshBuildSchedule.Count > 0 || unbuiltChunks.Count > 0) // wait until all scheduled builds complete
            {
                int remaining = unbuiltChunks.Count;
                int built = initialTotal - remaining;
                Console.WriteLine($"[World] Chunk mesh build: {built}/{initialTotal}, remaining: {remaining}");
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

        private void EnqueueInitialChunkPositions()
        {
            int lodDist = GameManager.settings.lod1RenderDistance;
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int verticalRows = lodDist; // current behavior

            int count = 0; // track how many initial chunks we enqueue

            // For each increasing radius ring, enqueue entire vertical stack for each (x,z) column before moving on.
            for (int radius = 0; radius < lodDist; radius++)
            {
                if (radius == 0)
                {
                    // Center column vertical stack
                    for (int vy = 0; vy < verticalRows; vy++)
                    {
                        EnqueueChunkPosition(0, vy * sizeY, 0);
                        count++;
                    }
                    continue;
                }
                int min = -radius;
                int max = radius;
                for (int x = min; x <= max; x++)
                {
                    for (int z = min; z <= max; z++)
                    {
                        // Only perimeter cells (ring)
                        if (Math.Abs(x) != radius && Math.Abs(z) != radius) continue;
                        for (int vy = 0; vy < verticalRows; vy++)
                        {
                            EnqueueChunkPosition(x * sizeX, vy * sizeY, z * sizeZ);
                            count++;
                        }
                    }
                }
            }

            Console.WriteLine($"[World] Scheduled {count} initial chunks.");
        }

        private void EnqueueUnbuiltChunksForBuild()
        {
            foreach (var key in unbuiltChunks.Keys)
            {
                EnqueueMeshBuild(key, markDirty:false); // initial builds are not dirty rebuilds
            }
        }

        private void EnqueueChunkPosition(int worldX, int worldY, int worldZ, bool force = false)
        {
            var key = ChunkIndexKey(worldX, worldY, worldZ);
            // Region boundary check (inclusive): disallow if any chunk coordinate magnitude exceeds regionWidthInChunks
            long regionLimit = GameManager.settings.regionWidthInChunks; // allowed range: -regionLimit .. +regionLimit
            if (Math.Abs(key.cx) > regionLimit || Math.Abs(key.cy) > regionLimit || Math.Abs(key.cz) > regionLimit)
            {
                return; // outside configured world region
            }
            if (!force)
            {
                if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key)) return; // already generated or built
                if (!chunkGenSchedule.TryAdd(key, 0)) return; // already queued
            }
            else
            {
                chunkGenSchedule[key] = 0; // overwrite / ensure present
            }
            chunkPositionQueue.Add(new Vector3(worldX, worldY, worldZ));
        }

        private void ChunkGenerationWorker(CancellationToken token)
        {
            string chunkSaveDirectory = Path.Combine(loader.currentWorldSaveDirectory, loader.currentWorldSavedChunksSubDirectory);
            try
            {
                foreach (var pos in chunkPositionQueue.GetConsumingEnumerable(token))
                {
                    if (token.IsCancellationRequested) break;

                    // quick discard if far / cancelled before doing any work
                    int sizeX = GameManager.settings.chunkMaxX;
                    int sizeY = GameManager.settings.chunkMaxY;
                    int sizeZ = GameManager.settings.chunkMaxZ;
                    int cx = (int)Math.Floor(pos.X / sizeX);
                    int cy = (int)Math.Floor(pos.Y / sizeY);
                    int cz = (int)Math.Floor(pos.Z / sizeZ);
                    var key = (cx, cy, cz);

                    long regionLimit = GameManager.settings.regionWidthInChunks;
                    if (Math.Abs(cx) > regionLimit || Math.Abs(cy) > regionLimit || Math.Abs(cz) > regionLimit)
                    {
                        chunkGenSchedule.TryRemove(key, out _); // outside region
                        continue;
                    }

                    int lodDist = GameManager.settings.lod1RenderDistance;
                    int verticalRange = lodDist; // symmetric
                    if (cancelledChunks.ContainsKey(key) || Math.Abs(cx - playerChunkX) >= lodDist || Math.Abs(cz - playerChunkZ) >= lodDist || Math.Abs(cy - playerChunkY) > verticalRange)
                    {
                        chunkGenSchedule.TryRemove(key, out _);
                        cancelledChunks.TryRemove(key, out _);
                        continue; // skip generation
                    }

                    // Heightmap reuse per (x,z) across vertical stack
                    int baseX = (int)pos.X;
                    int baseZ = (int)pos.Z;
                    var hmKey = (baseX, baseZ);
                    float[,] heightmap = heightmapCache.GetOrAdd(hmKey, _ => Chunk.GenerateHeightMap(loader.seed, baseX, baseZ));

                    var chunk = new Chunk(pos, loader.seed, Path.Combine(loader.currentWorldSaveDirectory, loader.currentWorldSavedChunksSubDirectory), heightmap);
                    unbuiltChunks[key] = chunk;

                    // Persist immediately after generation
                    SaveChunkToFile(chunk);

                    chunkGenSchedule.TryRemove(key, out _);

                    // Enqueue self for initial mesh build
                    EnqueueMeshBuild(key, markDirty:false);
                    // Mark neighbors so they can rebuild to hide now occluded faces (only if boundary overlap has solids)
                    MarkNeighborsDirty(key, chunk);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("Chunk generation worker error: " + ex.Message);
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

        private void MarkNeighborsDirty((int cx, int cy, int cz) key, Chunk newChunk)
        {
            foreach (var dir in NeighborDirs)
            {
                var nk = (key.cx + dir.dx, key.cy + dir.dy, key.cz + dir.dz);
                // Only consider already present neighbor chunks
                bool neighborExists = unbuiltChunks.ContainsKey(nk) || activeChunks.ContainsKey(nk);
                if (!neighborExists) continue;

                if (!HasAnySolidOnBoundary(newChunk, dir)) continue;

                // Mark dirty & enqueue (coalesce multiple marks by using dirtyChunks)
                if (dirtyChunks.TryAdd(nk, 0))
                {
                    EnqueueMeshBuild(nk, markDirty:true);
                }
            }
        }

        private void EnqueueMeshBuild((int cx,int cy,int cz) key, bool markDirty = true)
        {
            if (!unbuiltChunks.ContainsKey(key) && !activeChunks.ContainsKey(key)) return;
            if (markDirty)
            {
                // Ensure dirty flag exists (idempotent)
                dirtyChunks.TryAdd(key, 0);
            }
            if (meshBuildSchedule.TryAdd(key, 0))
            {
                meshBuildQueue.Add(key);
            }
        }

        private void MeshBuildWorker(CancellationToken token)
        {
            try
            {
                foreach (var key in meshBuildQueue.GetConsumingEnumerable(token))
                {
                    if (token.IsCancellationRequested) break;

                    // distance cull before building
                    int lodDist = GameManager.settings.lod1RenderDistance;
                    int verticalRange = lodDist;
                    if (Math.Abs(key.cx - playerChunkX) >= lodDist || Math.Abs(key.cz - playerChunkZ) >= lodDist || Math.Abs(key.cy - playerChunkY) > verticalRange)
                    {
                        // remove any stale data
                        unbuiltChunks.TryRemove(key, out _);
                        meshBuildSchedule.TryRemove(key, out _);
                        dirtyChunks.TryRemove(key, out _);
                        continue;
                    }

                    meshBuildSchedule.TryRemove(key, out _);

                    // Acquire chunk from either dictionary
                    if (!unbuiltChunks.TryGetValue(key, out var ch))
                    {
                        if (!activeChunks.TryGetValue(key, out ch)) continue; // disappeared
                    }

                    try
                    {
                        // attempt neighbor-based burial classification
                        TryMarkBuriedByNeighbors(key, ch);

                        PopulateNeighborFaceFlags(key, ch); // <-- New call to populate neighbor face flags

                        ch.BuildRender(worldBlockAccessor);

                        // If this was first-time build (in unbuilt), move to built dictionary
                        if (unbuiltChunks.TryRemove(key, out var builtChunk))
                        {
                            activeChunks[key] = builtChunk;
                        }

                        // Clear dirty flag after successful build
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
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[World] Chunk scheduling error: {ex.Message}");
                    }
                }
                Thread.Sleep(sleepMs);
            }
        }

        private void ScheduleChunksAroundPlayer(int centerCx, int centerCy, int centerCz)
        {
            int lodDist = GameManager.settings.lod1RenderDistance;
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int verticalRows = lodDist; // use same distance vertically for symmetry

            // Define symmetric vertical range around player (includes center layer once)
            int vMin = -verticalRows; // below
            int vMax = verticalRows;  // above

            for (int radius = 0; radius < lodDist; radius++)
            {
                if (radius == 0)
                {
                    for (int vy = vMin; vy <= vMax; vy++)
                    {
                        int cy = centerCy + vy;
                        EnqueueChunkPosition(centerCx * sizeX, cy * sizeY, centerCz * sizeZ);
                    }
                    continue;
                }
                int min = -radius;
                int max = radius;
                for (int dx = min; dx <= max; dx++)
                {
                    for (int dz = min; dz <= max; dz++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue; // ring perimeter
                        for (int vy = vMin; vy <= vMax; vy++)
                        {
                            int cy = centerCy + vy;
                            int wx = (centerCx + dx) * sizeX;
                            int wy = cy * sizeY;
                            int wz = (centerCz + dz) * sizeZ;
                            EnqueueChunkPosition(wx, wy, wz);
                        }
                    }
                }
            }
        }

        // Unload chunks (active or unbuilt) outside of current interest radius.
        private void UnloadFarChunks(int centerCx, int centerCy, int centerCz)
        {
            int lodDist = GameManager.settings.lod1RenderDistance;
            int verticalRange = lodDist;

            // Active chunks
            foreach (var key in activeChunks.Keys.ToArray())
            {
                if (Math.Abs(key.cx - centerCx) >= lodDist || Math.Abs(key.cz - centerCz) >= lodDist || Math.Abs(key.cy - centerCy) > verticalRange)
                {
                    if (activeChunks.TryRemove(key, out var chunk))
                    {
                        chunk.chunkRender?.ScheduleDelete();
                        dirtyChunks.TryRemove(key, out _);
                        meshBuildSchedule.TryRemove(key, out _);
                    }
                }
            }
            // Unbuilt chunks (cancel generation / building if far)
            foreach (var key in unbuiltChunks.Keys.ToArray())
            {
                if (Math.Abs(key.cx - centerCx) >= lodDist || Math.Abs(key.cz - centerCz) >= lodDist || Math.Abs(key.cy - centerCy) > verticalRange)
                {
                    if (unbuiltChunks.TryRemove(key, out _))
                    {
                        cancelledChunks[key] = 0; // mark so generation worker discards if not yet processed
                        chunkGenSchedule.TryRemove(key, out _);
                        meshBuildSchedule.TryRemove(key, out _);
                        dirtyChunks.TryRemove(key, out _);
                    }
                }
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
                schedulingCts?.Dispose();
                generationCts?.Dispose();
                meshBuildCts?.Dispose();
                chunkPositionQueue?.Dispose();
                meshBuildQueue?.Dispose();
            }
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
        private void TryMarkBuriedByNeighbors((int cx,int cy,int cz) key, Chunk ch)
        {
            // Only perform on initial build attempt; if chunk already active we skip.
            if (!unbuiltChunks.ContainsKey(key)) return;

            // We require all six neighbors to be present (either generated but unbuilt, or already active).
            static bool GetChunk((int cx,int cy,int cz) k,
                                 ConcurrentDictionary<(int,int,int), Chunk> active,
                                 ConcurrentDictionary<(int,int,int), Chunk> pending,
                                 out Chunk result)
            {
                if (active.TryGetValue(k, out result)) return true;
                if (pending.TryGetValue(k, out result)) return true;
                result = null; return false;
            }

            var leftKey  = (key.cx - 1, key.cy, key.cz);
            var rightKey = (key.cx + 1, key.cy, key.cz);
            var downKey  = (key.cx, key.cy - 1, key.cz);
            var upKey    = (key.cx, key.cy + 1, key.cz);
            var backKey  = (key.cx, key.cy, key.cz - 1); // negative Z
            var frontKey = (key.cx, key.cy, key.cz + 1); // positive Z

            if (!GetChunk(leftKey,  activeChunks, unbuiltChunks, out var left )) return;
            if (!GetChunk(rightKey, activeChunks, unbuiltChunks, out var right)) return;
            if (!GetChunk(downKey,  activeChunks, unbuiltChunks, out var down )) return;
            if (!GetChunk(upKey,    activeChunks, unbuiltChunks, out var up   )) return;
            if (!GetChunk(backKey,  activeChunks, unbuiltChunks, out var back )) return;
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
    }
}

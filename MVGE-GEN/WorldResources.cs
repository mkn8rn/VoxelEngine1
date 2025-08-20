using MVGE_GFX;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
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

namespace MVGE_GEN
{
    public partial class WorldResources : IDisposable
    {
        public WorldLoader loader { get; private set; }

        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> activeChunks = new(); // track ready to render chunks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> unbuiltChunks = new(); // track generated but not yet built chunks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> pendingChunks = new(); // track enqueued but not yet generated

        // Track chunks marked dirty (needing rebuild) so we can coalesce multiple requests before building
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> dirtyChunks = new();

        // Cancellation token for streaming operations
        private CancellationTokenSource streamingCts;

        // Asynchronous generation pipeline
        private int generationWorkerCount;
        private Task[] generationWorkers;
        private BlockingCollection<Vector3> chunkPositionQueue; // gen tasks

        // Cache heightmaps for (baseX, baseZ) columns across vertical stacks
        private readonly ConcurrentDictionary<(int baseX,int baseZ), float[,]> heightmapCache = new();

        // World block accessor delegates
        private Func<int, int, int, ushort> worldBlockAccessor;

        // Asynchronous mesh build pipeline
        private int meshBuildWorkerCount;
        private Task[] meshBuildWorkers;
        private BlockingCollection<(int cx,int cy,int cz)> meshBuildQueue; // build tasks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> meshBuildSchedule = new(); // de-dupe keys in build queue
        
        public WorldResources()
        {
            Console.WriteLine("World manager initializing.");

            float proc = Environment.ProcessorCount;
            if (FlagManager.flags.worldGenWorkersPerCore is not null)
            {
                generationWorkerCount = (int)(FlagManager.flags.worldGenWorkersPerCore.Value * proc);
            }
            else
            {
                generationWorkerCount = (int)proc;
            }
            if (FlagManager.flags.meshRenderWorkersPerCore is not null)
            {
                meshBuildWorkerCount = (int)(FlagManager.flags.meshRenderWorkersPerCore.Value * proc);
            }
            else
            {
                meshBuildWorkerCount = (int)proc * 2;
            }

            loader = new WorldLoader();
            loader.ChooseWorld();
            Console.WriteLine("World data loaded.");

            streamingCts = new CancellationTokenSource();

            bool streamGeneration = false;
            if (FlagManager.flags.renderStreamingIfAllowed is not null)
            {
                streamGeneration = FlagManager.flags.renderStreamingIfAllowed.Value;
            }

            InitializeGeneration(streamGeneration);
            if(streamGeneration == false) WaitForInitialChunkGeneration();

            InitializeBuilding(streamGeneration);
            if (streamGeneration == false) WaitForInitialChunkRenderBuild();
        }

        private void WaitForInitialChunkGeneration()
        {
            Console.WriteLine("[World] Waiting for initial chunk generation...");
            var sw = Stopwatch.StartNew();

            int done = unbuiltChunks.Count;
            int remaining = pendingChunks.Count;
            int total = pendingChunks.Count + done;

            while (pendingChunks.Count > 0)
            {
                done = unbuiltChunks.Count;
                remaining = pendingChunks.Count;

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

        private void InitializeGeneration(bool streamGeneration)
        {
            worldBlockAccessor = GetBlock;
            chunkPositionQueue = new BlockingCollection<Vector3>(new ConcurrentQueue<Vector3>());

            EnqueueInitialChunkPositions();

            Console.WriteLine("[World] Initializing world generation workers...");
            int proc = Environment.ProcessorCount;
            generationWorkers = new Task[generationWorkerCount];
            for (int i = 0; i < generationWorkerCount; i++)
            {
                generationWorkers[i] = Task.Run(() => ChunkGenerationWorker(streamingCts.Token, streamGeneration));
            }
            Console.WriteLine($"[World] Initialized {generationWorkers.Count()} world generation workers.");
        }

        private void InitializeBuilding(bool streamGeneration)
        {
            meshBuildQueue = new BlockingCollection<(int cx,int cy,int cz)>(new ConcurrentQueue<(int,int,int)>());

            if(streamGeneration == false)
            {
                EnqueueUnbuiltChunksForBuild();
            }

            Console.WriteLine("[World] Initializing mesh build workers...");
            meshBuildWorkers = new Task[meshBuildWorkerCount];
            for (int i = 0; i < meshBuildWorkerCount; i++)
            {
                meshBuildWorkers[i] = Task.Run(() => MeshBuildWorker(streamingCts.Token));
            }
            Console.WriteLine($"[World] Initialized {meshBuildWorkers.Count()} mesh build workers.");
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
            if (!force)
            {
                if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key)) return; // already generated or built
                if (!pendingChunks.TryAdd(key, 0)) return; // already queued
            }
            else
            {
                pendingChunks[key] = 0; // overwrite / ensure present
            }
            chunkPositionQueue.Add(new Vector3(worldX, worldY, worldZ));
        }

        private void ChunkGenerationWorker(CancellationToken token, bool streamGeneration = false)
        {
            string chunkSaveDirectory = Path.Combine(loader.currentWorldSaveDirectory, loader.currentWorldSavedChunksSubDirectory);
            try
            {
                foreach (var pos in chunkPositionQueue.GetConsumingEnumerable(token))
                {
                    if (token.IsCancellationRequested) break;

                    // Heightmap reuse per (x,z) across vertical stack
                    int baseX = (int)pos.X;
                    int baseZ = (int)pos.Z;
                    var hmKey = (baseX, baseZ);
                    float[,] heightmap = heightmapCache.GetOrAdd(hmKey, _ => Chunk.GenerateHeightMap(loader.seed, baseX, baseZ));

                    var chunk = new Chunk(pos, loader.seed, Path.Combine(loader.currentWorldSaveDirectory, loader.currentWorldSavedChunksSubDirectory), heightmap);
                    var key = ChunkIndexKey((int)pos.X, (int)pos.Y, (int)pos.Z);
                    unbuiltChunks[key] = chunk;

                    pendingChunks.TryRemove(key, out _);

                    if(streamGeneration == true) // We don't need to rebuild meshes during initial generation
                    {
                        // Enqueue self for initial mesh build
                        EnqueueMeshBuild(key, markDirty:false);
                        // Mark neighbors so they can rebuild to hide now occluded faces (only if boundary overlap has solids)
                        MarkNeighborsDirty(key, chunk);
                    }
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

                    meshBuildSchedule.TryRemove(key, out _);

                    // Acquire chunk from either dictionary
                    if (!unbuiltChunks.TryGetValue(key, out var ch))
                    {
                        if (!activeChunks.TryGetValue(key, out ch)) continue; // disappeared
                    }

                    try
                    {
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

        public void Dispose()
        {
            try
            {
                streamingCts?.Cancel();
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
            }
            catch { }
            finally
            {
                streamingCts?.Dispose();
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
    }
}

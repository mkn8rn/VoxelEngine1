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
using World;
using System.Diagnostics;
using MVGE_INF.Loaders;

namespace MVGE.World
{
    public enum RenderDetail
    {
        LoD1,
        LoD2,
        LoD3,
        LoD4,
        LoD5
    }

    internal class World : IDisposable
    {
        public WorldLoader loader { get; private set; }

        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> chunks = new(); // track generated chunks
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> pendingChunks = new(); // track enqueued but not yet generated

        // Streaming pipeline structures
        private BlockingCollection<Vector3> chunkPositionQueue;
        private CancellationTokenSource streamingCts;
        private Task[] generationWorkers;
        private int workerCount;
        private Func<int, int, int, ushort> worldBlockAccessor;

        // Cache heightmaps for (baseX, baseZ) columns across vertical stacks
        private readonly ConcurrentDictionary<(int baseX,int baseZ), float[,]> heightmapCache = new();

        // for tracking which chunks need mesh updates
        private readonly ConcurrentQueue<(int cx, int cy, int cz)> dirtyMeshQueue = new();
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> dirtyMeshSet = new();

        public World()
        {
            Console.WriteLine("World manager initializing.");

            loader = new WorldLoader();
            loader.ChooseWorld();
            Console.WriteLine("World data loaded.");

            InitializeStreaming();
            Console.WriteLine("Streaming chunk generation...");

            WaitForInitialChunkGeneration();
            WaitForInitialChunkRenderBuild();
        }

        private void WaitForInitialChunkGeneration()
        {
            Console.WriteLine("[World] Waiting for initial chunk generation...");
            var sw = Stopwatch.StartNew();
            int lastPercent = -1;

            int done = chunks.Count;
            int remaining = pendingChunks.Count;
            int total = pendingChunks.Count + done;
            int percent = (int)(done * 100L / total);

            while (pendingChunks.Count > 0)
            {
                done = chunks.Count;
                remaining = pendingChunks.Count;
                percent = (int)(done * 100L / total);

                if (percent != lastPercent)
                {
                    Console.WriteLine($"[World] Initial chunk generation: {done}/{total} ({percent}%), remaining: {remaining}");
                    lastPercent = percent;
                }
                Thread.Sleep(200);
            }
            sw.Stop();
            Console.WriteLine($"[World] Initial chunk generation complete: {total} chunks in {sw.ElapsedMilliseconds} ms.");
        }

        private void WaitForInitialChunkRenderBuild()
        {
            Console.WriteLine("[World] Building chunk meshes...");
            var sw = Stopwatch.StartNew();
            int initialTotal = dirtyMeshSet.Count;
            int lastPercent = -1;
            while (dirtyMeshSet.Count > 0)
            {
                int remaining = dirtyMeshSet.Count;
                int done = initialTotal - remaining;
                int percent = done >= initialTotal ? 100 : (int)(done * 100L / initialTotal);
                if (percent != lastPercent)
                {
                    Console.WriteLine($"[World] Chunk mesh build: {done}/{initialTotal} ({percent}%)");
                    lastPercent = percent;
                }
                BuildChunks();
                Thread.Sleep(200);
            }
            sw.Stop();
            Console.WriteLine($"[World] Chunk mesh build complete in {sw.ElapsedMilliseconds} ms.");
        }

        public void Render(ShaderProgram program)
        {
            BuildChunks();
            foreach (var chunk in chunks.Values) chunk.Render(program);
        }

        private void InitializeStreaming()
        {
            worldBlockAccessor = GetBlock;

            // Prepare queue & cancellation
            streamingCts = new CancellationTokenSource();
            chunkPositionQueue = new BlockingCollection<Vector3>(new ConcurrentQueue<Vector3>());

            // Enqueue initial target chunk positions (spiral ordering) non-blocking
            EnqueueInitialChunkPositions();

            // Worker count: leave 1 core for render thread if possible
            workerCount = Math.Max(1, Environment.ProcessorCount - 1);
            generationWorkers = new Task[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                generationWorkers[i] = Task.Run(() => ChunkGenerationWorker(streamingCts.Token));
            }
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

        private void EnqueueChunkPosition(int worldX, int worldY, int worldZ, bool force = false)
        { // force is for priority, like prioritising chunks in camera view
            var key = ChunkIndexKey(worldX, worldY, worldZ);
            if (!force)
            {
                if (chunks.ContainsKey(key)) return; // already generated
                if (!pendingChunks.TryAdd(key, 0)) return; // already queued
            }
            else
            {
                pendingChunks[key] = 0; // overwrite / ensure present
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

                    // Heightmap reuse per (x,z) across vertical stack
                    int baseX = (int)pos.X;
                    int baseZ = (int)pos.Z;
                    var hmKey = (baseX, baseZ);
                    float[,] heightmap = heightmapCache.GetOrAdd(hmKey, _ => Chunk.GenerateHeightMap(loader.seed, baseX, baseZ));

                    var chunk = new Chunk(pos, loader.seed, Path.Combine(loader.currentWorldSaveDirectory, loader.currentWorldSavedChunksSubDirectory), heightmap);
                    var key = ChunkIndexKey((int)pos.X, (int)pos.Y, (int)pos.Z);
                    chunks[key] = chunk;

                    // Reminder to self: 
                    // Removed the buildrender call, re-add it somewhere else
                    /*
                    lock (chunk)
                    {
                        chunk.BuildRender(worldBlockAccessor);
                    }*/

                    // Remove from pending since generation complete
                    pendingChunks.TryRemove(key, out _);

                    MarkNeighborsDirty(key);
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

        private void MarkNeighborsDirty((int cx, int cy, int cz) key)
        {
            foreach (var (dx, dy, dz) in NeighborDirs)
            {
                var nk = (key.cx + dx, key.cy + dy, key.cz + dz);
                if (chunks.ContainsKey(nk))
                {
                    if (dirtyMeshSet.TryAdd(nk, 0))
                        dirtyMeshQueue.Enqueue(nk);
                }
            }
        }

        private void BuildChunks()
        {
            while (dirtyMeshQueue.TryDequeue(out var key))
            {
                dirtyMeshSet.TryRemove(key, out _);
                if (chunks.TryGetValue(key, out var ch))
                {
                    lock (ch)
                    {
                        ch.BuildRender(worldBlockAccessor);
                    }
                }
            }
        }

        public void Dispose()
        {
            try
            {
                streamingCts?.Cancel();
                chunkPositionQueue?.CompleteAdding();
                if (generationWorkers != null)
                {
                    Task.WaitAll(generationWorkers, TimeSpan.FromSeconds(2));
                }
            }
            catch { }
            finally
            {
                streamingCts?.Dispose();
                chunkPositionQueue?.Dispose();
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
            if (!chunks.TryGetValue(key, out var chunk))
            {
                return (ushort)BaseBlockType.Empty;
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

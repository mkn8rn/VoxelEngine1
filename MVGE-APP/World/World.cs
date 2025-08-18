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
        public Guid ID;
        public string worldName;

        private readonly ConcurrentDictionary<(int cx, int cy, int cz), Chunk> chunks = new();
        private readonly ConcurrentDictionary<(int cx, int cy, int cz), byte> pendingChunks = new(); // track enqueued but not yet generated
        private readonly ConcurrentDictionary<(int cx,int cy,int cz), byte> dirtyMeshes = new();

        private int seed;
        private string currentWorldSaveDirectory;
        private string currentWorldDataFile = "world.txt";
        private string currentWorldSavedChunksSubDirectory = "chunks";
        private static Dictionary<Guid, string> worldSaves = new Dictionary<Guid, string>();

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

            ChooseWorld();
            Console.WriteLine("World manager loaded.");

            InitializeStreaming();
            Console.WriteLine("Streaming chunk generation...");
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

            // For each increasing radius ring, enqueue entire vertical stack for each (x,z) column before moving on.
            for (int radius = 0; radius < lodDist; radius++)
            {
                if (radius == 0)
                {
                    // Center column vertical stack
                    for (int vy = 0; vy < verticalRows; vy++)
                    {
                        EnqueueChunkPosition(0, vy * sizeY, 0);
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
                        }
                    }
                }
            }
        }

        private void EnqueueChunkPosition(int worldX, int worldY, int worldZ, bool force = false) // force for priority
        {
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

        // Public API to request additional rings dynamically (e.g. player moved)
        public void RequestRingsAround(Vector3 playerPosition, int additionalRadius)
        {
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int verticalRows = GameManager.settings.lod1RenderDistance; // maintain vertical stack count

            // Determine current center chunk indices
            int centerCX = FloorDiv((int)playerPosition.X, sizeX);
            int centerCZ = FloorDiv((int)playerPosition.Z, sizeZ);
            int baseY = 0; // assuming ground start (could adapt to player Y later)

            for (int radius = 0; radius <= additionalRadius; radius++)
            {
                if (radius == 0)
                {
                    for (int vy = 0; vy < verticalRows; vy++)
                    {
                        int wy = vy * sizeY + baseY;
                        EnqueueChunkPosition(centerCX * sizeX, wy, centerCZ * sizeZ);
                    }
                    continue;
                }
                int minX = centerCX - radius;
                int maxX = centerCX + radius;
                int minZ = centerCZ - radius;
                int maxZ = centerCZ + radius;
                for (int cx = minX; cx <= maxX; cx++)
                {
                    for (int cz = minZ; cz <= maxZ; cz++)
                    {
                        if (Math.Abs(cx - centerCX) != radius && Math.Abs(cz - centerCZ) != radius) continue;
                        for (int vy = 0; vy < verticalRows; vy++)
                        {
                            int wy = vy * sizeY + baseY;
                            EnqueueChunkPosition(cx * sizeX, wy, cz * sizeZ);
                        }
                    }
                }
            }
        }

        private void ChunkGenerationWorker(CancellationToken token)
        {
            string chunkSaveDirectory = Path.Combine(currentWorldSaveDirectory, currentWorldSavedChunksSubDirectory);
            try
            {
                foreach (var pos in chunkPositionQueue.GetConsumingEnumerable(token))
                {
                    if (token.IsCancellationRequested) break;

                    // Heightmap reuse per (x,z) across vertical stack
                    int baseX = (int)pos.X;
                    int baseZ = (int)pos.Z;
                    var hmKey = (baseX, baseZ);
                    float[,] heightmap = heightmapCache.GetOrAdd(hmKey, _ => Chunk.GenerateHeightMap(seed, baseX, baseZ));

                    var chunk = new Chunk(pos, seed, chunkSaveDirectory, heightmap);
                    var key = ChunkIndexKey((int)pos.X, (int)pos.Y, (int)pos.Z);
                    chunks[key] = chunk;

                    // Build mesh CPU-side (face extraction) now (OpenGL build deferred until render)
                    lock (chunk)
                    {
                        chunk.BuildRender(worldBlockAccessor);
                    }

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
            if (dirtyMeshSet.TryAdd(key, 0))
                dirtyMeshQueue.Enqueue(key);
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

        private void ChooseWorld()
        {
            DetectWorldSaves();

            if (worldSaves.Count == 0)
            {
                GenerateWorldSave();
                return;
            }

            Console.WriteLine("Please select world: ");
            Console.WriteLine("0. Generate brand new world");

            List<string> worldSaveNames = worldSaves.Values.ToList();

            for (int i = 0; i < worldSaveNames.Count; i++)
            {
                Console.WriteLine(i + 1 + ". " + worldSaveNames[i]);
            }

            string input = Console.ReadLine();

            if (int.TryParse(input, out int selectedWorldIndex))
            {
                if (selectedWorldIndex == 0)
                {
                    GenerateWorldSave();
                }
                else if (selectedWorldIndex >= 1 && selectedWorldIndex <= worldSaves.Count)
                {
                    string selectedWorld = worldSaveNames[selectedWorldIndex - 1];
                    Guid selectedWorldId = worldSaves.FirstOrDefault(x => x.Value == selectedWorld).Key;
                    LoadWorldSave(selectedWorldId);
                }
                else
                {
                    Console.WriteLine("Invalid input. Please select a valid world.");
                }
            }
            else
            {
                Console.WriteLine("Invalid input. Please enter a valid number.");
            }
        }

        public void GenerateWorldSave()
        {
            if (string.IsNullOrWhiteSpace(worldName))
            {
                GetWorldName();
            }

            if (seed == 0)
            {
                GetWorldSeed();
            }

            if (ID == Guid.Empty)
            {
                ID = Guid.NewGuid();
            }

            Console.WriteLine($"Generating world {worldName} with seed: {seed}, id: {ID}");

            currentWorldSaveDirectory = Path.Combine(GameManager.settings.savesWorldDirectory, ID.ToString());
            Directory.CreateDirectory(currentWorldSaveDirectory);

            string worldDataPath = Path.Combine(currentWorldSaveDirectory, currentWorldDataFile);
            using (StreamWriter writer = new StreamWriter(worldDataPath))
            {
                writer.WriteLine(ID.ToString());
                writer.WriteLine(worldName);
                writer.WriteLine(seed);
            }

            string chunkSaveFolderPath = Path.Combine(currentWorldSaveDirectory, currentWorldSavedChunksSubDirectory);
            Directory.CreateDirectory(chunkSaveFolderPath);
        }

        public void DetectWorldSaves()
        {
            string[] directories = Directory.GetDirectories(GameManager.settings.savesWorldDirectory);
            foreach (string directory in directories)
            {
                string[] files = Directory.GetFiles(directory);
                foreach (string file in files)
                {
                    if (file.Contains(currentWorldDataFile))
                    {
                        string[] lines = File.ReadAllLines(file);
                        if (lines.Length >= 2)
                        {
                            var id = Guid.Parse(lines[0]);
                            var wn = lines[1];
                            if (!worldSaves.ContainsKey(id))
                            {
                                worldSaves.Add(id, wn);
                                Console.WriteLine("Detected world save: " + wn + ", id: " + id);
                            }
                        }
                    }
                }
            }
        }

        public void LoadWorldSave(Guid id)
        {
            ID = id;
            currentWorldSaveDirectory = Path.Combine(GameManager.settings.savesWorldDirectory, id.ToString());
            string worldDataPath = Path.Combine(currentWorldSaveDirectory, currentWorldDataFile);
            string[] lines = File.ReadAllLines(worldDataPath);

            if (lines.Length >= 3)
            {
                worldName = lines[1];
                seed = int.Parse(lines[2]);
            }

            Console.WriteLine($"Loaded world save: {worldName}, id: {id}, seed: {seed}");
        }

        public void Render(ShaderProgram program)
        {
            if (!dirtyMeshes.IsEmpty)
            {
                foreach (var kv in dirtyMeshes.Keys)
                {
                    if (chunks.TryGetValue(kv, out var ch))
                    {
                        lock (ch) ch.BuildRender(worldBlockAccessor);
                    }
                    dirtyMeshes.TryRemove(kv, out _);
                }
            }
            foreach (var chunk in chunks.Values) chunk.Render(program);
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

        private bool IsLatinAlphabet(string input)
        {
            foreach (char c in input)
            {
                if (!char.IsLetter(c))
                {
                    return false;
                }
            }
            return true;
        }

        public void GetWorldName()
        {
            while (true)
            {
                Console.WriteLine("Please enter a name for the world: ");
                string input = Console.ReadLine();
                if (IsLatinAlphabet(input))
                {
                    if (worldSaves.Values.Contains(input))
                    {
                        Console.WriteLine("Invalid input. The world name is already taken. Please enter a different name.");
                    }
                    else
                    {
                        worldName = input;
                        break;
                    }
                }
                else
                {
                    Console.WriteLine("Invalid input. Please enter a name with only Latin alphabet characters.");
                }
            }
        }

        private void GetWorldSeed()
        {
            Console.WriteLine("Please enter a seed for the world: ");
            string input = Console.ReadLine();

            while (!int.TryParse(input, out seed))
            {
                Console.WriteLine("Invalid input. Please enter a valid seed: ");
                input = Console.ReadLine();
            }
        }
    }
}

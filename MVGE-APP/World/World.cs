using MVGE_GFX;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    internal class World
    {
        public Guid ID;
        public string worldName;

        private readonly Dictionary<(int cx, int cy, int cz), Chunk> chunks = new();

        // Settings
        private int seed;

        // Paths
        private string currentWorldSaveDirectory;
        private string currentWorldDataFile = "world.txt";
        private string currentWorldSavedChunksSubDirectory = "chunks";

        private static Dictionary<Guid, string> worldSaves = new Dictionary<Guid, string>();

        public World()
        {
            Console.WriteLine("World manager initializing.");

            ChooseWorld();
            LoadChunks();

            Console.WriteLine("World manager loaded.");
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

        public void LoadChunks()
        {
            Console.WriteLine("Loading chunks.");

            string chunkSaveDirectory = Path.Combine(currentWorldSaveDirectory, currentWorldSavedChunksSubDirectory);

            int chunkDistance = (GameManager.settings.lod1RenderDistance - 1) * 2;
            int chunksToRenderHorizontalRow = (chunkDistance + 1) * (chunkDistance + 1);
            int verticalRows = GameManager.settings.lod1RenderDistance;

            int startX = -(GameManager.settings.lod1RenderDistance - 1) * GameManager.settings.chunkMaxX;
            int startZ = -(GameManager.settings.lod1RenderDistance - 1) * GameManager.settings.chunkMaxZ;
            int startY = 0;
            int currentX = startX;
            int currentZ = startZ;
            int currentY = startY;

            List<Task> tasks = new List<Task>();

            object locker = new object();

            for (int j = 0; j < verticalRows; j++)
            {
                for (int i = 0; i < chunksToRenderHorizontalRow; i++)
                {
                    if (i % (chunkDistance + 1) == 0 && i != 0)
                    {
                        currentZ += GameManager.settings.chunkMaxZ;
                        currentX = startX;
                    }

                    Vector3 chunkPosition = new Vector3(currentX, currentY, currentZ);

                    tasks.Add(Task.Run(() =>
                    {
                        Chunk chunk = new Chunk(chunkPosition, seed, chunkSaveDirectory);
                        var key = ChunkIndexKey((int)chunk.position.X, (int)chunk.position.Y, (int)chunk.position.Z);
                        lock (locker)
                        {
                            chunks[key] = chunk;
                        }
                    }));

                    currentX += GameManager.settings.chunkMaxX;
                }
                currentX = startX;
                currentZ = startZ;
                currentY += GameManager.settings.chunkMaxY;
            }

            Task.WaitAll(tasks.ToArray());

            BuildAllChunkMeshes();

            Console.WriteLine("Chunks loaded: " + chunks.Count);
        }

        private void BuildAllChunkMeshes()
        {
            Func<int, int, int, ushort> accessor = GetBlock;
            foreach (var kvp in chunks)
            {
                kvp.Value.BuildRender(accessor);
            }
        }

        public void Render(ShaderProgram program)
        {
            foreach (var chunk in chunks.Values)
            {
                chunk.Render(program);
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

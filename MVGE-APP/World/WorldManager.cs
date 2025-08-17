using MVGE.Graphics;
using MVGE.World.Terrain;
using MVGE_INF.Managers;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

    internal class WorldManager
    {
        public Guid ID;
        public string worldName;

        // Chunks
        private List<Chunk> chunks;

        // Settings
        private Int32 seed;

        // Subdirectories and paths
        private string currentWorldSaveDirectory;
        private string currentWorldDataFile = "world.txt";
        private string currentWorldSavedChunksSubDirectory = "chunks";

        // Existing world saves
        private static Dictionary<Guid, string> worldSaves = new Dictionary<Guid, string>();

        public WorldManager()
        {
            Console.WriteLine("World manager initializing.");

            chunks = new List<Chunk>();

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

            int selectedWorldIndex;
            if (int.TryParse(input, out selectedWorldIndex))
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
            if (worldName == null || worldName == "" || worldName.Length < 1)
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

            Console.WriteLine("Generating world " + worldName + " with seed: " + this.seed.ToString() + ", id: " + ID.ToString());

            this.currentWorldSaveDirectory = Path.Combine(GameManager.settings.savesWorldDirectory, ID.ToString());
            Directory.CreateDirectory(this.currentWorldSaveDirectory);

            string worldDataPath = Path.Combine(this.currentWorldSaveDirectory, this.currentWorldDataFile);
            using (StreamWriter writer = new StreamWriter(worldDataPath))
            {
                writer.WriteLine(ID.ToString());
                writer.WriteLine(worldName);
                writer.WriteLine(seed);
            }

            string chunkSaveFolderPath = Path.Combine(this.currentWorldSaveDirectory, this.currentWorldSavedChunksSubDirectory);
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
                            var worldName = lines[1];
                            worldSaves.Add(id, worldName);
                            Console.WriteLine("Detected world save: " + worldName + ", id: " + id);
                        }
                    }
                }
            }
        }

        public void LoadWorldSave(Guid id)
        {
            this.ID = id;
            this.currentWorldSaveDirectory = Path.Combine(GameManager.settings.savesWorldDirectory, id.ToString());
            string worldDataPath = Path.Combine(this.currentWorldSaveDirectory, this.currentWorldDataFile);
            string[] lines = File.ReadAllLines(worldDataPath);

            if (lines.Length >= 3)
            {
                this.worldName = lines[1];
                this.seed = Int32.Parse(lines[2]);
            }

            Console.WriteLine("Loaded world save: " + worldName + ", id: " + id + ", seed: " + seed);
        }

        public void LoadChunks()
        {
            Console.WriteLine("Loading chunks.");

            string chunkSaveDirectory = Path.Combine(this.currentWorldSaveDirectory, this.currentWorldSavedChunksSubDirectory);

            // Decided on this because I want renderDistance = 1 to only render 1 chunk
            // Just because it makes sense to me intuitively
            int chunkDistance = (GameManager.settings.lod1RenderDistance - 1) * 2;
            int chunksToRenderHorizontalRow = (chunkDistance + 1) * (chunkDistance + 1);
            int verticalRows = GameManager.settings.lod1RenderDistance;

            // Start at the bottom left corner of the render distance
            int startX = -(GameManager.settings.lod1RenderDistance - 1) * TerrainDataLoader.CHUNK_SIZE;
            int startZ = -(GameManager.settings.lod1RenderDistance - 1) * TerrainDataLoader.CHUNK_SIZE;
            int startY = 0;
            int currentX = startX;
            int currentZ = startZ;
            int currentY = startY;

            List<Task> tasks = new List<Task>();

            for (int j = 0; j < verticalRows; j++)
            {
                for (int i = 0; i < chunksToRenderHorizontalRow; i++)
                {
                    // if i is divisible by chunkDistance + 1, we are at the end of a row
                    if (i % (chunkDistance + 1) == 0 && i != 0)
                    {
                        currentZ += TerrainDataLoader.CHUNK_SIZE;
                        currentX = startX;
                    }

                    Vector3 chunkPosition = new Vector3(currentX, currentY, currentZ);
                    int chunkIndex = i; // Capture the current value of i

                    tasks.Add(Task.Run(() =>
                    {
                        Chunk chunk = new Chunk(chunkPosition, seed, chunkSaveDirectory);
                        lock (chunks)
                        {
                            chunks.Add(chunk);
                        }
                        //Console.WriteLine("Chunk " + (chunkIndex + 1) + "/" + chunksToRenderHorizontalRow + " generated at: " + chunkPosition.ToString());
                    }));

                    currentX += TerrainDataLoader.CHUNK_SIZE;
                }
                currentX = startX;
                currentZ = startZ;
                currentY += TerrainDataLoader.CHUNK_SIZE;
            }

            Task.WaitAll(tasks.ToArray()); // Wait for all tasks to complete

            Console.WriteLine("Chunks loaded: " + chunks.Count());
        }

        public void Render(ShaderProgram program)
        {
            foreach (var chunk in chunks)
            {
                chunk.Render(program);
            }
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
            string input;
            bool isValid = false;

            while (!isValid)
            {
                Console.WriteLine("Please enter a name for the world: ");
                input = Console.ReadLine();

                if (IsLatinAlphabet(input))
                {
                    if (worldSaves.Values.Contains(input))
                    {
                        Console.WriteLine("Invalid input. The world name is already taken. Please enter a different name.");
                    }
                    else
                    {
                        worldName = input;
                        isValid = true;
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
            Int32 parsedSeed;

            while (!Int32.TryParse(input, out parsedSeed))
            {
                Console.WriteLine("Invalid input. Please enter a valid seed: ");
                input = Console.ReadLine();
            }

            seed = parsedSeed;
        }
    }
}

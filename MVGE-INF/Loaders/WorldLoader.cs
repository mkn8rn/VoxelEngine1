using MVGE_INF.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Loaders
{
    public class WorldLoader
    {
        public Guid ID;
        public Guid RegionID;
        public string worldName;
        public int seed;
        public string currentWorldSaveDirectory;
        public string currentWorldDataFile = "world.txt";
        public string currentWorldSavedChunksSubDirectory = "chunks";
        public static Dictionary<Guid, string> worldSaves = new Dictionary<Guid, string>();
        public void ChooseWorld()
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
            if (RegionID == Guid.Empty)
            {
                RegionID = Guid.NewGuid();
            }

            Console.WriteLine($"Generating world {worldName} with seed: {seed}, id: {ID}, region: {RegionID}");

            // World root directory (by world ID)
            var worldRoot = Path.Combine(GameManager.settings.savesWorldDirectory, ID.ToString());
            Directory.CreateDirectory(worldRoot);
            currentWorldSaveDirectory = worldRoot; // keep root (region-specific paths built later)

            string worldDataPath = Path.Combine(worldRoot, currentWorldDataFile);
            using (var writer = new StreamWriter(worldDataPath))
            {
                // New format (4 lines): ID, RegionID, worldName, seed
                writer.WriteLine(ID.ToString());
                writer.WriteLine(RegionID.ToString());
                writer.WriteLine(worldName);
                writer.WriteLine(seed);
            }

            // Ensure region folder + chunks subfolder now
            string regionFolder = Path.Combine(worldRoot, RegionID.ToString());
            Directory.CreateDirectory(regionFolder);
            string chunkSaveFolderPath = Path.Combine(regionFolder, currentWorldSavedChunksSubDirectory);
            Directory.CreateDirectory(chunkSaveFolderPath);
        }

        public void DetectWorldSaves()
        {
            string[] worldRootDirs = Directory.GetDirectories(GameManager.settings.savesWorldDirectory);
            foreach (string worldRoot in worldRootDirs)
            {
                string worldDataPath = Path.Combine(worldRoot, currentWorldDataFile);
                if (!System.IO.File.Exists(worldDataPath)) continue;
                try
                {
                    string[] lines = File.ReadAllLines(worldDataPath);
                    var id = Guid.Parse(lines[0]);
                    var worldNameLocal = lines[2];
                    if (!worldSaves.ContainsKey(id))
                    {
                        worldSaves.Add(id, worldNameLocal);
                        Console.WriteLine($"Detected world save: {worldNameLocal}, id: {id}");
                    }
                }
                catch { }
            }
        }

        public void LoadWorldSave(Guid id)
        {
            ID = id;
            var worldRoot = Path.Combine(GameManager.settings.savesWorldDirectory, id.ToString());
            currentWorldSaveDirectory = worldRoot; // root, region subfolders below
            string worldDataPath = Path.Combine(worldRoot, currentWorldDataFile);
            string[] lines = File.ReadAllLines(worldDataPath);

            RegionID = Guid.Parse(lines[1]);
            worldName = lines[2];
            seed = int.Parse(lines[3]);

            // Ensure region directories exist
            string regionFolder = Path.Combine(worldRoot, RegionID.ToString());
            Directory.CreateDirectory(regionFolder);
            string chunkSaveFolderPath = Path.Combine(regionFolder, currentWorldSavedChunksSubDirectory);
            Directory.CreateDirectory(chunkSaveFolderPath);

            Console.WriteLine($"Loaded world save: {worldName}, id: {id}, seed: {seed}");
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
        public void GetWorldSeed()
        {
            Console.WriteLine("Please enter a seed for the world: ");
            string input = Console.ReadLine();

            while (!int.TryParse(input, out seed))
            {
                Console.WriteLine("Invalid input. Please enter a valid seed: ");
                input = Console.ReadLine();
            }
        }
        public bool IsLatinAlphabet(string input)
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
    }
}

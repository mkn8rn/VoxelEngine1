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

using MVoxelEngine1.Infrastructure.Managers;

namespace MVoxelEngine1.Infrastructure.Loaders
{
    public class WorldLoader
    {
        public Guid ID;
        public Guid RegionID;
        public string worldName = string.Empty;
        public int seed;
        public string currentWorldSaveDirectory = string.Empty;
        public string currentWorldDataFile = "world.txt";
        public string currentWorldSavedChunksSubDirectory = "chunks";

        private readonly Dictionary<Guid, string> worldSaves = new();

        public IReadOnlyDictionary<Guid, string> WorldSaves => worldSaves;

        public void ChooseWorld(string? requestedWorldName = null, int? requestedSeed = null)
        {
            if (requestedSeed.HasValue)
            {
                string resolvedWorldName = string.IsNullOrWhiteSpace(requestedWorldName)
                    ? $"World{requestedSeed.Value}"
                    : requestedWorldName;
                CreateWorldSave(resolvedWorldName, requestedSeed.Value);
                return;
            }

            DetectWorldSaves();

            if (worldSaves.Count == 0)
            {
                GenerateWorldSave();
                return;
            }

            Console.WriteLine("Please select a world:");
            Console.WriteLine("0. Generate a new world");

            List<string> worldSaveNames = worldSaves.Values.ToList();
            for (int index = 0; index < worldSaveNames.Count; index++)
                Console.WriteLine(index + 1 + ". " + worldSaveNames[index]);

            string? input = Console.ReadLine();
            if (!int.TryParse(input, out int selectedWorldIndex))
                throw new InvalidOperationException("World selection is not a valid number.");

            if (selectedWorldIndex == 0)
            {
                GenerateWorldSave();
                return;
            }

            if (selectedWorldIndex < 1 || selectedWorldIndex > worldSaves.Count)
                throw new InvalidOperationException("World selection is outside the valid range.");

            string selectedWorld = worldSaveNames[selectedWorldIndex - 1];
            Guid selectedWorldId = worldSaves.First(item => item.Value == selectedWorld).Key;
            LoadWorldSave(selectedWorldId);
        }

        public void CreateWorldSave(string name, int worldSeed)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("World name is null or empty.", nameof(name));

            worldName = name;
            seed = worldSeed;
            ID = Guid.NewGuid();
            RegionID = Guid.NewGuid();
            WriteWorldSave();
        }

        public void GenerateWorldSave()
        {
            if (string.IsNullOrWhiteSpace(worldName))
                GetWorldName();

            if (seed == 0)
                GetWorldSeed();

            if (ID == Guid.Empty)
                ID = Guid.NewGuid();
            if (RegionID == Guid.Empty)
                RegionID = Guid.NewGuid();

            WriteWorldSave();
        }

        public void DetectWorldSaves()
        {
            string savesDirectory = GameManager.settings.savesWorldDirectory;
            Directory.CreateDirectory(savesDirectory);
            worldSaves.Clear();

            foreach (string worldRoot in Directory.GetDirectories(savesDirectory))
            {
                string worldDataPath = Path.Combine(worldRoot, currentWorldDataFile);
                if (!File.Exists(worldDataPath))
                    continue;

                WorldSaveData data = ReadWorldSave(worldDataPath);
                if (!worldSaves.TryAdd(data.ID, data.Name))
                    throw new InvalidDataException($"Duplicate world ID '{data.ID}' was found at {worldDataPath}.");

                Console.WriteLine($"Detected world save: {data.Name}, id: {data.ID}");
            }
        }

        public void LoadWorldSave(Guid id)
        {
            string worldRoot = Path.Combine(GameManager.settings.savesWorldDirectory, id.ToString());
            string worldDataPath = Path.Combine(worldRoot, currentWorldDataFile);
            WorldSaveData data = ReadWorldSave(worldDataPath);

            if (data.ID != id)
                throw new InvalidDataException($"World file ID '{data.ID}' does not match directory ID '{id}'.");

            ID = data.ID;
            RegionID = data.RegionID;
            worldName = data.Name;
            seed = data.Seed;
            currentWorldSaveDirectory = worldRoot;

            CreateRegionDirectories(worldRoot);
            Console.WriteLine($"Loaded world save: {worldName}, id: {ID}, seed: {seed}");
        }

        public void GetWorldName()
        {
            while (true)
            {
                Console.WriteLine("Please enter a world name:");
                string? input = Console.ReadLine();
                if (!IsLatinAlphabet(input))
                {
                    Console.WriteLine("The world name must contain only Latin alphabet characters.");
                    continue;
                }

                if (worldSaves.Values.Contains(input!, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine("The world name is already in use.");
                    continue;
                }

                worldName = input!;
                return;
            }
        }

        public void GetWorldSeed()
        {
            Console.WriteLine("Please enter a world seed:");
            string? input = Console.ReadLine();

            while (!int.TryParse(input, out seed))
            {
                Console.WriteLine("The world seed must be an integer.");
                input = Console.ReadLine();
            }
        }

        public bool IsLatinAlphabet(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            foreach (char character in input)
            {
                if (!char.IsAsciiLetter(character))
                    return false;
            }

            return true;
        }

        private void WriteWorldSave()
        {
            Console.WriteLine($"Generating world {worldName} with seed: {seed}, id: {ID}, region: {RegionID}");

            string worldRoot = Path.Combine(GameManager.settings.savesWorldDirectory, ID.ToString());
            Directory.CreateDirectory(worldRoot);
            currentWorldSaveDirectory = worldRoot;

            string worldDataPath = Path.Combine(worldRoot, currentWorldDataFile);
            File.WriteAllLines(worldDataPath, new[]
            {
                ID.ToString(),
                RegionID.ToString(),
                worldName,
                seed.ToString()
            });

            CreateRegionDirectories(worldRoot);
        }

        private void CreateRegionDirectories(string worldRoot)
        {
            string regionDirectory = Path.Combine(worldRoot, RegionID.ToString());
            Directory.CreateDirectory(regionDirectory);
            Directory.CreateDirectory(Path.Combine(regionDirectory, currentWorldSavedChunksSubDirectory));
        }

        private static WorldSaveData ReadWorldSave(string worldDataPath)
        {
            if (!File.Exists(worldDataPath))
                throw new FileNotFoundException("World data file was not found.", worldDataPath);

            string[] lines = File.ReadAllLines(worldDataPath);
            if (lines.Length != 4)
                throw new InvalidDataException($"World data file must contain four lines: {worldDataPath}");
            if (!Guid.TryParse(lines[0], out Guid id))
                throw new InvalidDataException($"World ID is invalid in {worldDataPath}.");
            if (!Guid.TryParse(lines[1], out Guid regionId))
                throw new InvalidDataException($"Region ID is invalid in {worldDataPath}.");
            if (string.IsNullOrWhiteSpace(lines[2]))
                throw new InvalidDataException($"World name is empty in {worldDataPath}.");
            if (!int.TryParse(lines[3], out int worldSeed))
                throw new InvalidDataException($"World seed is invalid in {worldDataPath}.");

            return new WorldSaveData(id, regionId, lines[2], worldSeed);
        }

        private readonly record struct WorldSaveData(Guid ID, Guid RegionID, string Name, int Seed);
    }
}

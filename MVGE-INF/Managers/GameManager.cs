using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MVGE_INF.Models;

namespace MVGE_INF.Managers
{
    public class GameManager
    {
        public static GameSettings settings = new GameSettings();

        public static void Initialize()
        {
            LoadEnvironmentDefaultSettings();
        }

        private static void LoadEnvironmentDefaultSettings()
        {
            var flags = FlagManager.flags;
            if (string.IsNullOrEmpty(flags.gamesDirectory))
                throw new Exception("gamesDirectory flag is null or empty.");

            var path = Path.GetDirectoryName(typeof(GameManager).Assembly.Location)!;
            settings.gamesDirectory = Path.Combine(path, flags.gamesDirectory);
        }

        public static void LoadGameDefaultSettings(string gameDirectory)
        {
            string defaultsPath = Path.Combine(gameDirectory, "Defaults.txt");
            if (!File.Exists(defaultsPath))
                throw new Exception($"Defaults.txt not found in {gameDirectory}");

            var lines = File.ReadAllLines(defaultsPath);
            var dict = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                    dict[parts[0].Trim()] = parts[1].Trim();
            }

            string GetSetting(string key)
            {
                if (!dict.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                    throw new Exception($"Missing or empty setting: {key}");
                return value;
            }

            settings.chunkMaxX = int.Parse(GetSetting("chunkMaxX"));
            settings.chunkMaxZ = int.Parse(GetSetting("chunkMaxZ"));
            settings.chunkMaxY = int.Parse(GetSetting("chunkMaxY"));
            settings.blockTileWidth = int.Parse(GetSetting("blockTileWidth"));
            settings.blockTileHeight = int.Parse(GetSetting("blockTileHeight"));
            settings.textureFileExtension = GetSetting("textureFileExtension");
            settings.lod1RenderDistance = int.Parse(GetSetting("lod1RenderDistance"));
            settings.lod2RenderDistance = int.Parse(GetSetting("lod2RenderDistance"));
            settings.lod3RenderDistance = int.Parse(GetSetting("lod3RenderDistance"));
            settings.lod4RenderDistance = int.Parse(GetSetting("lod4RenderDistance"));
            settings.lod5RenderDistance = int.Parse(GetSetting("lod5RenderDistance"));
            settings.entityLoadRange = int.Parse(GetSetting("entityLoadRange"));
            settings.entitySpawnMaxRange = int.Parse(GetSetting("entitySpawnMaxRange"));
            settings.entityDespawnMaxRange = int.Parse(GetSetting("entityDespawnMaxRange"));
            settings.loadedGameSettingsDirectory = Path.Combine(gameDirectory, GetSetting("loadedGameSettingsDirectory"));
            settings.assetsBaseBlockTexturesDirectory = Path.Combine(gameDirectory, GetSetting("assetsBaseBlockTexturesDirectory"));
            settings.assetsBlockTexturesDirectory = Path.Combine(gameDirectory, GetSetting("assetsBlockTexturesDirectory"));
            settings.dataBlockTypesDirectory = Path.Combine(gameDirectory, GetSetting("dataBlockTypesDirectory"));
            settings.dataBiomeTypesDirectory = Path.Combine(gameDirectory, GetSetting("dataBiomeTypesDirectory"));
            settings.savesWorldDirectory = Path.Combine(gameDirectory, GetSetting("savesWorldDirectory"));
            settings.savesCharactersDirectory = Path.Combine(gameDirectory, GetSetting("savesCharactersDirectory"));
            settings.loadedGameDirectory = gameDirectory;
        }

        public static string SelectGameFolder(string? autoGameName = null)
        {
            string[] gameFolders = Directory.GetDirectories(settings.gamesDirectory);
            if (gameFolders.Length == 1)
            {
                string onlyGameName = Path.GetFileName(gameFolders[0]);
                Console.WriteLine($"Only one game: '{onlyGameName}' detected. Skipping game selection.");
                return gameFolders[0];
            }
            var defaultIndex = Array.FindIndex(gameFolders, f => Path.GetFileName(f).Equals("Default", StringComparison.OrdinalIgnoreCase));
            List<string> orderedFolders = new List<string>();
            if (defaultIndex != -1)
            {
                orderedFolders.Add(gameFolders[defaultIndex]);
                for (int i = 0; i < gameFolders.Length; i++)
                {
                    if (i != defaultIndex) orderedFolders.Add(gameFolders[i]);
                }
            }
            else
            {
                orderedFolders.AddRange(gameFolders);
            }

            if (!string.IsNullOrEmpty(autoGameName))
            {
                var match = orderedFolders.FirstOrDefault(f => Path.GetFileName(f).Equals(autoGameName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    Console.WriteLine($"Auto-selecting game: '{autoGameName}' via command-line flag.");
                    return match;
                }
                else
                {
                    Console.WriteLine($"Game '{autoGameName}' not found. Proceeding with manual selection.");
                }
            }

            Console.WriteLine("Select a game to load:");
            for (int i = 0; i < orderedFolders.Count; i++)
            {
                Console.WriteLine($"{i + 1}: {Path.GetFileName(orderedFolders[i])}");
            }
            while (true)
            {
                Console.Write($"Enter a number (1-{orderedFolders.Count}): ");
                string? input = Console.ReadLine();
                if (int.TryParse(input, out int selectedIndex) && selectedIndex >= 1 && selectedIndex <= orderedFolders.Count)
                {
                    return orderedFolders[selectedIndex - 1];
                }
                Console.WriteLine("Invalid input. Please try again.");
            }
        }
    }
}

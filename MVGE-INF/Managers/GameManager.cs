using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MVGE_INF.Models;

namespace MVGE_INF.Managers
{
    public class GameManager
    {
        // Backing field (nullable until a game is loaded)
        private static GameSettings? _settings;
        // Public accessor (non-null after LoadGameDefaultSettings). Existing code can continue calling GameManager.settings
        public static GameSettings settings => _settings ?? throw new InvalidOperationException("Game settings not loaded. Call LoadGameDefaultSettings first.");

        // Root folder containing all games (set from flags at initialization)
        private static string gamesRoot = string.Empty;

        private static readonly JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true
        };

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
            gamesRoot = Path.Combine(path, flags.gamesDirectory);
            if (!Directory.Exists(gamesRoot))
                throw new DirectoryNotFoundException($"Games root directory not found: {gamesRoot}");
        }

        public static void LoadGameDefaultSettings(string gameDirectory)
        {
            string defaultsPath = Path.Combine(gameDirectory, "Defaults.txt"); // still using .txt extension
            if (!File.Exists(defaultsPath))
                throw new Exception($"Defaults.txt not found in {gameDirectory}");

            string json = File.ReadAllText(defaultsPath);
            if (string.IsNullOrWhiteSpace(json))
                throw new Exception("Game Defaults JSON is empty.");

            GameSettings? loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<GameSettings>(json, jsonOptions);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to deserialize game Defaults.txt: {ex.Message}");
            }
            if (loaded == null)
                throw new Exception("Deserialization returned null GameSettings.");

            // Post-process: ensure required directory paths are rooted relative to the game folder.
            loaded.gamesDirectory = gamesRoot; // override any JSON value
            loaded.loadedGameDirectory = gameDirectory;
            loaded.loadedGameSettingsDirectory = Path.Combine(gameDirectory, loaded.loadedGameSettingsDirectory);
            loaded.assetsBaseBlockTexturesDirectory = Path.Combine(gameDirectory, loaded.assetsBaseBlockTexturesDirectory);
            loaded.assetsBlockTexturesDirectory = Path.Combine(gameDirectory, loaded.assetsBlockTexturesDirectory);
            loaded.dataBlockTypesDirectory = Path.Combine(gameDirectory, loaded.dataBlockTypesDirectory);
            loaded.dataBiomeTypesDirectory = Path.Combine(gameDirectory, loaded.dataBiomeTypesDirectory);
            loaded.savesWorldDirectory = Path.Combine(gameDirectory, loaded.savesWorldDirectory);
            loaded.savesCharactersDirectory = Path.Combine(gameDirectory, loaded.savesCharactersDirectory);

            _settings = loaded;
        }

        private static string MakeRelative(string baseDir, string fullPath)
        {
            try
            {
                var baseUri = new Uri(AppendDirectorySeparator(baseDir));
                var fullUri = new Uri(fullPath);
                if (baseUri.IsBaseOf(fullUri))
                    return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
            }
            catch { }
            return fullPath; // fallback to original
        }

        private static string AppendDirectorySeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

        public static string SelectGameFolder(string? autoGameName = null)
        {
            if (string.IsNullOrEmpty(gamesRoot))
                throw new InvalidOperationException("GameManager not initialized (gamesRoot unset). Call GameManager.Initialize() first.");

            string[] gameFolders = Directory.GetDirectories(gamesRoot);
            if (gameFolders.Length == 0)
                throw new Exception($"No game folders found in {gamesRoot}");
            if (gameFolders.Length == 1)
            {
                string onlyGameName = Path.GetFileName(gameFolders[0]);
                Console.WriteLine($"Only one game: '{onlyGameName}' detected. Skipping game selection.");
                return gameFolders[0];
            }
            var defaultIndex = Array.FindIndex(gameFolders, f => Path.GetFileName(f).Equals("Default", StringComparison.OrdinalIgnoreCase));
            List<string> orderedFolders = new();
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
                Console.WriteLine($"Game '{autoGameName}' not found. Proceeding with manual selection.");
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

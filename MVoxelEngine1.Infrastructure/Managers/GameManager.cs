using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Infrastructure.Managers
{
    public class GameManager
    {
        // Backing field (nullable until a game is loaded)
        private static GameSettings? _settings;
        // Public accessor (non-null after LoadGameDefaultSettings). Existing code can continue calling GameManager.settings
        public static GameSettings settings => _settings ?? throw new InvalidOperationException("Game settings not loaded. Call LoadGameDefaultSettings first.");

        private static string gameDataRoot = string.Empty;

        public static string GameDataRoot => !string.IsNullOrWhiteSpace(gameDataRoot)
            ? gameDataRoot
            : throw new InvalidOperationException("GameManager is not initialized.");

        private static readonly JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true
        };

        public static void Initialize()
        {
            string? configuredDirectory = FlagManager.flags.gameDataDirectory;
            if (string.IsNullOrWhiteSpace(configuredDirectory))
                throw new InvalidOperationException("gameDataDirectory flag is null or empty.");

            Initialize(configuredDirectory);
        }

        public static void Initialize(string gameDataDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDataDirectory))
                throw new ArgumentException("Game data directory is null or empty.", nameof(gameDataDirectory));

            string assemblyDirectory = Path.GetDirectoryName(typeof(GameManager).Assembly.Location)!;
            string resolvedDirectory = Path.IsPathRooted(gameDataDirectory)
                ? gameDataDirectory
                : Path.Combine(assemblyDirectory, gameDataDirectory);

            gameDataRoot = Path.GetFullPath(resolvedDirectory);
            if (!Directory.Exists(gameDataRoot))
                throw new DirectoryNotFoundException($"Game data root directory not found: {gameDataRoot}");
        }

        public static void LoadGameDefaultSettings(string gameDirectory)
        {
            gameDirectory = Path.GetFullPath(gameDirectory);
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
            loaded.gameDataDirectory = gameDataRoot;
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

        public static string SelectGameFolder(string? autoGameName = null)
        {
            if (string.IsNullOrEmpty(gameDataRoot))
                throw new InvalidOperationException("GameManager is not initialized. Call GameManager.Initialize first.");

            string[] gameFolders = Directory.GetDirectories(gameDataRoot);
            if (gameFolders.Length == 0)
                throw new DirectoryNotFoundException($"No game folders found in {gameDataRoot}");

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

                throw new DirectoryNotFoundException($"Game '{autoGameName}' was not found in {gameDataRoot}.");
            }

            if (gameFolders.Length == 1)
            {
                string onlyGameName = Path.GetFileName(gameFolders[0]);
                Console.WriteLine($"Only one game: '{onlyGameName}' detected. Skipping game selection.");
                return gameFolders[0];
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

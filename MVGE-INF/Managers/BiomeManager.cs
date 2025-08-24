using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MVGE_INF.Generation.Models;

namespace MVGE_INF.Managers
{
    public static class BiomeManager
    {
        public static IReadOnlyDictionary<string, Biome> Biomes => _biomes;        
        public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Microbiome>> Microbiomes => _microbiomes;

        private static readonly Dictionary<string, Biome> _biomes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, IReadOnlyDictionary<string, Microbiome>> _microbiomes = new(StringComparer.OrdinalIgnoreCase);
        private static string[] _biomeOrder = Array.Empty<string>();
        private static readonly HashSet<int> _biomeIds = new();

        private static readonly JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = false
        };

        public static void LoadAllBiomes()
        {
            _biomes.Clear();
            _microbiomes.Clear();
            _biomeIds.Clear();

            string assemblyDir = Path.GetDirectoryName(typeof(BiomeManager).Assembly.Location)!;
            string configured = GameManager.settings.dataBiomeTypesDirectory;
            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException("dataBiomeTypesDirectory setting is null or empty.");

            string biomesRoot = Path.IsPathRooted(configured) ? configured : Path.Combine(assemblyDir, configured);
            if (!Directory.Exists(biomesRoot))
                throw new DirectoryNotFoundException($"Biome types directory not found: {biomesRoot}");

            foreach (var biomeDir in Directory.GetDirectories(biomesRoot))
            {
                var biomeFolderName = Path.GetFileName(biomeDir);
                if (string.IsNullOrWhiteSpace(biomeFolderName)) continue;

                var biome = LoadBiomeJson(biomeDir, biomeFolderName);
                if (string.IsNullOrWhiteSpace(biome.name))
                {
                    biome.name = biomeFolderName; // fallback if name not provided in JSON
                }

                if (!_biomeIds.Add(biome.id))
                    throw new InvalidOperationException($"Duplicate biome id {biome.id} detected (folder '{biomeFolderName}'). IDs must be unique.");

                _biomes[biomeFolderName] = biome;
                var microbiomes = LoadMicrobiomes(Path.Combine(biomeDir, "Microbiomes"), biomeFolderName);
                _microbiomes[biomeFolderName] = microbiomes;

                Console.WriteLine($"[Biome] Loaded biome id={biome.id} folder='{biomeFolderName}' name='{biome.name}' (microbiomes: {microbiomes.Count})");
            }

            _biomeOrder = new string[_biomes.Count];
            _biomes.Keys.CopyTo(_biomeOrder, 0);
            Array.Sort(_biomeOrder, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"Total biomes loaded: {_biomes.Count}");
        }

        public static Biome SelectBiomeForChunk(long worldSeed, int chunkX, int chunkZ)
        {
            int count = _biomeOrder.Length;
            if (count == 0) throw new InvalidOperationException("No biomes loaded.");
            if (count == 1) return _biomes[_biomeOrder[0]];

            ulong x = (ulong)(uint)chunkX;
            ulong z = (ulong)(uint)chunkZ;
            ulong h = (ulong)worldSeed;
            h ^= x + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            h ^= z + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            int idx = (int)(h % (ulong)count);
            return _biomes[_biomeOrder[idx]];
        }

        private static Biome LoadBiomeJson(string biomeDir, string biomeFolderName)
        {
            string jsonPath = Path.Combine(biomeDir, "Defaults.txt"); // still use .txt extension per existing layout
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"Biome JSON Defaults.txt not found for biome '{biomeFolderName}' at {jsonPath}");

            string json = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException($"Biome JSON empty for biome '{biomeFolderName}'.");

            Biome biome;
            try
            {
                biome = JsonSerializer.Deserialize<Biome>(json, jsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse biome JSON for '{biomeFolderName}': {ex.Message}");
            }

            if (biome.id == 0 && !json.Contains("\"id\"", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Biome '{biomeFolderName}' is missing required 'id' field.");

            return biome;
        }

        private static IReadOnlyDictionary<string, Microbiome> LoadMicrobiomes(string microbiomesRoot, string biomeName)
        {
            var result = new Dictionary<string, Microbiome>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(microbiomesRoot)) return result;

            foreach (var microDir in Directory.GetDirectories(microbiomesRoot))
            {
                var microName = Path.GetFileName(microDir);
                if (string.IsNullOrWhiteSpace(microName)) continue;
                result[microName] = LoadMicrobiome(microDir, biomeName, microName);
            }
            return result;
        }

        private static Microbiome LoadMicrobiome(string microDir, string biomeName, string microName)
        {
            string defaultsPath = Path.Combine(microDir, "Defaults.txt");
            if (!File.Exists(defaultsPath))
                throw new FileNotFoundException($"Microbiome Defaults.txt not found for microbiome '{microName}' in biome '{biomeName}' at {defaultsPath}");
            // Placeholder for future JSON parse; currently only validates presence
            return new Microbiome();
        }
    }
}

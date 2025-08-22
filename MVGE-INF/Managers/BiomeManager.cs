using System;
using System.Collections.Generic;
using System.IO;
using MVGE_INF.Generation.Models;

namespace MVGE_INF.Managers
{
    public static class BiomeManager
    {
        // Public read-only accessors
        public static IReadOnlyDictionary<string, Biome> Biomes => _biomes;        
        public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Microbiome>> Microbiomes => _microbiomes;

        private static readonly Dictionary<string, Biome> _biomes = new();
        private static readonly Dictionary<string, IReadOnlyDictionary<string, Microbiome>> _microbiomes = new();
        private static string[] _biomeOrder = Array.Empty<string>();

        public static void LoadAllBiomes()
        {
            _biomes.Clear();
            _microbiomes.Clear();

            // Base directory of the executing assembly (same pattern as GameManager)
            string assemblyDir = Path.GetDirectoryName(typeof(BiomeManager).Assembly.Location)!;
            string configured = GameManager.settings.dataBiomeTypesDirectory;
            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException("dataBiomeTypesDirectory setting is null or empty.");

            string biomesRoot = Path.IsPathRooted(configured) ? configured : Path.Combine(assemblyDir, configured);

            if (!Directory.Exists(biomesRoot))
                throw new DirectoryNotFoundException($"Biome types directory not found: {biomesRoot}");

            foreach (var biomeDir in Directory.GetDirectories(biomesRoot))
            {
                var biomeName = Path.GetFileName(biomeDir);
                if (string.IsNullOrWhiteSpace(biomeName)) continue;

                var biome = LoadBiome(biomeDir, biomeName);
                _biomes[biomeName] = biome;
                var microbiomes = LoadMicrobiomes(Path.Combine(biomeDir, "Microbiomes"), biomeName);
                _microbiomes[biomeName] = microbiomes;
            }

            _biomeOrder = new string[_biomes.Count];
            _biomes.Keys.CopyTo(_biomeOrder, 0);
            Array.Sort(_biomeOrder, StringComparer.OrdinalIgnoreCase); // deterministic order
        }

        public static Biome SelectBiomeForChunk(long worldSeed, int chunkX, int chunkZ)
        {
            int count = _biomeOrder.Length;
            if (count == 0) throw new InvalidOperationException("No biomes loaded.");
            if (count == 1) return _biomes[_biomeOrder[0]]; // fast path

            // 64-bit mix (xorshift-like) for deterministic selection
            ulong x = (ulong)(uint)chunkX;
            ulong z = (ulong)(uint)chunkZ;
            ulong h = (ulong)worldSeed;
            h ^= x + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            h ^= z + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            int idx = (int)(h % (ulong)count);
            return _biomes[_biomeOrder[idx]];
        }

        private static Biome LoadBiome(string biomeDir, string biomeName)
        {
            string defaultsPath = Path.Combine(biomeDir, "Defaults.txt");
            if (!File.Exists(defaultsPath))
                throw new FileNotFoundException($"Biome Defaults.txt not found for biome '{biomeName}' at {defaultsPath}");

            var dict = ParseDefaultsFile(defaultsPath);
            return new Biome
            {
                stone_min_ylevel = ParseInt(dict, "stone_min_ylevel", biomeName),
                stone_max_ylevel = ParseInt(dict, "stone_max_ylevel", biomeName),
                stone_min_depth = ParseInt(dict, "stone_min_depth", biomeName),
                stone_max_depth = ParseInt(dict, "stone_max_depth", biomeName),
                soil_min_ylevel = ParseInt(dict, "soil_min_ylevel", biomeName),
                soil_max_ylevel = ParseInt(dict, "soil_max_ylevel", biomeName),
                soil_min_depth = ParseInt(dict, "soil_min_depth", biomeName),
                soil_max_depth = ParseInt(dict, "soil_max_depth", biomeName)
            };
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
            // Currently nothing to parse (struct empty) but call to validate file exists / future use
            _ = ParseDefaultsFile(defaultsPath);
            return new Microbiome();
        }

        private static Dictionary<string, string> ParseDefaultsFile(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2) dict[parts[0]] = parts[1];
            }
            return dict;
        }

        private static int ParseInt(Dictionary<string, string> dict, string key, string biomeName)
        {
            if (!dict.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new Exception($"Missing required int setting '{key}' for biome '{biomeName}'.");
            if (!int.TryParse(value, out var parsed))
                throw new Exception($"Invalid int value '{value}' for key '{key}' in biome '{biomeName}'.");
            return parsed;
        }
    }
}

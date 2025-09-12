using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MVGE_INF.Generation.Models;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Loaders;
using MVGE_INF.Models.Generation.Biomes;
using System.Text;

namespace MVGE_INF.Managers
{
    public static class BiomeManager
    {
        // Public read-only accessors now expose runtime Biome objects
        public static IReadOnlyDictionary<string, Biome> Biomes => _biomes;        
        public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, MicrobiomeJSON>> Microbiomes => _microbiomes; // keep raw microbiome map for now

        private static readonly Dictionary<string, Biome> _biomes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, IReadOnlyDictionary<string, MicrobiomeJSON>> _microbiomes = new(StringComparer.OrdinalIgnoreCase);
        private static string[] _biomeOrder = Array.Empty<string>(); // ordered keys for deterministic selection
        private static readonly HashSet<int> _biomeIds = new();

        private static readonly JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = false
        };

        static BiomeManager()
        {
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
        }

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

                // Load raw JSON model
                var biomeJson = LoadBiomeJson(biomeDir, biomeFolderName);
                if (string.IsNullOrWhiteSpace(biomeJson.name))
                {
                    biomeJson.name = biomeFolderName; // fallback if name not provided in JSON
                }

                if (!_biomeIds.Add(biomeJson.id))
                    throw new InvalidOperationException($"Duplicate biome id {biomeJson.id} detected (folder '{biomeFolderName}'). IDs must be unique.");

                // Load microbiomes (raw JSON) and convert to list for runtime biome
                var microbiomesMap = LoadMicrobiomes(Path.Combine(biomeDir, "Microbiomes"), biomeFolderName);
                _microbiomes[biomeFolderName] = microbiomesMap; // store map
                var microbiomesList = new List<MicrobiomeJSON>(microbiomesMap.Values);

                // Load generation rules (required to parse if file exists; throw on failure if present)
                var simpleReplacementRules = new List<SimpleReplacementRule>();
                string generationRulesPath = Path.Combine(biomeDir, "GenerationRules.txt");
                if (File.Exists(generationRulesPath))
                {
                    try
                    {
                        string rulesJsonText = File.ReadAllText(generationRulesPath);
                        if (string.IsNullOrWhiteSpace(rulesJsonText))
                            throw new Exception("GenerationRules.txt is empty");

                        // Preprocess to allow unquoted identifiers (e.g., Stone, Soil, LimeWhole) by mapping them to numeric IDs.
                        // This keeps the on-disk format human-readable while reusing existing numeric-based JSON model.
                        string processedText = PreprocessGenerationRulesText(rulesJsonText);

                        var rules = JsonSerializer.Deserialize<List<GenerationRuleJSON>>(processedText, jsonOptions);
                        if (rules == null)
                            throw new Exception("Deserialized rules list is null");

                        foreach (var rule in rules)
                        {
                            if (rule.generation_type != GenerationType.SimpleReplacement)
                                continue; // only map SimpleReplacement for now

                            // Resolve target block type by ID
                            var targetBlock = TerrainLoader.allBlockTypeObjects.Find(b => b.ID == rule.block_type_id);
                            if (targetBlock == null)
                                throw new Exception($"Rule references unknown block_type_id {rule.block_type_id}");

                            // Build list of base block types to replace
                            var baseList = new List<BaseBlockType>();
                            var blockList = new List<BlockType>();
                            if (rule.base_blocks_to_replace != null && rule.base_blocks_to_replace.Count > 0)
                            {
                                foreach (var bb in rule.base_blocks_to_replace)
                                {
                                    if (Enum.IsDefined(typeof(BaseBlockType), (byte)bb))
                                        baseList.Add((BaseBlockType)bb);
                                }
                            }
                            
                            if (rule.blocks_to_replace != null && rule.blocks_to_replace.Count > 0)
                            {
                                // Derive block types from specific block IDs
                                foreach (var btId in rule.blocks_to_replace)
                                {
                                    var blockType = TerrainLoader.allBlockTypeObjects.Find(b => b.ID == btId);
                                    if (blockType != null && !blockList.Contains(blockType))
                                        blockList.Add(blockType);
                                }
                            }

                            var simpleRule = new SimpleReplacementRule
                            {
                                base_blocks_to_replace = baseList,
                                blocks_to_replace = blockList,
                                block_type = targetBlock,
                                priority = rule.priority,
                                microbiomeId = rule.microbiome_id,
                                absoluteMinYlevel = rule.absolute_min_ylevel,
                                absoluteMaxYlevel = rule.absolute_max_ylevel
                            };
                            simpleReplacementRules.Add(simpleRule);
                        }

                        // Order by priority (ascending: lower number first)
                        simpleReplacementRules.Sort((a,b)=> a.priority.CompareTo(b.priority));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Failed to parse generation rules for biome '{biomeFolderName}': {ex.Message}");
                    }
                }

                // Map to runtime Biome class
                var runtimeBiome = new Biome
                {
                    id = biomeJson.id,
                    name = biomeJson.name,
                    stoneMinYLevel = biomeJson.stone_min_ylevel,
                    stoneMaxYLevel = biomeJson.stone_max_ylevel,
                    stoneMinDepth = biomeJson.stone_min_depth,
                    stoneMaxDepth = biomeJson.stone_max_depth,
                    soilMinYLevel = biomeJson.soil_min_ylevel,
                    soilMaxYLevel = biomeJson.soil_max_ylevel,
                    soilMinDepth = biomeJson.soil_min_depth,
                    soilMaxDepth = biomeJson.soil_max_depth,
                    waterLevel = biomeJson.water_level,
                    microbiomes = microbiomesList,
                    simpleReplacements = simpleReplacementRules
                };

                // --- Precompile simple replacement rules & vertical buckets -----------------------
                BuildCompiledSimpleReplacementRules(runtimeBiome);

                _biomes[biomeFolderName] = runtimeBiome;

                Console.WriteLine($"[Biome] Loaded biome id={runtimeBiome.id} folder='{biomeFolderName}' name='{runtimeBiome.name}' (microbiomes: {microbiomesMap.Count}, simpleRules: {simpleReplacementRules.Count})");
            }

            _biomeOrder = new string[_biomes.Count];
            _biomes.Keys.CopyTo(_biomeOrder, 0);
            Array.Sort(_biomeOrder, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"Total biomes loaded: {_biomes.Count}");
        }

        private static void BuildCompiledSimpleReplacementRules(Biome biome)
        {
            var list = biome.simpleReplacements;
            if (list == null || list.Count == 0)
            {
                biome.compiledSimpleReplacementRules = Array.Empty<CompiledSimpleReplacementRule>();
                biome.sectionYRuleBuckets = Array.Empty<int[]>();
                return;
            }

            var compiled = new List<CompiledSimpleReplacementRule>(list.Count);
            foreach (var r in list)
            {
                // Build specific id array
                var idList = new List<ushort>();
                if (r.blocks_to_replace != null)
                {
                    foreach (var bt in r.blocks_to_replace)
                    {
                        if (bt != null) idList.Add(bt.ID);
                    }
                }
                idList.Sort();
                // Build base type mask
                uint mask = 0u;
                if (r.base_blocks_to_replace != null)
                {
                    foreach (var bb in r.base_blocks_to_replace)
                    {
                        mask |= 1u << (int)bb;
                    }
                }
                int minY = r.absoluteMinYlevel ?? int.MinValue;
                int maxY = r.absoluteMaxYlevel ?? int.MaxValue;
                var compiledRule = new CompiledSimpleReplacementRule(
                    r.block_type.ID,
                    idList.ToArray(),
                    mask,
                    minY,
                    maxY,
                    r.microbiomeId,
                    r.priority);
                compiled.Add(compiledRule);
            }
            // Already sorted by original simpleReplacements ordering (which was by priority). Ensure stable ascending.
            compiled.Sort((a,b)=> a.Priority.CompareTo(b.Priority));
            biome.compiledSimpleReplacementRules = compiled.ToArray();

            // Vertical bucketing by section Y (assuming fixed 16-high sections). Determine vertical span using game settings.
            int chunkMaxY = GameManager.settings.chunkMaxY; // world vertical size per chunk
            int sectionSize = ChunkSection.SECTION_SIZE;
            int sectionCountY = chunkMaxY / sectionSize;
            var buckets = new int[sectionCountY][]; // fill lazily
            var tempLists = new List<int>[sectionCountY];
            for (int i=0;i<sectionCountY;i++) tempLists[i] = new List<int>();
            for (int ri=0; ri<compiled.Count; ri++)
            {
                var cr = compiled[ri];
                // compute intersecting section indices
                int firstSection = Math.Max(0, cr.MinY == int.MinValue ? 0 : cr.MinY / sectionSize);
                int lastSection = cr.MaxY == int.MaxValue ? sectionCountY - 1 : cr.MaxY / sectionSize;
                if (lastSection >= sectionCountY) lastSection = sectionCountY - 1;
                if (firstSection >= sectionCountY || lastSection < 0) continue;
                if (firstSection < 0) firstSection = 0;
                for (int sy = firstSection; sy <= lastSection; sy++)
                {
                    // verify actual overlap (section world bounds)
                    int secY0 = sy * sectionSize;
                    int secY1 = secY0 + sectionSize - 1;
                    if (!(cr.MaxY < secY0 || cr.MinY > secY1))
                        tempLists[sy].Add(ri);
                }
            }
            for (int sy=0; sy<sectionCountY; sy++)
            {
                buckets[sy] = tempLists[sy].Count == 0 ? Array.Empty<int>() : tempLists[sy].ToArray();
            }
            biome.sectionYRuleBuckets = buckets;
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

        // Raw JSON loading helpers
        private static BiomeJSON LoadBiomeJson(string biomeDir, string biomeFolderName)
        {
            string jsonPath = Path.Combine(biomeDir, "Defaults.txt");
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"Biome JSON Defaults.txt not found for biome '{biomeFolderName}' at {jsonPath}");

            string json = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException($"Biome JSON empty for biome '{biomeFolderName}'.");

            BiomeJSON biome;
            try
            {
                biome = JsonSerializer.Deserialize<BiomeJSON>(json, jsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse biome JSON for '{biomeFolderName}': {ex.Message}");
            }

            if (biome.id == 0 && !json.Contains("\"id\"", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Biome '{biomeFolderName}' is missing required 'id' field.");

            return biome;
        }

        private static IReadOnlyDictionary<string, MicrobiomeJSON> LoadMicrobiomes(string microbiomesRoot, string biomeName)
        {
            var result = new Dictionary<string, MicrobiomeJSON>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(microbiomesRoot)) return result;

            foreach (var microDir in Directory.GetDirectories(microbiomesRoot))
            {
                var microName = Path.GetFileName(microDir);
                if (string.IsNullOrWhiteSpace(microName)) continue;
                result[microName] = LoadMicrobiome(microDir, biomeName, microName);
            }
            return result;
        }

        private static MicrobiomeJSON LoadMicrobiome(string microDir, string biomeName, string microName)
        {
            string defaultsPath = Path.Combine(microDir, "Defaults.txt");
            if (!File.Exists(defaultsPath))
                throw new FileNotFoundException($"Microbiome Defaults.txt not found for microbiome '{microName}' in biome '{biomeName}' at {defaultsPath}");
            // Placeholder for future JSON parse; currently only validates presence
            return new MicrobiomeJSON();
        }

        // --- Preprocessing helper -------------------------------------------------------------
        // Converts unquoted identifiers referencing BaseBlockType names or BlockType unique names
        // to their numeric IDs so that standard System.Text.Json deserialization (expecting numbers) succeeds.
        private static string PreprocessGenerationRulesText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            // Build lookup maps (case-insensitive)
            var baseTypeMap = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            foreach (BaseBlockType bt in Enum.GetValues(typeof(BaseBlockType)))
            {
                baseTypeMap[bt.ToString()] = (ushort)bt;
            }
            var blockTypeMap = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            foreach (var bt in TerrainLoader.allBlockTypeObjects)
            {
                if (!blockTypeMap.ContainsKey(bt.UniqueName)) blockTypeMap[bt.UniqueName] = bt.ID;
                if (!string.IsNullOrWhiteSpace(bt.Name) && !blockTypeMap.ContainsKey(bt.Name)) blockTypeMap[bt.Name] = bt.ID;
            }

            // We only transform tokens that are not inside strings.
            // A token becomes numeric if found in either map; otherwise left as-is.
            // Reserved literals that should remain untouched.
            static bool IsReserved(string s) => string.Equals(s, "null", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder(raw.Length + 64);
            bool inString = false;
            for (int i = 0; i < raw.Length; )
            {
                char c = raw[i];
                if (c == '"')
                {
                    sb.Append(c);
                    i++;
                    // handle escape sequences (skip) simplistic
                    inString = !inString;
                    continue;
                }
                if (inString)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    i++;
                    while (i < raw.Length && (char.IsLetterOrDigit(raw[i]) || raw[i] == '_')) i++;
                    string token = raw[start..i];
                    if (IsReserved(token))
                    {
                        sb.Append(token);
                        continue;
                    }
                    if (baseTypeMap.TryGetValue(token, out ushort baseId))
                    {
                        sb.Append(baseId.ToString());
                        continue;
                    }
                    if (blockTypeMap.TryGetValue(token, out ushort blockId))
                    {
                        sb.Append(blockId.ToString());
                        continue;
                    }
                    // Unknown identifier: leave unchanged so downstream error surfaces with context.
                    sb.Append(token);
                    continue;
                }
                // All other characters copied verbatim
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}

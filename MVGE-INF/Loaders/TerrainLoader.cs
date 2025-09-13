using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace MVGE_INF.Loaders
{
    public class TerrainLoader
    {
        // Loading
        private const ushort FIRST_CUSTOM_BLOCK_ID = 256; // IDs <256 reserved for base / special

        // Non-opaque (transparent or translucent) blocks.
        public static HashSet<BlockType> NonOpaqueBlocks { get; private set; }
        public static HashSet<ushort> NonOpaqueBlockIds { get; private set; }

        // Fast O(1) classification table (index = block id)
        // Always length 65536 (full ushort domain) to avoid bounds checks.
        private static readonly bool[] NonOpaqueLut = new bool[65536];
        private static readonly bool[] LiquidLut = new bool[65536];
        private static readonly bool[] SolidLut = new bool[65536];
        private static readonly bool[] GasLut = new bool[65536];

        // Liquid blocks.
        public static HashSet<BlockType> LiquidBlocks { get; private set; }
        public static HashSet<ushort> LiquidBlockIds { get; private set; }

        // Hardcoded list of base block types that are non-opaque.
        public List<BaseBlockType> NonOpaqueBaseBlocks = new List<BaseBlockType> {
            BaseBlockType.Empty,
            BaseBlockType.Gas,
            BaseBlockType.Water,
            BaseBlockType.Glass
        };

        // Hardcoded list of liquid base block types.
        public List<BaseBlockType> LiquidBaseBlocks = new List<BaseBlockType> {
            BaseBlockType.Water
        };

        public TerrainLoader()
        {
            Console.WriteLine("Terrain data loading.");

            LoadBaseBlockType();
            LoadOtherBlockTypes();
            InitializeNonOpaqueBlocks();
            BuildNonOpaqueLookup();
            InitializeLiquidBlocks();
            BuildLiquidLookup();

            Console.WriteLine("Terrain data finished loading.");
            Console.WriteLine($"Total block types (including base): {allBlockTypes.Count}");
        }

        // Build / rebuild the non-opaque LUT from current NonOpaqueBlockIds (idempotent, fast).
        private static void BuildNonOpaqueLookup()
        {
            if (NonOpaqueBlockIds == null) return; // nothing to do yet
            Array.Clear(NonOpaqueLut, 0, NonOpaqueLut.Length);
            foreach (var id in NonOpaqueBlockIds)
            {
                NonOpaqueLut[id] = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNonOpaque(ushort blockId)
        {
            // Uses precomputed LUT for O(1) classification. Air (id 0) always treated as non-opaque even
            // if LUT not yet built. Falls back gracefully before initialization.
            if (blockId == 0) return true; // air shortcut
            return NonOpaqueLut[blockId];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOpaque(ushort blockId)
        {
            // Opaque = not air and not in non-opaque LUT.
            if (blockId == 0) return false; // air
            return !NonOpaqueLut[blockId];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsLiquid(ushort blockId)
        {
            // Liquid = not air and in liquid LUT.
            if (blockId == 0) return false; // air
            return LiquidLut[blockId];
        }

        private void InitializeNonOpaqueBlocks()
        {
            // Create new set (object instances) and parallel id set for O(1) lookups (backing data for LUT construction).
            NonOpaqueBlocks = new HashSet<BlockType>();
            NonOpaqueBlockIds = new HashSet<ushort>();

            // 1. Add the hardcoded non-opaque base block types (includes Empty).
            foreach (var baseType in NonOpaqueBaseBlocks)
            {
                ushort id = (ushort)baseType;
                var bt = allBlockTypeObjects.FirstOrDefault(o => o.ID == id);
                if (bt != null)
                {
                    NonOpaqueBlocks.Add(bt);
                    NonOpaqueBlockIds.Add(bt.ID);
                }
            }

            // 2. Add all custom (non-base) block types that are transparent.
            foreach (var bt in allBlockTypeObjects)
            {
                if (bt.ID < FIRST_CUSTOM_BLOCK_ID) continue; // skip base enum defined types here
                if (bt.IsTransparent)
                {
                    NonOpaqueBlocks.Add(bt);
                    NonOpaqueBlockIds.Add(bt.ID);
                }
            }
        }

        private void InitializeLiquidBlocks()
        {
            // Create new set (object instances) and parallel id set for O(1) lookups (backing data for LUT construction).
            LiquidBlocks = new HashSet<BlockType>();
            LiquidBlockIds = new HashSet<ushort>();
            // 1. Add the hardcoded liquid base block types.
            foreach (var baseType in LiquidBaseBlocks)
            {
                ushort id = (ushort)baseType;
                var bt = allBlockTypeObjects.FirstOrDefault(o => o.ID == id);
                if (bt != null)
                {
                    LiquidBlocks.Add(bt);
                    LiquidBlockIds.Add(bt.ID);
                }
            }
            // 2. Add all custom (non-base) block types that are liquids.
            foreach (var bt in allBlockTypeObjects)
            {
                if (bt.ID < FIRST_CUSTOM_BLOCK_ID) continue; // skip base enum defined types here
                if (bt.StateOfMatter == BlockStateOfMatter.Liquid)
                {
                    LiquidBlocks.Add(bt);
                    LiquidBlockIds.Add(bt.ID);
                }
            }
        }

        private void BuildLiquidLookup()
        {
            if (LiquidBlockIds == null) return; // nothing to do yet
            Array.Clear(LiquidLut, 0, LiquidLut.Length);
            foreach (var id in LiquidBlockIds)
            {
                LiquidLut[id] = true;
            }
        }

        internal void LoadBaseBlockType()
        {
            // Base enum block types occupy the reserved ID range starting at 0.
            foreach (BaseBlockType baseType in Enum.GetValues(typeof(BaseBlockType)))
            {
                ushort id = (ushort)baseType; // authoritative ID for the base block
                if (id >= FIRST_CUSTOM_BLOCK_ID)
                {
                    throw new InvalidOperationException($"Base block enum value '{baseType}' has underlying id {id} which collides with custom block ID range (>= {FIRST_CUSTOM_BLOCK_ID}).");
                }

                string name = baseType.ToString();
                bool isTransparent = NonOpaqueBaseBlocks.Contains(baseType);
                BlockStateOfMatter StateOfMatter = LiquidBaseBlocks.Contains(baseType) ? BlockStateOfMatter.Liquid : BlockStateOfMatter.Solid;

                // Detect and warn on duplicate (should not happen, but keeps behavior explicit).
                if (allBlockTypesByIds.ContainsKey(id))
                {
                    Console.WriteLine($"[TerrainLoader][WARN] Duplicate base block ID {id} for enum {baseType}; existing='{allBlockTypesByIds[id]}'. Skipping.");
                    continue;
                }

                allBlockTypes.Add(name);
                allBlockTypesByBaseType[name] = baseType;
                allBlockTypesByIds[id] = name;

                var btObj = new BlockType
                {
                    ID = id,
                    UniqueName = name,
                    Name = name,
                    BaseType = baseType,
                    TextureFaceBase = name,
                    TextureFaceTop = name,
                    TextureFaceFront = name,
                    TextureFaceBack = name,
                    TextureFaceLeft = name,
                    TextureFaceRight = name,
                    TextureFaceBottom = name,
                    IsTransparent = isTransparent,
                    StateOfMatter = StateOfMatter
                };
                allBlockTypeObjects.Add(btObj);

                Console.WriteLine("Base type defined: " + name + ", id: " + id + "/65535");
            }
        }

        internal void LoadOtherBlockTypes()
        {
            string dir = GameManager.settings.dataBlockTypesDirectory;
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"Block type data directory not found: {dir}");
                return;
            }

            string[] txtFiles = Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly);
            if (txtFiles.Length == 0)
            {
                Console.WriteLine("No custom block type files found.");
                return;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());

            // Tracking collections for reporting
            var explicitIdList = new List<(BlockTypeJSON json, string file)>();
            var autoIdList = new List<(BlockTypeJSON json, string file)>();
            var skippedFiles = new List<(string file, string reason)>();
            var explicitAssigned = new List<(string name, ushort id)>();
            var autoAssigned = new List<(string name, ushort id)>();

            // First pass: deserialize and classify
            foreach (string txtFile in txtFiles)
            {
                try
                {
                    string jsonText = File.ReadAllText(txtFile);
                    if (string.IsNullOrWhiteSpace(jsonText))
                    {
                        skippedFiles.Add((Path.GetFileName(txtFile), "Empty file"));
                        continue;
                    }
                    BlockTypeJSON? parsed = JsonSerializer.Deserialize<BlockTypeJSON>(jsonText, jsonOptions);
                    if (parsed == null)
                    {
                        skippedFiles.Add((Path.GetFileName(txtFile), "Deserialize returned null"));
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(parsed.Value.Name))
                    {
                        skippedFiles.Add((Path.GetFileName(txtFile), "Missing Name"));
                        continue;
                    }
                    if (parsed.Value.ID.HasValue)
                        explicitIdList.Add((parsed.Value, txtFile));
                    else
                        autoIdList.Add((parsed.Value, txtFile));
                }
                catch (Exception ex)
                {
                    skippedFiles.Add((Path.GetFileName(txtFile), "Exception: " + ex.Message));
                }
            }

            // Track taken IDs (include base + reserved)
            var takenIds = new HashSet<ushort>(allBlockTypeObjects.Select(b => b.ID));
            for (ushort r = 0; r < FIRST_CUSTOM_BLOCK_ID; r++) takenIds.Add(r);

            void RegisterRuntimeBlock(BlockType rt, string filePath)
            {
                allBlockTypes.Add(rt.Name);
                allBlockTypesByBaseType[rt.Name] = rt.BaseType;
                allBlockTypesByIds[rt.ID] = rt.Name;
                allBlockTypeObjects.Add(rt);
                Console.WriteLine($"Block type JSON loaded: {rt.Name} (unique '{rt.UniqueName}'), base type: {rt.BaseType}, id: {rt.ID}/65535 (file: {Path.GetFileName(filePath)})");
            }

            // Explicit IDs first
            foreach (var (json, file) in explicitIdList)
            {
                try
                {
                    ushort requestedId = json.ID!.Value;
                    if (requestedId < FIRST_CUSTOM_BLOCK_ID)
                        throw new InvalidOperationException($"Requested reserved ID {requestedId}");
                    if (takenIds.Contains(requestedId))
                        throw new InvalidOperationException($"Requested ID {requestedId} already taken");

                    takenIds.Add(requestedId);
                    string fileBaseName = Path.GetFileNameWithoutExtension(file);
                    var rt = new BlockType
                    {
                        ID = requestedId,
                        UniqueName = fileBaseName,
                        Name = json.Name,
                        BaseType = json.BaseType,
                        TextureFaceBase = json.TextureFaceBase,
                        TextureFaceTop = json.TextureFaceTop ?? json.TextureFaceBase,
                        TextureFaceFront = json.TextureFaceFront ?? json.TextureFaceBase,
                        TextureFaceBack = json.TextureFaceBack ?? json.TextureFaceBase,
                        TextureFaceLeft = json.TextureFaceLeft ?? json.TextureFaceBase,
                        TextureFaceRight = json.TextureFaceRight ?? json.TextureFaceBase,
                        TextureFaceBottom = json.TextureFaceBottom ?? json.TextureFaceBase,
                        IsTransparent = json.IsTransparent,
                        StateOfMatter = json.StateOfMatter
                    };
                    RegisterRuntimeBlock(rt, file);
                    explicitAssigned.Add((rt.Name, rt.ID));
                }
                catch (Exception ex)
                {
                    skippedFiles.Add((Path.GetFileName(file), "Explicit ID failed: " + ex.Message));
                }
            }

            // Auto IDs
            ushort nextId = FIRST_CUSTOM_BLOCK_ID;
            foreach (var (json, file) in autoIdList)
            {
                try
                {
                    while (takenIds.Contains(nextId))
                    {
                        if (nextId == ushort.MaxValue)
                            throw new InvalidOperationException("Ran out of block IDs");
                        nextId++;
                    }
                    ushort assignedId = nextId;
                    takenIds.Add(assignedId);
                    nextId++;

                    string fileBaseName = Path.GetFileNameWithoutExtension(file);
                    var rt = new BlockType
                    {
                        ID = assignedId,
                        UniqueName = fileBaseName,
                        Name = json.Name,
                        BaseType = json.BaseType,
                        TextureFaceBase = json.TextureFaceBase,
                        TextureFaceTop = json.TextureFaceTop ?? json.TextureFaceBase,
                        TextureFaceFront = json.TextureFaceFront ?? json.TextureFaceBase,
                        TextureFaceBack = json.TextureFaceBack ?? json.TextureFaceBase,
                        TextureFaceLeft = json.TextureFaceLeft ?? json.TextureFaceBase,
                        TextureFaceRight = json.TextureFaceRight ?? json.TextureFaceBase,
                        TextureFaceBottom = json.TextureFaceBottom ?? json.TextureFaceBase,
                        IsTransparent = json.IsTransparent,
                        StateOfMatter = json.StateOfMatter
                    };
                    RegisterRuntimeBlock(rt, file);
                    autoAssigned.Add((rt.Name, rt.ID));
                }
                catch (Exception ex)
                {
                    skippedFiles.Add((Path.GetFileName(file), "Auto ID failed: " + ex.Message));
                }
            }

            // Summary report
            Console.WriteLine($"Explicit ID requests processed: {explicitIdList.Count}, successful: {explicitAssigned.Count}, failed: {explicitIdList.Count - explicitAssigned.Count}");
            if (explicitAssigned.Count > 0)
                Console.WriteLine("Explicit assignments: " + string.Join(", ", explicitAssigned.Select(e => e.name + "->" + e.id)));
            Console.WriteLine($"Auto-ID blocks processed: {autoIdList.Count}, assigned: {autoAssigned.Count}, failed: {autoIdList.Count - autoAssigned.Count}");
            if (autoAssigned.Count > 0)
                Console.WriteLine("Auto assignments: " + string.Join(", ", autoAssigned.Select(a => a.name + "->" + a.id)));
            Console.WriteLine($"Skipped files: {skippedFiles.Count}");
            foreach (var (f, r) in skippedFiles)
                Console.WriteLine("  Skipped " + f + ": " + r);
        }

        public static List<string> allBlockTypes { get; set; } = new List<string>();
        public static Dictionary<string, BaseBlockType> allBlockTypesByBaseType { get; set; } = new Dictionary<string, BaseBlockType>();
        public static Dictionary<ushort, string> allBlockTypesByIds { get; set; } = new Dictionary<ushort, string>();
        public static List<BlockType> allBlockTypeObjects { get; set; } = new List<BlockType>();
    }
}

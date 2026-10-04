using MVoxelEngine1.Infrastructure.Resources;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace MVoxelEngine1.Infrastructure.Loaders
{
    public sealed class TerrainLoader
    {
        // Loading
        private const ushort FIRST_CUSTOM_BLOCK_ID = 256; // IDs <256 reserved for base / special

        // Non-opaque (transparent or translucent) blocks.
        private static HashSet<BlockType> nonOpaqueBlocks = [];
        public static IReadOnlySet<BlockType> NonOpaqueBlocks => nonOpaqueBlocks;
        private static HashSet<ushort> nonOpaqueBlockIds = [];
        public static IReadOnlySet<ushort> NonOpaqueBlockIds => nonOpaqueBlockIds;

        // Fast O(1) classification table (index = block id)
        // Always length 65536 (full ushort domain) to avoid bounds checks.
        private static readonly bool[] NonOpaqueLut = new bool[65536];
        private static readonly bool[] LiquidLut = new bool[65536];

        // Liquid blocks.
        private static HashSet<BlockType> liquidBlocks = [];
        public static IReadOnlySet<BlockType> LiquidBlocks => liquidBlocks;
        private static HashSet<ushort> liquidBlockIds = [];
        public static IReadOnlySet<ushort> LiquidBlockIds => liquidBlockIds;

        // Hardcoded list of base block types that are non-opaque.
        private static readonly BaseBlockType[] NonOpaqueBaseBlocks = [
            BaseBlockType.Empty,
            BaseBlockType.Gas,
            BaseBlockType.Water,
            BaseBlockType.Glass
        ];

        // Hardcoded list of liquid base block types.
        private static readonly BaseBlockType[] LiquidBaseBlocks = [
            BaseBlockType.Water
        ];

        public TerrainLoader()
        {
            Console.WriteLine(EngineMessages.TerrainLoading);

            LoadBaseBlockType();
            LoadOtherBlockTypes();
            InitializeNonOpaqueBlocks();
            BuildNonOpaqueLookup();
            InitializeLiquidBlocks();
            BuildLiquidLookup();

            Console.WriteLine(EngineMessages.TerrainLoaded);
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

        private static void InitializeNonOpaqueBlocks()
        {
            // Create new set (object instances) and parallel id set for O(1) lookups (backing data for LUT construction).
            nonOpaqueBlocks = new HashSet<BlockType>();
            nonOpaqueBlockIds = new HashSet<ushort>();

            // 1. Add the hardcoded non-opaque base block types (includes Empty).
            foreach (var baseType in NonOpaqueBaseBlocks)
            {
                ushort id = (ushort)baseType;
                var bt = allBlockTypeObjects.FirstOrDefault(o => o.ID == id);
                if (bt != null)
                {
                    nonOpaqueBlocks.Add(bt);
                    nonOpaqueBlockIds.Add(bt.ID);
                }
            }

            // 2. Add all custom (non-base) block types that are transparent.
            foreach (var bt in allBlockTypeObjects)
            {
                if (bt.ID < FIRST_CUSTOM_BLOCK_ID) continue; // skip base enum defined types here
                if (bt.IsTransparent)
                {
                    nonOpaqueBlocks.Add(bt);
                    nonOpaqueBlockIds.Add(bt.ID);
                }
            }
        }

        private static void InitializeLiquidBlocks()
        {
            // Create new set (object instances) and parallel id set for O(1) lookups (backing data for LUT construction).
            liquidBlocks = new HashSet<BlockType>();
            liquidBlockIds = new HashSet<ushort>();
            // 1. Add the hardcoded liquid base block types.
            foreach (var baseType in LiquidBaseBlocks)
            {
                ushort id = (ushort)baseType;
                var bt = allBlockTypeObjects.FirstOrDefault(o => o.ID == id);
                if (bt != null)
                {
                    liquidBlocks.Add(bt);
                    liquidBlockIds.Add(bt.ID);
                }
            }
            // 2. Add all custom (non-base) block types that are liquids.
            foreach (var bt in allBlockTypeObjects)
            {
                if (bt.ID < FIRST_CUSTOM_BLOCK_ID) continue; // skip base enum defined types here
                if (bt.StateOfMatter == BlockStateOfMatter.Liquid)
                {
                    liquidBlocks.Add(bt);
                    liquidBlockIds.Add(bt.ID);
                }
            }
        }

        private static void BuildLiquidLookup()
        {
            if (LiquidBlockIds == null) return; // nothing to do yet
            Array.Clear(LiquidLut, 0, LiquidLut.Length);
            foreach (var id in LiquidBlockIds)
            {
                LiquidLut[id] = true;
            }
        }

        internal static void LoadBaseBlockType()
        {
            // Base enum block types occupy the reserved ID range starting at 0.
            foreach (BaseBlockType baseType in Enum.GetValues<BaseBlockType>())
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
                if (allBlockTypesByIds.TryGetValue(id, out string? existingName))
                {
                    Console.WriteLine($"[TerrainLoader][WARN] Duplicate base block ID {id} for enum {baseType}; existing='{existingName}'. Skipping.");
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

        internal static void LoadOtherBlockTypes()
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
                Console.WriteLine(EngineMessages.NoCustomBlockFiles);
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
                    BlockTypeJSON parsed = JsonSerializer.Deserialize<BlockTypeJSON>(jsonText, jsonOptions);
                    if (string.IsNullOrWhiteSpace(parsed.Name))
                    {
                        skippedFiles.Add((Path.GetFileName(txtFile), "Missing Name"));
                        continue;
                    }
                    if (parsed.ID.HasValue)
                        explicitIdList.Add((parsed, txtFile));
                    else
                        autoIdList.Add((parsed, txtFile));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
                {
                    skippedFiles.Add((Path.GetFileName(txtFile), "Exception: " + ex.Message));
                }
            }
            ReportCustomBlockAssignments(explicitIdList, autoIdList, skippedFiles, explicitAssigned, autoAssigned);
        }

        private static void RegisterRuntimeBlock(BlockType rt, string filePath)
        {
            allBlockTypes.Add(rt.Name);
            allBlockTypesByBaseType[rt.Name] = rt.BaseType;
            allBlockTypesByIds[rt.ID] = rt.Name;
            allBlockTypeObjects.Add(rt);
            Console.WriteLine($"Block type JSON loaded: {rt.Name} (unique '{rt.UniqueName}'), base type: {rt.BaseType}, id: {rt.ID}/65535 (file: {Path.GetFileName(filePath)})");
        }

        public static IList<string> allBlockTypes { get; } = new List<string>();
        public static IDictionary<string, BaseBlockType> allBlockTypesByBaseType { get; } = new Dictionary<string, BaseBlockType>(StringComparer.Ordinal);
        public static IDictionary<ushort, string> allBlockTypesByIds { get; } = new Dictionary<ushort, string>();
        public static IList<BlockType> allBlockTypeObjects { get; } = new List<BlockType>();

        private static void AssignAutomaticBlockIds(global::System.Collections.Generic.List<(global::MVoxelEngine1.Infrastructure.Models.Terrain.BlockTypeJSON json, string file)> explicitIdList, global::System.Collections.Generic.List<(global::MVoxelEngine1.Infrastructure.Models.Terrain.BlockTypeJSON json, string file)> autoIdList, global::System.Collections.Generic.List<(string file, string reason)> skippedFiles, global::System.Collections.Generic.List<(string name, ushort id)> explicitAssigned, global::System.Collections.Generic.List<(string name, ushort id)> autoAssigned, global::System.Collections.Generic.HashSet<ushort> takenIds)
        {
            // Auto IDs
            ushort nextId = FIRST_CUSTOM_BLOCK_ID;
            foreach (var(json, file)in autoIdList)
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
                catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
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
            foreach (var(f, r)in skippedFiles)
                Console.WriteLine("  Skipped " + f + ": " + r);
        }

        private static void ReportCustomBlockAssignments(global::System.Collections.Generic.List<(global::MVoxelEngine1.Infrastructure.Models.Terrain.BlockTypeJSON json, string file)> explicitIdList, global::System.Collections.Generic.List<(global::MVoxelEngine1.Infrastructure.Models.Terrain.BlockTypeJSON json, string file)> autoIdList, global::System.Collections.Generic.List<(string file, string reason)> skippedFiles, global::System.Collections.Generic.List<(string name, ushort id)> explicitAssigned, global::System.Collections.Generic.List<(string name, ushort id)> autoAssigned)
        {
            // Track taken IDs (include base + reserved)
            var takenIds = new HashSet<ushort>(allBlockTypeObjects.Select(b => b.ID));
            for (ushort r = 0; r < FIRST_CUSTOM_BLOCK_ID; r++)
                takenIds.Add(r);
            // Explicit IDs first
            foreach (var(json, file)in explicitIdList)
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
                catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
                {
                    skippedFiles.Add((Path.GetFileName(file), "Explicit ID failed: " + ex.Message));
                }
            }

            AssignAutomaticBlockIds(explicitIdList, autoIdList, skippedFiles, explicitAssigned, autoAssigned, takenIds);
        }
    }
}

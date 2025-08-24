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

namespace MVGE_INF.Loaders
{
    public class TerrainLoader
    {
        // Loading
        private ushort blockTypeCounter = 0; // used only for base types
        private const ushort FIRST_CUSTOM_BLOCK_ID = 101; // IDs <=100 reserved for base / special

        public TerrainLoader()
        {
            Console.WriteLine("Terrain data loading.");

            LoadBaseBlockType();
            LoadOtherBlockTypes();

            Console.WriteLine("Terrain data finished loading.");
            Console.WriteLine($"Total block types (including base): {allBlockTypes.Count}");
        }

        internal void LoadBaseBlockType()
        {
            // Base enum block types occupy the reserved ID range starting at 0.
            foreach (BaseBlockType baseType in Enum.GetValues(typeof(BaseBlockType)))
            {
                string name = baseType.ToString();
                allBlockTypes.Add(name);
                allBlockTypesByBaseType[name] = baseType;
                allBlockTypesByIds[blockTypeCounter] = name;

                var btObj = new BlockType
                {
                    ID = blockTypeCounter,
                    UniqueName = name,
                    Name = name,
                    BaseType = baseType,
                    TextureFaceBase = name,
                    TextureFaceTop = name,
                    TextureFaceFront = name,
                    TextureFaceBack = name,
                    TextureFaceLeft = name,
                    TextureFaceRight = name,
                    TextureFaceBottom = name
                };
                allBlockTypeObjects.Add(btObj);

                Console.WriteLine("Base type defined: " + name + ", id: " + blockTypeCounter + "/65535");
                blockTypeCounter++;
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
                        TextureFaceBottom = json.TextureFaceBottom ?? json.TextureFaceBase
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
                        TextureFaceBottom = json.TextureFaceBottom ?? json.TextureFaceBase
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

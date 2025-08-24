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
        private ushort blockTypeCounter = 0;

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
            // Base enum block types do not come from JSON; we synthesize them.
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
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());

            foreach (string txtFile in txtFiles)
            {
                try
                {
                    string json = File.ReadAllText(txtFile);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        Console.WriteLine($"[TerrainLoader] Skipping empty file: {Path.GetFileName(txtFile)}");
                        continue;
                    }

                    // Deserialize into the lightweight JSON struct first
                    BlockTypeJSON? parsedJson = JsonSerializer.Deserialize<BlockTypeJSON>(json, jsonOptions);
                    if (parsedJson == null)
                    {
                        Console.WriteLine($"[TerrainLoader] Failed to deserialize file: {Path.GetFileName(txtFile)}");
                        continue;
                    }

                    // Build the runtime BlockType object (filling defaults)
                    string fileBaseName = Path.GetFileNameWithoutExtension(txtFile);
                    var rt = new BlockType
                    {
                        ID = blockTypeCounter,
                        UniqueName = fileBaseName,
                        Name = parsedJson.Value.Name,
                        BaseType = parsedJson.Value.BaseType,
                        TextureFaceBase = parsedJson.Value.TextureFaceBase,
                        TextureFaceTop = parsedJson.Value.TextureFaceTop ?? parsedJson.Value.TextureFaceBase,
                        TextureFaceFront = parsedJson.Value.TextureFaceFront ?? parsedJson.Value.TextureFaceBase,
                        TextureFaceBack = parsedJson.Value.TextureFaceBack ?? parsedJson.Value.TextureFaceBase,
                        TextureFaceLeft = parsedJson.Value.TextureFaceLeft ?? parsedJson.Value.TextureFaceBase,
                        TextureFaceRight = parsedJson.Value.TextureFaceRight ?? parsedJson.Value.TextureFaceBase,
                        TextureFaceBottom = parsedJson.Value.TextureFaceBottom ?? parsedJson.Value.TextureFaceBase
                    };

                    if (string.IsNullOrWhiteSpace(rt.Name))
                    {
                        Console.WriteLine($"[TerrainLoader] Missing Name in file: {Path.GetFileName(txtFile)}");
                        continue;
                    }

                    allBlockTypes.Add(rt.Name);
                    allBlockTypesByBaseType[rt.Name] = rt.BaseType;
                    allBlockTypesByIds[blockTypeCounter] = rt.Name;
                    allBlockTypeObjects.Add(rt);

                    Console.WriteLine($"Block type JSON loaded: {rt.Name} (unique '{rt.UniqueName}'), base type: {rt.BaseType}, id: {blockTypeCounter}/65535 (file: {Path.GetFileName(txtFile)})");
                    blockTypeCounter++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TerrainLoader] Error reading '{Path.GetFileName(txtFile)}': {ex.Message}");
                }
            }
        }

        public static List<string> allBlockTypes { get; set; } = new List<string>();
        public static Dictionary<string, BaseBlockType> allBlockTypesByBaseType { get; set; } = new Dictionary<string, BaseBlockType>();
        public static Dictionary<ushort, string> allBlockTypesByIds { get; set; } = new Dictionary<ushort, string>();
        public static List<BlockType> allBlockTypeObjects { get; set; } = new List<BlockType>();
    }
}

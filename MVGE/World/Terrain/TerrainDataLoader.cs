using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE.World.Terrain
{
    internal class TerrainDataLoader
    {
        // Constants
        public const int CHUNK_SIZE = 100;
        public const int CHUNK_MAX_HEIGHT = 100;
        public const float BLOCK_SIZE = 0.2f;

        // Loading
        private ushort blockTypeCounter = 0;

        public TerrainDataLoader()
        {
            Console.WriteLine("Terrain data loading.");

            LoadBaseBlockType();
            LoadOtherBlockTypes();

            Console.WriteLine("Terrain data finished loading.");
        }

        internal void LoadBaseBlockType()
        {
            foreach (BaseBlockType blockType in Enum.GetValues(typeof(BaseBlockType)))
            {
                allBlockTypes.Add(blockType.ToString());
                allBlockTypesByBaseType[blockType.ToString()] = blockType;
                allBlockTypesByIds[blockTypeCounter] = blockType.ToString();
                Console.WriteLine("Base type defined: " + blockType.ToString() + ", id: " + this.blockTypeCounter + "/65535");

                blockTypeCounter++;
            }
        }

        internal void LoadOtherBlockTypes()
        {
            string[] txtFiles = Directory.GetFiles(Window.settings.dataBlockTypesDirectory, "*.txt");

            foreach (string txtFile in txtFiles)
            {
                List<string> typesList = new List<string>(File.ReadAllLines(txtFile));

                string fileName = Path.GetFileNameWithoutExtension(txtFile);
                BaseBlockType blockType = GetBaseBlockTypeFromFileName(fileName);

                foreach (var str in typesList)
                {
                    allBlockTypes.Add(str);
                    allBlockTypesByBaseType[str] = blockType;
                    allBlockTypesByIds[blockTypeCounter] = str;
                    Console.WriteLine("Block type defined: " + str + ", base type: " + blockType.ToString() + ", id: " + this.blockTypeCounter + "/65535");

                    blockTypeCounter++;
                }
            }
        }

        private BaseBlockType GetBaseBlockTypeFromFileName(string fileName)
        {
            switch (fileName)
            {
                case "SoilTypes":
                    return BaseBlockType.Soil;
                case "WaterTypes":
                    return BaseBlockType.Water;
                case "StoneTypes":
                    return BaseBlockType.Stone;
                case "WoodTypes":
                    return BaseBlockType.Wood;
                case "LivingTypes":
                    return BaseBlockType.Living;
                case "MineralTypes":
                    return BaseBlockType.Mineral;
                case "LiquidTypes":
                    return BaseBlockType.Liquid;
                default:
                    return BaseBlockType.Empty;
            }
        }

        public static List<string> allBlockTypes { get; set; } = new List<string>();
        public static Dictionary<string, BaseBlockType> allBlockTypesByBaseType { get; set; } = new Dictionary<string, BaseBlockType>();
        public static Dictionary<ushort, string> allBlockTypesByIds { get; set; } = new Dictionary<ushort, string>();
    }
}

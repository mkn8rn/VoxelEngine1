using MVoxelEngine1.Infrastructure.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Generation.Biomes
{
    public class SimpleReplacementRule
    {
        public GenerationType GenerationType { get; set; } = GenerationType.SimpleReplacement;
        public int? RelativeMinDepth { get; set; }
        public int? RelativeMaxDepth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("blocks_to_replace")]
        public required List<BlockType> BlocksToReplace { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("base_blocks_to_replace")]
        public required List<BaseBlockType> BaseBlocksToReplace { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("block_type")]
        public required BlockType BlockType { get; set; }
        public required int priority { get; set; }

        public required int? microbiomeId { get; set; }

        public required int? absoluteMinYlevel { get; set; }
        public required int? absoluteMaxYlevel { get; set; }
    }
}

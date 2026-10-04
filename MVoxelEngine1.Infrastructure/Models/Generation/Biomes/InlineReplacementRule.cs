using MVoxelEngine1.Infrastructure.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Generation.Biomes
{
    public class InlineReplacementRule
    {
        [System.Text.Json.Serialization.JsonPropertyName("base_blocks_to_replace")]
        public required IList<BaseBlockType> BaseBlocksToReplace { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("block_type")]
        public required BlockType BlockType { get; set; }
        public required int priority { get; set; }

        public required int? microbiomeId { get; set; }
    }
}

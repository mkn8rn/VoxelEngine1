using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Generation.Biomes
{
    public record struct GenerationRuleJSON
    {
        // Required
        [System.Text.Json.Serialization.JsonPropertyName("generation_type")]
        public required GenerationType GenerationType { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("base_blocks_to_replace")]
        public required IList<string> BaseBlocksToReplace { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("block_type_id")]
        public required string BlockTypeId { get; set; }
        public required int priority { get; set; }

        // Target blocks (for after inline rules)
        [System.Text.Json.Serialization.JsonPropertyName("blocks_to_replace")]
        public IList<ushort>? BlocksToReplace { get; init; }

        // Biome parameters
        [System.Text.Json.Serialization.JsonPropertyName("microbiome_id")]
        public int? MicrobiomeId { get; set; }

        // Depth parameters
        [System.Text.Json.Serialization.JsonPropertyName("absolute_min_ylevel")]
        public int? AbsoluteMinYLevel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("absolute_max_ylevel")]
        public int? AbsoluteMaxYLevel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("relative_min_depth")]
        public int? RelativeMinDepth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("relative_max_depth")]
        public int? RelativeMaxDepth { get; set; }

        // Noise parameters
        [System.Text.Json.Serialization.JsonPropertyName("fill_proportion")]
        public double? FillProportion { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("noise_type")]
        public NoiseType? NoiseType { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Generation.Biomes
{
    public record struct BiomeJSON
    {
        public required int id { get; set; }
        public required string name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stone_min_ylevel")]
        public required int StoneMinYLevel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stone_max_ylevel")]
        public required int StoneMaxYLevel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stone_min_depth")]
        public required int StoneMinDepth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stone_max_depth")]
        public required int StoneMaxDepth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("soil_min_ylevel")]
        public required int SoilMinYLevel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("soil_max_ylevel")]
        public required int SoilMaxYLevel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("soil_min_depth")]
        public required int SoilMinDepth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("soil_max_depth")]
        public required int SoilMaxDepth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("water_level")]
        public required int WaterLevel { get; set; }
    }
}

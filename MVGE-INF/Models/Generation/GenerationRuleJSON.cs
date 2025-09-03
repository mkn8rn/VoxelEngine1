using MVGE_INF.Generation.Models;
using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Generation
{
    public struct GenerationRuleJSON
    {
        // Required
        public required GenerationType generation_type { get; set; }
        public required List<ushort> base_blocks_to_replace { get; set; }
        public required ushort block_type_id { get; set; }
        public required int priority { get; set; }

        // Target blocks (for after inline rules)
        public List<ushort>? blocks_to_replace { get; set; }

        // Biome parameters
        public int? microbiome_id { get; set; }

        // Depth parameters
        public int? absolute_min_ylevel { get; set; }
        public int? absolute_max_ylevel { get; set; }
        public int? relative_min_depth { get; set; }
        public int? relative_max_depth { get; set; }

        // Noise parameters
        public double? fill_proportion { get; set; }
        public NoiseType? noise_type { get; set; }
    }
}

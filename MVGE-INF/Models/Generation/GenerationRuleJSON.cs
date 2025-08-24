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
        public required List<BaseBlockType> blocks_to_replace { get; set; }
        public required BlockType block_type { get; set; }
        public required GenerationType generation_type { get; set; }
        public required int priority { get; set; }

        // Biome parameters
        public int? biome_id { get; set; }
        public int? microbiome_id { get; set; }

        // Depth parameters
        public int? absolute_min_depth { get; set; }
        public int? absolute_max_depth { get; set; }
        public int? relative_min_depth { get; set; }
        public int? relative_max_depth { get; set; }

        // Noise parameters
        public float? fill_percentage { get; set; }
        public NoiseType? noise_type { get; set; }
    }
}

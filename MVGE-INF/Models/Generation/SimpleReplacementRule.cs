using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Generation
{
    public class SimpleReplacementRule
    {
        public required List<BaseBlockType> blocks_to_replace { get; set; }
        public required BlockType block_type { get; set; }
        public required GenerationType generation_type { get; set; }
        public required int priority { get; set; }

        public required int? biome_id { get; set; }
        public required int? microbiome_id { get; set; }

        public required int? absolute_min_depth { get; set; }
        public required int? absolute_max_depth { get; set; }
        public required int? relative_min_depth { get; set; }
        public required int? relative_max_depth { get; set; }
        public required float? fill_percentage { get; set; }
    }
}

using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Generation.Biomes
{
    public class SimpleReplacementRule
    {
        public required List<BlockType> blocks_to_replace { get; set; }
        public required List<BaseBlockType> base_blocks_to_replace { get; set; }
        public required BlockType block_type { get; set; }
        public required int priority { get; set; }

        public required int? microbiomeId { get; set; }

        public required int? absoluteMinYlevel { get; set; }
        public required int? absoluteMaxYlevel { get; set; }
    }
}

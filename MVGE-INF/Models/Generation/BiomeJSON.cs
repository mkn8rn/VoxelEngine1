using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Generation.Models
{
    public struct BiomeJSON
    {
        public required int id { get; set; }
        public required string name { get; set; }
        public required int stone_min_ylevel { get; set; }
        public required int stone_max_ylevel { get; set; }
        public required int stone_min_depth { get; set; }
        public required int stone_max_depth { get; set; }
        public required int soil_min_ylevel { get; set; }
        public required int soil_max_ylevel { get; set; }
        public required int soil_min_depth { get; set; }
        public required int soil_max_depth { get; set; }
    }
}

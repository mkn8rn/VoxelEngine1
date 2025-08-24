using MVGE_INF.Generation.Models;

namespace MVGE_INF.Models.Generation
{
    public class Biome
    {
        public required int id { get; set; }
        public required string name { get; set; }
        public required int stoneMinYLevel { get; set; }
        public required int stoneMaxYLevel { get; set; }
        public required int stoneMinDepth { get; set; }
        public required int stoneMaxDepth { get; set; }
        public required int soilMinYLevel { get; set; }
        public required int soilMaxYLevel { get; set; }
        public required int soilMinDepth { get; set; }
        public required int soilMaxDepth { get; set; }
        public required List<MicrobiomeJSON> microbiomes { get; set; }
        public required List<SimpleReplacementRule> simpleReplacements { get; set; }
    }
}

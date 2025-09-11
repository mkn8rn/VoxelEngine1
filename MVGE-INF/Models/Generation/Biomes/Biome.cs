namespace MVGE_INF.Models.Generation.Biomes
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
        public required int waterLevel { get; set; }
        public required List<MicrobiomeJSON> microbiomes { get; set; }
        public required List<SimpleReplacementRule> simpleReplacements { get; set; }

        // --- Added precompiled rule data (immutable once built) ---------------------------------
        public CompiledSimpleReplacementRule[] compiledSimpleReplacementRules { get; internal set; } = Array.Empty<CompiledSimpleReplacementRule>();
        // Bucketed by section Y index (for 16-high sections). Each entry holds indices into compiledSimpleReplacementRules.
        public int[][] sectionYRuleBuckets { get; internal set; } = Array.Empty<int[]>();
    }
}

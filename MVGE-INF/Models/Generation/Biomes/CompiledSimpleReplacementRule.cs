using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;

namespace MVGE_INF.Models.Generation.Biomes
{
    /// Precompiled representation of a SimpleReplacementRule for fast batch application.
    /// Built once at biome load time. Immutable after construction.
    public sealed class CompiledSimpleReplacementRule
    {
        public readonly ushort ReplacementId;            // target block id
        public readonly ushort[] SpecificIdsSorted;      // explicit block ids to match (sorted for binary search)
        public readonly uint BaseTypeBitMask;            // bit per BaseBlockType enum value
        public readonly int MinY;                        // inclusive world y (sentinel int.MinValue if unconstrained)
        public readonly int MaxY;                        // inclusive world y (sentinel int.MaxValue if unconstrained)
        public readonly int? MicroBiomeId;               // optional microbiome filter
        public readonly int Priority;                    // priority ordering (ascending)

        public CompiledSimpleReplacementRule(ushort replacementId,
                                             ushort[] specificIdsSorted,
                                             uint baseTypeBitMask,
                                             int minY,
                                             int maxY,
                                             int? microBiomeId,
                                             int priority)
        {
            ReplacementId = replacementId;
            SpecificIdsSorted = specificIdsSorted ?? Array.Empty<ushort>();
            BaseTypeBitMask = baseTypeBitMask;
            MinY = minY;
            MaxY = maxY;
            MicroBiomeId = microBiomeId;
            Priority = priority;
        }

        public bool VerticalIntersects(int sectionY0, int sectionY1)
            => !(MaxY < sectionY0 || MinY > sectionY1);

        public bool FullyCoversSection(int sectionY0, int sectionY1)
            => MinY <= sectionY0 && MaxY >= sectionY1;

        public bool Matches(ushort id, BaseBlockType bt)
        {
            if ((BaseTypeBitMask >> (int)bt & 1u) != 0u) return true;
            if (SpecificIdsSorted.Length == 0) return false;
            return Array.BinarySearch(SpecificIdsSorted, id) >= 0;
        }
    }
}

using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;

namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeReplacementRules
{
    internal static ushort Apply(
        scoped ref NativeGameSnapshotView game,
        scoped in NativeBiomeDescriptor biome,
        ushort source,
        int worldY,
        int solidSurfaceY)
    {
        ushort result = source;
        ReadOnlySpan<NativeReplacementRule> rules = game.ReplacementRules.Slice(
            biome.ReplacementRuleOffset, biome.ReplacementRuleCount);
        foreach (ref readonly NativeReplacementRule rule in rules)
        {
            long depth = (long)solidSurfaceY - worldY;
            if (worldY < rule.MinY || worldY > rule.MaxY ||
                (rule.RelativeMinDepth != int.MinValue && depth < rule.RelativeMinDepth) ||
                (rule.RelativeMaxDepth != int.MaxValue && depth > rule.RelativeMaxDepth))
                continue;

            // Inline rules substitute the original profile material. Simple
            // rules run afterwards and can match IDs produced by earlier rules.
            ushort candidate = rule.GenerationType == GenerationType.InlineReplacement
                ? source : result;
            NativeBlockDescriptor block = game.Blocks[candidate];
            if ((rule.BaseTypeBitMask & (1u << block.BaseType)) != 0 ||
                Contains(game.SpecificBlockIds.Slice(rule.SpecificIdOffset,
                    rule.SpecificIdCount), candidate))
                result = rule.ReplacementId;
        }
        return result;
    }

    internal static int ResolveMaterials(
        scoped ref NativeGameSnapshotView game,
        scoped in NativeBiomeDescriptor biome,
        scoped in NativeTerrainMaterialSet source,
        out NativeTerrainMaterialSet resolved)
    {
        resolved = source;
        if (biome.ReplacementRuleCount == 0)
            return 0;
        foreach (ref readonly NativeReplacementRule rule in game.ReplacementRules.Slice(
                     biome.ReplacementRuleOffset, biome.ReplacementRuleCount))
        {
            if (rule.MinY != int.MinValue || rule.MaxY != int.MaxValue ||
                rule.RelativeMinDepth != int.MinValue || rule.RelativeMaxDepth != int.MaxValue)
                return 2;
        }
        if (Apply(ref game, in biome, 0, 0, 0) != 0)
            return 2;
        resolved = new NativeTerrainMaterialSet(
            game.Blocks[Apply(ref game, in biome, source.Stone.Id, 0, 0)],
            game.Blocks[Apply(ref game, in biome, source.Soil.Id, 0, 0)],
            game.Blocks[Apply(ref game, in biome, source.Water.Id, 0, 0)],
            resolved: true);
        return 1;
    }

    private static bool Contains(ReadOnlySpan<ushort> ids, ushort candidate)
    {
        int lower = 0;
        int upper = ids.Length - 1;
        while (lower <= upper)
        {
            int middle = lower + (upper - lower) / 2;
            int comparison = ids[middle].CompareTo(candidate);
            if (comparison == 0)
                return true;
            if (comparison < 0)
                lower = middle + 1;
            else
                upper = middle - 1;
        }
        return false;
    }
}

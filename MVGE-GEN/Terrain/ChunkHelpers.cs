using MVGE_INF.Loaders;
using MVGE_INF.Models.Generation;
using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        // Static cache: block id -> base block type (built on first use)
        private static Dictionary<ushort, BaseBlockType> _blockIdToBaseType;
        private static readonly object _baseTypeInitLock = new();
        private static BaseBlockType GetBaseTypeFast(ushort id)
        {
            if (_blockIdToBaseType == null)
            {
                lock (_baseTypeInitLock)
                {
                    if (_blockIdToBaseType == null)
                    {
                        var dict = new Dictionary<ushort, BaseBlockType>(TerrainLoader.allBlockTypeObjects.Count);
                        foreach (var bt in TerrainLoader.allBlockTypeObjects)
                        {
                            if (!dict.ContainsKey(bt.ID))
                                dict[bt.ID] = bt.BaseType;
                        }
                        _blockIdToBaseType = dict;
                    }
                }
            }
            if (_blockIdToBaseType.TryGetValue(id, out var baseType)) return baseType;
            if (Enum.IsDefined(typeof(BaseBlockType), (byte)id)) return (BaseBlockType)(byte)id; // fallback if simple enum id
            return BaseBlockType.Empty;
        }
        private bool RuleTargetsBlock(SimpleReplacementRule rule, ushort blockId, BaseBlockType baseType)
        {
            if (rule.blocks_to_replace != null)
            {
                for (int i = 0; i < rule.blocks_to_replace.Count; i++)
                {
                    if (rule.blocks_to_replace[i].ID == blockId) return true;
                }
            }
            if (rule.base_blocks_to_replace != null)
            {
                for (int i = 0; i < rule.base_blocks_to_replace.Count; i++)
                {
                    if (rule.base_blocks_to_replace[i] == baseType) return true;
                }
            }
            return false;
        }
        private static bool SectionFullyInside(int sectionY0, int sectionY1, int? minY, int? maxY)
        {
            if (minY.HasValue && sectionY0 < minY.Value) return false;
            if (maxY.HasValue && sectionY1 > maxY.Value) return false;
            return true;
        }
        private static bool SectionOutside(int sectionY0, int sectionY1, int? minY, int? maxY)
        {
            if (minY.HasValue && sectionY1 < minY.Value) return true;
            if (maxY.HasValue && sectionY0 > maxY.Value) return true;
            return false;
        }
    }
}

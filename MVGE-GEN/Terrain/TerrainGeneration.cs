using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVGE_INF.Models.Generation;
using MVGE_INF.Models.Terrain;

namespace MVGE_GEN.Terrain
{
    internal static class TerrainGeneration
    {
        /// Derive world-space stone & soil spans for a single (cx,cz) column given its surface height.
        /// Returns inclusive world Y spans: (-1,-1) for an absent material span. This is a simplified form
        /// of the logic inside DeriveStoneSoilSpans used for batch profile construction: it performs the
        /// stone depth allocation (respecting soil reserve & min/max depth constraints) and the soil span
        /// determination above stone, but does NOT apply any chunk-local clipping or uniform invalidation.
        internal static (int stoneStart, int stoneEnd, int soilStart, int soilEnd) DeriveWorldStoneSoilSpans(int surfaceY, Biome biome)
        {
            // Biome specifications
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel;   int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth; int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth; int stoneMaxDepthSpec = biome.stoneMaxDepth;

            // ---- Stone span ----
            int stoneBandStartWorld = stoneMinY > 0 ? stoneMinY : 0; // clamp to non-negative floor
            int stoneBandEndWorld = stoneMaxY < surfaceY ? stoneMaxY : surfaceY;
            int available = stoneBandEndWorld - stoneBandStartWorld + 1; // inclusive length
            int stoneDepth = 0;
            if (available > 0)
            {
                int soilReserve = soilMinDepthSpec; if (soilReserve < 0) soilReserve = 0; if (soilReserve > available) soilReserve = available;
                int rawStone = available - soilReserve;
                if (rawStone < stoneMinDepthSpec) rawStone = stoneMinDepthSpec;
                if (rawStone > stoneMaxDepthSpec) rawStone = stoneMaxDepthSpec;
                if (rawStone > available) rawStone = available;
                stoneDepth = rawStone > 0 ? rawStone : 0;
            }
            int finalStoneTopWorld = stoneDepth > 0 ? (stoneBandStartWorld + stoneDepth - 1) : (stoneBandStartWorld - 1);
            int stoneStart = stoneDepth > 0 ? stoneBandStartWorld : -1;
            int stoneEnd = stoneDepth > 0 ? finalStoneTopWorld : -1;

            // ---- Soil span ----
            // Soil starts immediately above stone if stone present; otherwise begins at stone band start (which for world logic
            // is also where we allowed stone to start). Then enforce biome soilMinY.
            int soilStartWorld = stoneDepth > 0 ? (finalStoneTopWorld + 1) : stoneBandStartWorld;
            if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
            int soilStart = -1, soilEnd = -1;
            if (soilStartWorld <= soilMaxY && soilStartWorld <= surfaceY)
            {
                int soilBandCapWorld = soilMaxY < surfaceY ? soilMaxY : surfaceY;
                if (soilBandCapWorld >= soilStartWorld)
                {
                    int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                    if (soilAvailable > 0)
                    {
                        int soilDepth = soilAvailable < soilMaxDepthSpec ? soilAvailable : soilMaxDepthSpec;
                        if (soilDepth > 0)
                        {
                            soilStart = soilStartWorld;
                            soilEnd = soilStartWorld + soilDepth - 1;
                        }
                    }
                }
            }

            return (stoneStart, stoneEnd, soilStart, soilEnd);
        }
    }
}

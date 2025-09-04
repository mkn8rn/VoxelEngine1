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
        // Result container for per-column span derivation.
        internal readonly struct SpanDerivationResult
        {
            public readonly bool HasStone;
            public readonly short StoneStart;
            public readonly short StoneEnd;
            public readonly bool HasSoil;
            public readonly short SoilStart;
            public readonly short SoilEnd;
            public readonly bool InvalidateStoneUniform;
            public readonly bool InvalidateSoilUniform;

            public SpanDerivationResult(bool hasStone, short stoneStart, short stoneEnd,
                                        bool hasSoil, short soilStart, short soilEnd,
                                        bool invalidateStoneUniform, bool invalidateSoilUniform)
            {
                HasStone = hasStone;
                StoneStart = stoneStart;
                StoneEnd = stoneEnd;
                HasSoil = hasSoil;
                SoilStart = soilStart;
                SoilEnd = soilEnd;
                InvalidateStoneUniform = invalidateStoneUniform;
                InvalidateSoilUniform = invalidateSoilUniform;
            }
        }

        /// Derive local (chunk-relative) stone & soil spans for a single column.
        /// Mirrors the logic previously embedded inside Chunk.GenerateInitialChunkData fused pass.
        /// Returns spans clipped to [0, chunkHeight-1]. Missing spans reported via HasStone/HasSoil = false.
        internal static SpanDerivationResult DeriveStoneSoilSpans(
            int columnSurfaceHeightWorld,
            int chunkBaseY,
            int chunkHeight,
            int topOfChunk,
            Biome biome)
        {
            // Biome vertical specs
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel;  int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth; int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth; int stoneMaxDepthSpec = biome.stoneMaxDepth;

            // Column entirely below chunk slab: no spans; both uniform candidates invalid.
            if (columnSurfaceHeightWorld < chunkBaseY)
            {
                return new SpanDerivationResult(false, 0, 0, false, 0, 0, true, true);
            }

            // ---- Stone span derivation (world space) ----
            int stoneBandStartWorld = stoneMinY > 0 ? stoneMinY : 0; // clamp floor to non-negative
            int stoneBandEndWorld   = stoneMaxY < columnSurfaceHeightWorld ? stoneMaxY : columnSurfaceHeightWorld;
            int available = stoneBandEndWorld - stoneBandStartWorld + 1; // inclusive length (may be <=0)
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

            // Stone uniform invalidation condition
            bool invalidateStoneUniform = (available <= 0 || chunkBaseY < stoneBandStartWorld || topOfChunk > finalStoneTopWorld);

            // Convert stone span to local coordinates & clip
            bool hasStone = false;
            short localStoneStart = 0, localStoneEnd = 0;
            if (stoneDepth > 0)
            {
                int localStart = stoneBandStartWorld - chunkBaseY;
                int localEnd   = finalStoneTopWorld - chunkBaseY;
                int maxLocal = chunkHeight - 1;
                if (localEnd >= 0 && localStart < chunkHeight)
                {
                    if (localStart < 0) localStart = 0; if (localEnd > maxLocal) localEnd = maxLocal;
                    if (localStart <= localEnd)
                    {
                        hasStone = true;
                        localStoneStart = (short)localStart;
                        localStoneEnd   = (short)localEnd;
                    }
                }
            }

            // ---- Soil span derivation (world space) ----
            int soilStartWorld = stoneDepth > 0 ? (finalStoneTopWorld + 1) : stoneBandStartWorld;
            if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
            int soilEndWorld = -1;
            if (soilStartWorld <= soilMaxY && soilStartWorld <= columnSurfaceHeightWorld)
            {
                int soilBandCapWorld = soilMaxY < columnSurfaceHeightWorld ? soilMaxY : columnSurfaceHeightWorld;
                if (soilBandCapWorld >= soilStartWorld)
                {
                    int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                    if (soilAvailable > 0)
                    {
                        int soilDepth = soilAvailable < soilMaxDepthSpec ? soilAvailable : soilMaxDepthSpec;
                        soilEndWorld = soilStartWorld + soilDepth - 1;
                    }
                }
            }

            // Soil uniform invalidation
            bool invalidateSoilUniform = true; // assume invalid until proven otherwise
            if (soilEndWorld >= 0)
            {
                int soilStartWorldCheck = soilStartWorld;
                invalidateSoilUniform = (soilStartWorldCheck > soilMaxY || soilEndWorld < 0 ||
                                         (chunkBaseY < soilStartWorldCheck || topOfChunk > soilEndWorld ||
                                          chunkBaseY <= (stoneDepth > 0 ? finalStoneTopWorld : (stoneBandStartWorld - 1))));
            }

            // Convert soil span to local coordinates & clip
            bool hasSoil = false;
            short localSoilStart = 0, localSoilEnd = 0;
            if (soilEndWorld >= 0)
            {
                int localStart = soilStartWorld - chunkBaseY;
                int localEnd = soilEndWorld - chunkBaseY;
                int maxLocal = chunkHeight - 1;
                if (localEnd >= 0 && localStart < chunkHeight)
                {
                    if (localStart < 0) localStart = 0; if (localEnd > maxLocal) localEnd = maxLocal;
                    if (localStart <= localEnd)
                    {
                        hasSoil = true;
                        localSoilStart = (short)localStart;
                        localSoilEnd   = (short)localEnd;
                    }
                }
            }
            // If no soil span at all, soil uniform candidate is invalid.
            if (!hasSoil) invalidateSoilUniform = true;

            return new SpanDerivationResult(
                hasStone, localStoneStart, localStoneEnd,
                hasSoil, localSoilStart, localSoilEnd,
                invalidateStoneUniform, invalidateSoilUniform);
        }
    }
}

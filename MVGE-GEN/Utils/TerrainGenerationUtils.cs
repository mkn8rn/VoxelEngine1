using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVGE_INF.Models.Generation.Biomes;
using MVGE_INF.Models.Terrain;

namespace MVGE_GEN.Terrain
{
    internal static class TerrainGenerationUtils
    {
        // ---------------- Soil smoothing constants ----------------
        // Larger => smoother, bigger coherent patches (8..24 is typical)
        private const int NoiseCellSize = 12;
        // Reduces the soil “reserve” on slopes; lower => smoother (0.2..1.00)
        private const float ReserveSlopeFactor = 0.40f;
        // Smooth noise impact on the soil reserve; lower => smoother (0.00..1.00)
        private const float ReserveNoiseAmp = 0.20f;
        // Maximum blocks to lower near-surface soil; 1..3 keeps top near surface, 7..9 is already quite harsh
        private const int MaxLowering = 6;
        // Exposure weights: reduce NoiseWeight for smoother results; keep sum ≈ 1
        private const float ExposureSlopeWeight = 0.60f;
        private const float ExposureNoiseWeight = 0.40f;

        // -------------------------------------------------------------------------
        // Derive world-space stone & soil spans for a single (cx,cz) column given its surface height.
        // Returns inclusive world Y spans: (-1,-1) for an absent material span. This is a simplified form
        // of the logic inside DeriveStoneSoilSpans used for batch profile construction: it performs the
        // stone depth allocation (respecting soil reserve & min/max depth constraints) and the soil span
        // determination above stone, but does NOT apply any chunk-local clipping or uniform invalidation.
        // -------------------------------------------------------------------------
        internal static (int stoneStart, int stoneEnd, int soilStart, int soilEnd, int waterStart, int waterEnd)
        DeriveWorldStoneSoilSpans(
            int surfaceY,
            Biome biome,
            int worldX,
            int worldZ,
            long seed,
            float slope01 = 0f)
        {
            // ---- Biome specs ----
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel; int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth; int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth; int stoneMaxDepthSpec = biome.stoneMaxDepth;

            if (slope01 < 0f) slope01 = 0f; else if (slope01 > 1f) slope01 = 1f;

            // ---- Smooth value noise in [-1,1] (low frequency) ----
            float noise01 = SmoothValueNoise01(worldX, worldZ, seed, NoiseCellSize);
            float noiseSigned = noise01 * 2f - 1f; // [-1..1]

            // ---- Stone span ----
            int stoneBandStartWorld = stoneMinY > 0 ? stoneMinY : 0;
            int stoneBandEndWorld = stoneMaxY < surfaceY ? stoneMaxY : surfaceY;
            int availableStoneBand = stoneBandEndWorld - stoneBandStartWorld + 1;

            // Locally vary the soil reserve (so stone eats more/less of the band).
            // Less reserve on steeper slopes (more exposed stone), add smooth noise.
            int effectiveReserve = 0;
            if (availableStoneBand > 0 && soilMinDepthSpec > 0)
            {
                float reserveF = soilMinDepthSpec
                                 * (1f - ReserveSlopeFactor * slope01)
                                 * (1f + ReserveNoiseAmp * noiseSigned);
                if (reserveF < 0f) reserveF = 0f;
                if (reserveF > soilMinDepthSpec) reserveF = soilMinDepthSpec;
                effectiveReserve = (int)MathF.Floor(reserveF);
                if (effectiveReserve > availableStoneBand) effectiveReserve = availableStoneBand;
                if (effectiveReserve < 0) effectiveReserve = 0;
            }

            int stoneDepth = 0;
            if (availableStoneBand > 0)
            {
                int rawStone = availableStoneBand - effectiveReserve;
                if (rawStone < stoneMinDepthSpec) rawStone = stoneMinDepthSpec;
                if (rawStone > stoneMaxDepthSpec) rawStone = stoneMaxDepthSpec;
                if (rawStone > availableStoneBand) rawStone = availableStoneBand;
                if (rawStone < 0) rawStone = 0;
                stoneDepth = rawStone;
            }

            int stoneStart = stoneDepth > 0 ? stoneBandStartWorld : -1;
            int stoneEnd = stoneDepth > 0 ? (stoneBandStartWorld + stoneDepth - 1) : -1;

            // ---- Soil span (smoothly reduced, keeps near-surface fill) ----
            int soilStartWorld = stoneDepth > 0 ? (stoneEnd + 1) : stoneBandStartWorld;
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
                        int baseSoilDepth = Math.Min(soilMaxDepthSpec, soilAvailable);

                        // Smooth, small lowering to create coherent exposed-stone patches
                        float exposure = MathF.Max(0f,
                            ExposureSlopeWeight * slope01 +
                            ExposureNoiseWeight * (-noiseSigned));

                        int lowering = (int)MathF.Floor(MaxLowering * exposure);
                        if (lowering < 0) lowering = 0;
                        if (lowering > MaxLowering) lowering = MaxLowering;

                        int soilDepth = baseSoilDepth - lowering;
                        if (soilDepth < 0) soilDepth = 0;
                        if (soilDepth > soilAvailable) soilDepth = soilAvailable;

                        if (soilDepth > 0)
                        {
                            soilStart = soilStartWorld;
                            soilEnd = soilStartWorld + soilDepth - 1;
                        }
                    }
                }
            }

            // ---- Water span (fill from actual top solid up to biome water level) ----
            // If soil lowering created air under the analytic surface and the column is underwater,
            // we must start water right above the true top solid, not at surfaceY+1.
            int waterStart = -1, waterEnd = -1;
            {
                int topSolidForWater;
                if (soilEnd >= 0 || stoneEnd >= 0)
                {
                    // Pick the highest existing solid in this column
                    topSolidForWater = Math.Max(soilEnd, stoneEnd);
                }
                else
                {
                    // No solids produced by spans; fall back to the analytic surface
                    topSolidForWater = surfaceY;
                }

                if (biome.waterLevel > topSolidForWater)
                {
                    waterStart = topSolidForWater + 1; // starts immediately above the actual top solid (or surface fallback)
                    waterEnd = biome.waterLevel;       // inclusive
                }
            }

            return (stoneStart, stoneEnd, soilStart, soilEnd, waterStart, waterEnd);

            // ------------------ Helpers ------------------
            static uint Hash(int x, int z, long s)
            {
                unchecked
                {
                    uint h = 2166136261u;
                    h ^= (uint)x; h *= 16777619u;
                    h ^= (uint)z; h *= 16777619u;
                    h ^= (uint)s; h *= 16777619u;
                    h ^= (uint)(s >> 32); h *= 16777619u;
                    h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12; h *= 0x297a2d39; h ^= h >> 15;
                    return h;
                }
            }

            // Smooth value noise in [0..1] using bilinear interpolation on a coarse integer grid
            static float SmoothValueNoise01(int x, int z, long s, int cell)
            {
                int gx = FloorDiv(x, cell);
                int gz = FloorDiv(z, cell);
                float fx = (x - gx * cell) / (float)cell;
                float fz = (z - gz * cell) / (float)cell;

                uint h00 = Hash(gx, gz, s);
                uint h10 = Hash(gx + 1, gz, s);
                uint h01 = Hash(gx, gz + 1, s);
                uint h11 = Hash(gx + 1, gz + 1, s);

                // map to [0..1]
                float v00 = (h00 & 0x3FFFFF) / 4194303f;
                float v10 = (h10 & 0x3FFFFF) / 4194303f;
                float v01 = (h01 & 0x3FFFFF) / 4194303f;
                float v11 = (h11 & 0x3FFFFF) / 4194303f;

                float vx0 = Lerp(v00, v10, SmoothStep(fx));
                float vx1 = Lerp(v01, v11, SmoothStep(fx));
                return Lerp(vx0, vx1, SmoothStep(fz));
            }

            static float Lerp(float a, float b, float t) => a + (b - a) * t;
            static float SmoothStep(float t) => t * t * (3f - 2f * t);
            static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
        }
    }
}
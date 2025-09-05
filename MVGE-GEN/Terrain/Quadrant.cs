using MVGE_INF.Managers;
using MVGE_INF.Models.Generation;
using MVGE_INF.Models.Terrain;
using MVGE_Tools.Noise;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MVGE_GEN.Terrain
{
    // Holds world‑space vertical material extents for a single (chunkX,chunkZ) column footprint.
    // Surface  : highest sampled surface height (used for fast AllAir classification).
    // Stone/Soil spans are inclusive Y world extents. A value of -1 for start/end means span absent.
    internal struct ColumnProfile
    {
        public int Surface;
        public int StoneStart;
        public int StoneEnd;  // -1 when no stone span
        public int SoilStart;
        public int SoilEnd;   // -1 when no soil span
    }

    // previously named Batch - you may see out of date comments referencing Batch.
    internal sealed class Quadrant
    {
        // ------------------------------------------------------------
        // Configuration / identity
        // ------------------------------------------------------------
        // quad size was previously 32, but reduced to 16. You may see out of date comments referencing 32.
        public const int QUAD_SIZE = 16;                                   // Horizontal footprint width in chunk units (X & Z)
        public readonly int quadX;                                         // Batch index along X in chunk space (floor(cx/32))
        public readonly int quadZ;                                         // Batch index along Z in chunk space (floor(cz/32))

        // ------------------------------------------------------------
        // Global caches (shared across all batches in process)
        // ------------------------------------------------------------
        // Noise instances cached by seed for procedural height generation.
        private static readonly ConcurrentDictionary<long, OpenSimplexNoise> _noiseCache = new();
        // Heightmap cache keyed by (seed, chunkBaseX, chunkBaseZ). Each heightmap covers one 16x16 chunk footprint.
        private static readonly ConcurrentDictionary<(long seed, int baseX, int baseZ), float[,]> _heightmapCacheGlobal = new();

        // ------------------------------------------------------------
        // Per‑batch storage
        // ------------------------------------------------------------
        private readonly Dictionary<(int cx, int cy, int cz), Chunk> _chunks = new(); // All chunk instances belonging to this batch
        private readonly HashSet<(int cx, int cz)> _generatedColumns = new();          // Tracks columns that have at least one vertical layer materialized
        private readonly object _lock = new();                                         // Coarse lock for chunk/column mutation

        // ------------------------------------------------------------
        // Biome (single biome applied to all chunks in the batch for now)
        // ------------------------------------------------------------
        public Biome Biome { get; private set; }

        // ------------------------------------------------------------
        // Column classification profiles & uniform slab inference
        // ------------------------------------------------------------
        private readonly ColumnProfile[,] _profiles = new ColumnProfile[QUAD_SIZE, QUAD_SIZE];
        private volatile bool _profilesBuilt;                    // True once every profile cell initialized
        private readonly object _profileBuildLock = new();       // Guards one‑time profile build

        // ------------------------------------------------------------
        // Batch state flags
        // ------------------------------------------------------------
        public volatile bool Dirty;                              // Marked when chunk additions/removals occur (world save grouping)
        public volatile bool GenerationComplete;                 // Reserved flag for future full batch completion signaling

        // ------------------------------------------------------------
        // Uniform classification kinds for vertical chunk slabs
        // ------------------------------------------------------------
        internal enum UniformKind
        {
            None = 0,
            AllAir = 1,
            AllStone = 2,
            AllSoil = 3
        }

        // ------------------------------------------------------------
        // Public surface API
        // ------------------------------------------------------------
        public Quadrant(int batchX, int batchZ)
        {
            this.quadX = batchX;
            this.quadZ = batchZ;
        }

        public IEnumerable<Chunk> Chunks
        {
            get
            {
                lock (_lock)
                {
                    return new List<Chunk>(_chunks.Values);
                }
            }
        }

        public bool TryGetChunk(int cx, int cy, int cz, out Chunk chunk)
        {
            lock (_lock)
            {
                return _chunks.TryGetValue((cx, cy, cz), out chunk);
            }
        }

        public void AddOrReplaceChunk(Chunk chunk, int cx, int cy, int cz)
        {
            lock (_lock)
            {
                _chunks[(cx, cy, cz)] = chunk;
                _generatedColumns.Add((cx, cz));
                Dirty = true;
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _chunks.Count == 0;
                }
            }
        }

        public void SetBiomeIfUnset(Biome biome)
        {
            if (Biome == null && biome != null)
            {
                Biome = biome;
            }
        }

        // ------------------------------------------------------------
        // Static helpers for batch indexing
        // ------------------------------------------------------------
        public static (int bx, int bz) GetBatchIndices(int cx, int cz)
        {
            static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
            return (FloorDiv(cx, QUAD_SIZE), FloorDiv(cz, QUAD_SIZE));
        }

        public static (int localX, int localZ) LocalIndices(int cx, int cz)
        {
            int localX = (int)((uint)(cx % QUAD_SIZE + QUAD_SIZE) % QUAD_SIZE);
            int localZ = (int)((uint)(cz % QUAD_SIZE + QUAD_SIZE) % QUAD_SIZE);
            return (localX, localZ);
        }

        // ------------------------------------------------------------
        // Global noise + heightmap generation & retrieval
        // ------------------------------------------------------------
        private static OpenSimplexNoise GetNoise(long seed)
        {
            return _noiseCache.GetOrAdd(seed, s => new OpenSimplexNoise(s));
        }

        // Builds a 16x16 heightmap for a chunk footprint (origin at baseX, baseZ) using current world settings.
        private static float[,] GenerateHeightMap(long seed, int baseX, int baseZ)
        {
            int maxX = GameManager.settings.chunkMaxX;
            int maxZ = GameManager.settings.chunkMaxZ;
            float[,] heightmap = new float[maxX, maxZ];
            var noise = GetNoise(seed);

            const float scale = 0.005f;
            const float minHeight = 1f;
            const float maxHeight = 1000f;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    float noiseValue = (float)noise.Evaluate((x + baseX) * scale, (z + baseZ) * scale);
                    float normalizedValue = noiseValue * 0.5f + 0.5f;
                    heightmap[x, z] = normalizedValue * (maxHeight - minHeight) + minHeight;
                }
            }
            return heightmap;
        }

        // Retrieves a cached heightmap or generates and caches if missing.
        private static float[,] GetOrCreateHeightmap(long seed, int baseWorldX, int baseWorldZ)
        {
            return _heightmapCacheGlobal.GetOrAdd((seed, baseWorldX, baseWorldZ), key => GenerateHeightMap(key.seed, key.baseX, key.baseZ));
        }

        // ------------------------------------------------------------
        // Profile construction
        // ------------------------------------------------------------
        // Builds column profiles once for the entire batch footprint using the supplied provider delegate.
        public void BuildProfiles(Func<int, int, (int surface, int stoneStart, int stoneEnd, int soilStart, int soilEnd)> provider)
        {
            if (_profilesBuilt)
            {
                return;
            }
            lock (_profileBuildLock)
            {
                if (_profilesBuilt)
                {
                    return;
                }

                for (int lx = 0; lx < QUAD_SIZE; lx++)
                {
                    for (int lz = 0; lz < QUAD_SIZE; lz++)
                    {
                        var (surface, stoneStart, stoneEnd, soilStart, soilEnd) = provider(quadX * QUAD_SIZE + lx, quadZ * QUAD_SIZE + lz);
                        _profiles[lx, lz] = new ColumnProfile
                        {
                            Surface = surface,
                            StoneStart = stoneStart,
                            StoneEnd = stoneEnd,
                            SoilStart = soilStart,
                            SoilEnd = soilEnd
                        };
                    }
                }
                _profilesBuilt = true;
            }
        }

        // ------------------------------------------------------------
        // Uniform classification for a vertical chunk slab
        // ------------------------------------------------------------
        public bool ClassifyVerticalChunk(int cy, int sizeY, out UniformKind kind)
        {
            kind = UniformKind.None;
            if (!_profilesBuilt)
            {
                return false;
            }

            int baseY = cy * sizeY;
            int topY = baseY + sizeY - 1;

            bool allAir = true;
            bool allStone = true;
            bool allSoil = true;

            for (int lx = 0; lx < QUAD_SIZE; lx++)
            {
                for (int lz = 0; lz < QUAD_SIZE; lz++)
                {
                    ref readonly ColumnProfile p = ref _profiles[lx, lz];

                    // AllAir requires this slab to sit strictly above surface for every column.
                    if (!(baseY > p.Surface))
                    {
                        allAir = false;
                    }

                    // Stone: stone span must cover entire slab range with no gap and soil must not intrude.
                    bool stoneCovers = p.StoneStart >= 0 && p.StoneStart <= baseY && p.StoneEnd >= topY;
                    if (!stoneCovers)
                    {
                        allStone = false;
                    }

                    // Soil: soil span must fully cover slab and stone not extend into slab.
                    bool soilCovers = p.SoilStart >= 0 && p.SoilStart <= baseY && p.SoilEnd >= topY && !(p.StoneEnd >= baseY);
                    if (!soilCovers)
                    {
                        allSoil = false;
                    }

                    // Early escape if all three disqualified.
                    if (!allAir && !allStone && !allSoil)
                    {
                        kind = UniformKind.None;
                        return true;
                    }
                }
            }

            if (allAir) kind = UniformKind.AllAir;
            else if (allStone) kind = UniformKind.AllStone;
            else if (allSoil) kind = UniformKind.AllSoil;
            else kind = UniformKind.None;
            return true;
        }

        // Delegate for registering newly created chunks with world dictionaries.
        public delegate void ChunkRegistrar((int cx, int cy, int cz) key, Chunk chunk, bool insideLod1);

        // ------------------------------------------------------------
        // Column generation entry point
        // ------------------------------------------------------------
        // Generates (or loads) all vertical chunk layers inside a single column of the batch.
        // Performs biome initialization (if unset), builds profiles (one‑time), classifies each slab for uniform overrides,
        // creates chunk instances, and invokes the registrar for external indexing.
        internal void GenerateOrLoadColumn(
            int cx,
            int cz,
            int playerCx,
            int playerCy,
            int playerCz,
            int lodDist,
            int verticalRange,
            long regionLimit,
            long seed,
            string chunkSaveDirectory,
            int sizeX,
            int sizeY,
            int sizeZ,
            ChunkRegistrar registrar)
        {
            // Horizontal cull (LoD1 + 1 ring).
            if (Math.Abs(cx - playerCx) > lodDist + 1 || Math.Abs(cz - playerCz) > lodDist + 1)
            {
                return;
            }

            // Biome selection (single biome per batch).
            if (Biome == null)
            {
                int worldBaseX = cx * sizeX;
                int worldBaseZ = cz * sizeZ;
                Biome = BiomeManager.SelectBiomeForChunk(seed, worldBaseX, worldBaseZ);
            }

            // Build profiles once across full 32x32 footprint.
            BuildProfiles((colCx, colCz) =>
            {
                int baseWorldX = colCx * sizeX;
                int baseWorldZ = colCz * sizeZ;
                var hm = GetOrCreateHeightmap(seed, baseWorldX, baseWorldZ);
                int localX = (int)((uint)(colCx % sizeX + sizeX) % sizeX);
                int localZ = (int)((uint)(colCz % sizeZ + sizeZ) % sizeZ);
                int surface = (int)hm[localX, localZ];
                var (stoneStart, stoneEnd, soilStart, soilEnd) = TerrainGeneration.DeriveWorldStoneSoilSpans(surface, Biome);
                return (surface, stoneStart, stoneEnd, soilStart, soilEnd);
            });

            // Shared heightmap for entire vertical stack of this column.
            int columnBaseX = cx * sizeX;
            int columnBaseZ = cz * sizeZ;
            var columnHeightmap = GetOrCreateHeightmap(seed, columnBaseX, columnBaseZ);

            int vMin = playerCy - verticalRange;
            int vMax = playerCy + verticalRange;
            if (vMin < -regionLimit) vMin = (int)-regionLimit;
            if (vMax > regionLimit) vMax = (int)regionLimit;

            for (int cy = vMin; cy <= vMax; cy++)
            {
                // Skip if chunk already exists.
                if (TryGetChunk(cx, cy, cz, out _))
                {
                    continue;
                }

                // Uniform classification -> optional override.
                UniformKind uniformKind = UniformKind.None;
                Chunk.UniformOverride overrideKind = Chunk.UniformOverride.None;
                if (ClassifyVerticalChunk(cy, sizeY, out var classified))
                {
                    uniformKind = classified;
                    if (uniformKind == UniformKind.AllAir)      overrideKind = Chunk.UniformOverride.AllAir;
                    else if (uniformKind == UniformKind.AllStone) overrideKind = Chunk.UniformOverride.AllStone;
                    else if (uniformKind == UniformKind.AllSoil)  overrideKind = Chunk.UniformOverride.AllSoil;
                }

                var worldPos = new Vector3(columnBaseX, cy * sizeY, columnBaseZ);
                float[,] hmRef = (overrideKind == Chunk.UniformOverride.None) ? columnHeightmap : null; // Only needed for non‑uniform generation
                var chunk = new Chunk(worldPos, seed, chunkSaveDirectory, hmRef, autoGenerate: true, uniformOverride: overrideKind);

                AddOrReplaceChunk(chunk, cx, cy, cz);

                bool insideLod1 = Math.Abs(cx - playerCx) <= lodDist &&
                                  Math.Abs(cz - playerCz) <= lodDist &&
                                  Math.Abs(cy - playerCy) <= verticalRange;

                registrar((cx, cy, cz), chunk, insideLod1);
            }
        }
    }
}

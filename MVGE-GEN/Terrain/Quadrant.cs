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
    // Surface  : highest sampled surface height (used for fast AllAir classification) aggregated across all block columns in the chunk.
    // Stone/Soil spans here remain the aggregated (max coverage) span used ONLY for slab uniform classification.
    // Each ColumnProfile owns the full set of per‑block (x,z) columns for that chunk (chunkMaxX * chunkMaxZ).
    internal struct ChunkColumnProfile
    {
        public int Surface;      // aggregated maximum surface in this chunk column
        public int StoneStart;   // aggregated min stone start across block columns
        public int StoneEnd;     // aggregated max stone end across block columns
        public int SoilStart;    // aggregated min soil start across block columns
        public int SoilEnd;      // aggregated max soil end across block columns

        // Full per-block column data. Length = chunkMaxX * chunkMaxZ.
        public BlockColumnProfile[] BlockColumns;
        public bool BlockColumnsBuilt; // true once BlockColumns array populated
        public bool AggregatedBuilt;   // true once aggregated (Surface/Stone/Soil) values populated lazily
    }

    // Per single BLOCK (local x,z inside a chunk) vertical column absolute world extents.
    internal struct BlockColumnProfile
    {
        public int Surface;    // world surface height for this precise block column
        public int StoneStart; // world stone span start (inclusive) or -1 absent
        public int StoneEnd;   // world stone span end (inclusive) or -1
        public int SoilStart;  // world soil span start (inclusive) or -1
        public int SoilEnd;    // world soil span end (inclusive) or -1
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

        // Seed captured when first column generated (required for lazy per-block column builds).
        private long _seed;
        private bool _seedSet;

        // ------------------------------------------------------------
        // Column classification profiles & uniform slab inference
        // ------------------------------------------------------------
        private readonly ChunkColumnProfile[,] _profiles = new ChunkColumnProfile[QUAD_SIZE, QUAD_SIZE];
        private volatile bool _profilesBuilt;                    // True once every profile cell initialized (aggregated data only) – now only set when all 256 have been lazily built
        private readonly object _profileBuildLock = new();       // Guards aggregated profile initialization (per-column lazy path)
        private int _aggregatedBuiltCount;                       // Count of aggregated column profiles built so far (when reaches QUAD_SIZE^2 -> _profilesBuilt=true)

        // ------------------------------------------------------------
        // Cached per-column block column span arrays
        // ------------------------------------------------------------
        // Key: (columnCx, columnCz) in chunk coordinates.
        // Value: array sized (chunkMaxX * chunkMaxZ) of BlockColumnProfile mapping each local (x,z) block column inside the chunk.
        // Index convention: index = localX * chunkMaxZ + localZ.
        private readonly ConcurrentDictionary<(int cx, int cz), BlockColumnProfile[]> _columnLocalSpanCache = new();

        // Stores the regionLimit/vertical chunk count used when maps were built so we can detect incompatible requests (legacy: retained, no longer used for sizing).
        private long _spanCacheRegionLimit = -1; // -1 => uninitialized
        private int _spanCacheChunkHeight = -1;

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
        // Profile construction (aggregated only)
        // ------------------------------------------------------------
        // Builds aggregated column profiles once for the entire batch footprint using the supplied provider delegate.
        // Per‑block column arrays are built lazily per chunk column on demand.
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
                        _profiles[lx, lz].Surface = surface;
                        _profiles[lx, lz].StoneStart = stoneStart;
                        _profiles[lx, lz].StoneEnd = stoneEnd;
                        _profiles[lx, lz].SoilStart = soilStart;
                        _profiles[lx, lz].SoilEnd = soilEnd;
                        _profiles[lx, lz].AggregatedBuilt = true;
                    }
                }
                _profilesBuilt = true;
                _aggregatedBuiltCount = QUAD_SIZE * QUAD_SIZE;
            }
        }

        // Lazily ensure a single aggregated column profile (Surface / Stone / Soil extents) is built.
        private void EnsureAggregatedProfile(int columnCx, int columnCz)
        {
            var (lx, lz) = LocalIndices(columnCx, columnCz);
            ref var profile = ref _profiles[lx, lz];
            if (profile.AggregatedBuilt) return;
            lock (_profileBuildLock)
            {
                if (profile.AggregatedBuilt) return;
                if (!_seedSet)
                    throw new InvalidOperationException("Seed not set before building aggregated profile.");
                if (Biome == null)
                    throw new InvalidOperationException("Biome must be set before building aggregated profile.");

                int sizeX = GameManager.settings.chunkMaxX;
                int sizeZ = GameManager.settings.chunkMaxZ;
                int baseWorldX = columnCx * sizeX;
                int baseWorldZ = columnCz * sizeZ;
                var hm = GetOrCreateHeightmap(_seed, baseWorldX, baseWorldZ);
                int localX = (int)((uint)(columnCx % sizeX + sizeX) % sizeX);
                int localZ = (int)((uint)(columnCz % sizeZ + sizeZ) % sizeZ);
                int surface = (int)hm[localX, localZ];
                var (stoneStart, stoneEnd, soilStart, soilEnd) = TerrainGeneration.DeriveWorldStoneSoilSpans(surface, Biome);
                profile.Surface = surface;
                profile.StoneStart = stoneStart;
                profile.StoneEnd = stoneEnd;
                profile.SoilStart = soilStart;
                profile.SoilEnd = soilEnd;
                profile.AggregatedBuilt = true;
                _aggregatedBuiltCount++;
                if (_aggregatedBuiltCount == QUAD_SIZE * QUAD_SIZE)
                {
                    _profilesBuilt = true; // all aggregated now
                }
            }
        }

        // Ensure per-block column data exists for the specified chunk column (lazy build).
        private void EnsureBlockColumnsBuilt(int columnCx, int columnCz)
        {
            EnsureAggregatedProfile(columnCx, columnCz); // aggregated must precede block-level build
            var (lx, lz) = LocalIndices(columnCx, columnCz);
            ref var profile = ref _profiles[lx, lz];
            if (profile.BlockColumnsBuilt)
                return;
            if (!_seedSet)
                throw new InvalidOperationException("Seed not set before building block columns.");
            if (Biome == null)
                throw new InvalidOperationException("Biome must be set before building block columns.");

            int sizeX = GameManager.settings.chunkMaxX;
            int sizeZ = GameManager.settings.chunkMaxZ;
            profile.BlockColumns = new BlockColumnProfile[sizeX * sizeZ];

            int baseWorldX = columnCx * sizeX;
            int baseWorldZ = columnCz * sizeZ;
            var hm = GetOrCreateHeightmap(_seed, baseWorldX, baseWorldZ);

            // Build per-block columns.
            for (int x = 0; x < sizeX; x++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    int surface = (int)hm[x, z];
                    var (stoneStart, stoneEnd, soilStart, soilEnd) = TerrainGeneration.DeriveWorldStoneSoilSpans(surface, Biome);
                    profile.BlockColumns[x * sizeZ + z] = new BlockColumnProfile
                    {
                        Surface = surface,
                        StoneStart = stoneStart,
                        StoneEnd = stoneEnd,
                        SoilStart = soilStart,
                        SoilEnd = soilEnd
                    };
                }
            }

            profile.BlockColumnsBuilt = true;
        }

        // ------------------------------------------------------------
        // Build / retrieve per-column block column span arrays
        // ------------------------------------------------------------
        private BlockColumnProfile[] GetOrBuildColumnSpanMap(int columnCx, int columnCz, int chunkSizeY, long regionLimit)
        {
            // Aggregated profile for this column is ensured lazily; full quadrant aggregated build no longer required here.

            // clear cache if parameters change
            if (_spanCacheRegionLimit >= 0 && (_spanCacheRegionLimit != regionLimit || _spanCacheChunkHeight != chunkSizeY))
            {
                _columnLocalSpanCache.Clear();
                _spanCacheRegionLimit = -1;
            }
            if (_spanCacheRegionLimit < 0)
            {
                _spanCacheRegionLimit = regionLimit;
                _spanCacheChunkHeight = chunkSizeY;
            }

            return _columnLocalSpanCache.GetOrAdd((columnCx, columnCz), key => GetChunkBlockMap(key.cx, key.cz, chunkSizeY, regionLimit));
        }

        private BlockColumnProfile[] GetChunkBlockMap(int columnCx, int columnCz, int chunkSizeY, long regionLimit)
        {
            // Returns the exact per-block column world spans stored in the profile (reference to underlying array).
            EnsureBlockColumnsBuilt(columnCx, columnCz);
            var (lx, lz) = LocalIndices(columnCx, columnCz);
            ref readonly ChunkColumnProfile profile = ref _profiles[lx, lz];
            return profile.BlockColumns;
        }

        // ------------------------------------------------------------
        // Uniform classification for a vertical chunk slab
        // ------------------------------------------------------------
        public bool ClassifyVerticalChunk(int cy, int sizeY, out UniformKind kind)
        {
            kind = UniformKind.None;
            // If not all aggregated profiles built yet, uniform classification is deferred (return false) to avoid partial decisions.
            if (!_profilesBuilt)
            {
                return false;
            }

            int baseY = cy * sizeY;
            int topY = baseY + sizeY - 1;

            bool allAir = true;   // Rule: slab is strictly above any material (surface) for every column.
            bool allStone = true; // Rule: slab fully covered by stone span AND no soil overlaps slab (soil absent or entirely above or below slab) for every column.
            bool allSoil = true;  // Rule: slab fully covered by soil span AND stone does NOT intrude (stoneEnd < baseY) for every column.

            for (int lx = 0; lx < QUAD_SIZE; lx++)
            {
                for (int lz = 0; lz < QUAD_SIZE; lz++)
                {
                    ref readonly ChunkColumnProfile p = ref _profiles[lx, lz];

                    // Aggregated profile must be built when _profilesBuilt is true; defensively skip if not.
                    if (!p.AggregatedBuilt)
                    {
                        kind = UniformKind.None;
                        return false;
                    }

                    // --- All Air ---
                    if (!(baseY > p.Surface))
                    {
                        allAir = false;
                    }

                    bool hasStoneSpan = p.StoneStart >= 0;
                    bool hasSoilSpan = p.SoilStart >= 0;

                    bool stoneCoversSlab = hasStoneSpan && p.StoneStart <= baseY && p.StoneEnd >= topY;
                    bool soilCoversSlab = hasSoilSpan && p.SoilStart <= baseY && p.SoilEnd >= topY;

                    bool soilOverlapsSlab = hasSoilSpan && p.SoilStart <= topY && p.SoilEnd >= baseY; // any soil voxel inside slab
                    // bool stoneOverlapsSlab = hasStoneSpan && p.StoneStart <= topY && p.StoneEnd >= baseY; // retained variable removed (not used)

                    if (!(stoneCoversSlab && !soilOverlapsSlab))
                    {
                        allStone = false;
                    }

                    bool stoneIntrudes = hasStoneSpan && p.StoneEnd >= baseY; // any stone cell at/above baseY breaks pure soil
                    if (!(soilCoversSlab && !stoneIntrudes))
                    {
                        allSoil = false;
                    }

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
        // Performs biome initialization (if unset), builds aggregated profiles (one‑time), classifies each slab for uniform overrides,
        // creates chunk instances, and invokes the registrar for external indexing. Per-block column spans are built lazily in BuildSpanMapInternal.
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
            if (Math.Abs(cx - playerCx) > lodDist + 1 || Math.Abs(cz - playerCz) > lodDist + 1)
                return;

            if (Biome == null)
            {
                int worldBaseX = cx * sizeX;
                int worldBaseZ = cz * sizeZ;
                Biome = BiomeManager.SelectBiomeForChunk(seed, worldBaseX, worldBaseZ);
            }

            if (!_seedSet)
            {
                _seed = seed;
                _seedSet = true;
            }

            // Lazily build aggregated profile only for this column (and its heightmap) – no eager full-grid build.
            EnsureAggregatedProfile(cx, cz);

            int columnBaseX = cx * sizeX;
            int columnBaseZ = cz * sizeZ;
            var columnHeightmap = GetOrCreateHeightmap(seed, columnBaseX, columnBaseZ);

            // Build & retain the span map for this (cx,cz) column; pass to every vertical chunk.
            var spanMap = GetOrBuildColumnSpanMap(cx, cz, sizeY, regionLimit);

            int vMin = playerCy - verticalRange;
            int vMax = playerCy + verticalRange;
            if (vMin < -regionLimit) vMin = (int)-regionLimit;
            if (vMax > regionLimit) vMax = (int)regionLimit;

            bool canUniformClassify = _profilesBuilt; // only attempt uniform overrides once every column aggregated

            for (int cy = vMin; cy <= vMax; cy++)
            {
                if (TryGetChunk(cx, cy, cz, out _))
                    continue;

                UniformKind uniformKind = UniformKind.None;
                Chunk.UniformOverride overrideKind = Chunk.UniformOverride.None;
                if (canUniformClassify && ClassifyVerticalChunk(cy, sizeY, out var classified))
                {
                    uniformKind = classified;
                    if (uniformKind == UniformKind.AllAir) overrideKind = Chunk.UniformOverride.AllAir;
                    else if (uniformKind == UniformKind.AllStone) overrideKind = Chunk.UniformOverride.AllStone;
                    else if (uniformKind == UniformKind.AllSoil) overrideKind = Chunk.UniformOverride.AllSoil;
                }

                var worldPos = new Vector3(columnBaseX, cy * sizeY, columnBaseZ);
                float[,] hmRef = columnHeightmap;

                var chunk = new Chunk(
                    worldPos,
                    seed,
                    chunkSaveDirectory,
                    hmRef,
                    autoGenerate: true,
                    uniformOverride: overrideKind,
                    columnSpanMap: spanMap);

                AddOrReplaceChunk(chunk, cx, cy, cz);

                bool insideLod1 = Math.Abs(cx - playerCx) <= lodDist &&
                                  Math.Abs(cz - playerCz) <= lodDist &&
                                  Math.Abs(cy - playerCy) <= verticalRange;

                registrar((cx, cy, cz), chunk, insideLod1);
            }
        }
    }
}

using MVGE_INF.Managers;
using MVGE_INF.Models.Generation.Biomes;
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

    // Compact vertical band summary for a single (chunkX,chunkZ) column across ALL vertical chunks.
    // Values are expressed in chunk layer indices (cy). These bounds allow early-out of generation for
    // empty vertical layers (no non-air material) before chunk objects are created.
    internal struct ColumnVerticalBands
    {
        public short topNonAirCy;     // highest cy containing any stone or soil voxel
        public short bottomNonAirCy;  // lowest cy containing any stone or soil voxel
        public short firstStoneCy;    // lowest cy containing stone (or -1 if absent)
        public short lastStoneCy;     // highest cy containing stone (or -1 if absent)
        public short firstSoilCy;     // lowest cy containing soil (or -1 if absent)
        public short lastSoilCy;      // highest cy containing soil (or -1 if absent)

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool HasAnyNonAir() => bottomNonAirCy <= topNonAirCy;
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
        // Cached per-column block column span arrays + vertical band summaries
        // ------------------------------------------------------------
        // Key: (columnCx, columnCz) in chunk coordinates.
        // Value: array sized (chunkMaxX * chunkMaxZ) of BlockColumnProfile mapping each local (x,z) block column inside the chunk.
        // Index convention: index = localX * chunkMaxZ + localZ.
        private readonly ConcurrentDictionary<(int cx, int cz), BlockColumnProfile[]> _columnLocalSpanCache = new();
        // Column vertical bands (always produced when BlockColumns are built). Provides early-out vertical generation bounds.
        private readonly ConcurrentDictionary<(int cx, int cz), ColumnVerticalBands> _columnVerticalBands = new();

        // Stores the regionLimit/vertical chunk count used when maps were built so we can detect incompatible requests (legacy: retained, no longer used for sizing).
        private long _spanCacheRegionLimit = -1; // -1 => uninitialized
        private int _spanCacheChunkHeight = -1;

        // ------------------------------------------------------------
        // Batch state flags
        // ------------------------------------------------------------
        public volatile bool Dirty;                              // Marked when chunk additions/removals occur (world save grouping)

        // ------------------------------------------------------------
        // Precomputed quadrant-uniform ranges for stone, soil and air chunk layers
        // ------------------------------------------------------------
        private bool _uniformRangesComputed;      // true once below ranges computed
        private int _uniformStoneFirstCy = int.MaxValue;
        private int _uniformStoneLastCy = int.MinValue;
        private int _uniformSoilFirstCy = int.MaxValue;
        private int _uniformSoilLastCy = int.MinValue;
        private int _uniformAirFirstCy = int.MaxValue;          // any cy >= this is all air (above maximum surface)

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
            static int FloorDivLocal(int a, int b) => (int)Math.Floor((double)a / b);
            return (FloorDivLocal(cx, QUAD_SIZE), FloorDivLocal(cz, QUAD_SIZE));
        }

        public static (int localX, int localZ) LocalIndices(int cx, int cz)
        {
            int localX = (int)((uint)(cx % QUAD_SIZE + QUAD_SIZE) % QUAD_SIZE);
            int localZ = (int)((uint)(cz % QUAD_SIZE + QUAD_SIZE) % QUAD_SIZE);
            return (localX, localZ);
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

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

            const float scale = 0.001f;
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
                _aggregatedBuiltCount = QUAD_SIZE * QUAD_SIZE;
                _profilesBuilt = true;
                ComputeUniformRangesIfNeeded();
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
                
                // Cheap slope (normalized to [0..1])
                int clx0 = Math.Max(localX - 1, 0), clx1 = Math.Min(localX + 1, sizeX - 1);
                int clz0 = Math.Max(localZ - 1, 0), clz1 = Math.Min(localZ + 1, sizeZ - 1);
                float dx = hm[clx1, localZ] - hm[clx0, localZ];
                float dz = hm[localX, clz1] - hm[localX, clz0];
                float grad = MathF.Sqrt(dx * dx + dz * dz);
                float slope01 = MathF.Min(1f, grad / 6f); // tune 6f to control sensitivity
                var (stoneStart, stoneEnd, soilStart, soilEnd) =

                // Derive stone and soil spans for this column
                TerrainGenerationUtils.DeriveWorldStoneSoilSpans(
                    surface,
                    Biome,
                    baseWorldX + localX,
                    baseWorldZ + localZ,
                    _seed,
                    slope01
                );

                // Store aggregated profile
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
                    ComputeUniformRangesIfNeeded();
                }
            }
        }

        // Computes quadrant-wide uniform ranges for stone, soil and air vertical chunk layers based on aggregated column extents only.
        private void ComputeUniformRangesIfNeeded()
        {
            if (_uniformRangesComputed || !_profilesBuilt) return;

            // --- Uniform Air --- (any chunk layer whose baseY > every column surface)
            int maxSurface = int.MinValue;
            for (int lx = 0; lx < QUAD_SIZE; lx++)
                for (int lz = 0; lz < QUAD_SIZE; lz++)
                    if (_profiles[lx, lz].Surface > maxSurface) maxSurface = _profiles[lx, lz].Surface;
            if (maxSurface != int.MinValue)
            {
                int chunkSizeY = GameManager.settings.chunkMaxY;
                int firstAirBaseY = maxSurface + 1;
                if (firstAirBaseY < 0) firstAirBaseY = 0;
                _uniformAirFirstCy = (firstAirBaseY + chunkSizeY - 1) / chunkSizeY; // ceilDiv
            }

            // --- Uniform Stone --- (intersection of stone spans, truncated before any soil start)
            int stoneIntersectStart = int.MinValue;
            int stoneIntersectEnd = int.MaxValue;
            bool stonePossible = true;
            for (int lx = 0; lx < QUAD_SIZE && stonePossible; lx++)
            {
                for (int lz = 0; lz < QUAD_SIZE; lz++)
                {
                    ref readonly var p = ref _profiles[lx, lz];
                    if (p.StoneStart < 0 || p.StoneEnd < p.StoneStart) { stonePossible = false; break; }
                    if (p.StoneStart > stoneIntersectStart) stoneIntersectStart = p.StoneStart;
                    if (p.StoneEnd < stoneIntersectEnd) stoneIntersectEnd = p.StoneEnd;
                }
            }
            if (stonePossible && stoneIntersectStart <= stoneIntersectEnd)
            {
                for (int lx = 0; lx < QUAD_SIZE && stoneIntersectStart <= stoneIntersectEnd; lx++)
                {
                    for (int lz = 0; lz < QUAD_SIZE; lz++)
                    {
                        ref readonly var p = ref _profiles[lx, lz];
                        if (p.SoilStart >= 0 && p.SoilStart <= stoneIntersectEnd)
                        {
                            int newEnd = p.SoilStart - 1;
                            if (newEnd < stoneIntersectEnd) stoneIntersectEnd = newEnd;
                            if (stoneIntersectStart > stoneIntersectEnd) break;
                        }
                    }
                }
                if (stoneIntersectStart <= stoneIntersectEnd)
                {
                    int chunkSizeY = GameManager.settings.chunkMaxY;
                    _uniformStoneFirstCy = FloorDiv(stoneIntersectStart, chunkSizeY);
                    _uniformStoneLastCy = FloorDiv(stoneIntersectEnd, chunkSizeY);
                }
            }

            // --- Uniform Soil ---
            // Conditions across all columns for a baseY (chunk slab bottom) to be soil uniform:
            //   baseY >= max(soilStart_i), topY <= min(soilEnd_i), and baseY > max(stoneEnd_i) (stone not intruding).
            int soilStartMax = int.MinValue;
            int soilEndMin = int.MaxValue;
            int stoneEndMax = int.MinValue; // only stone columns considered for intrusion threshold
            bool soilPossible = true;
            for (int lx = 0; lx < QUAD_SIZE && soilPossible; lx++)
            {
                for (int lz = 0; lz < QUAD_SIZE; lz++)
                {
                    ref readonly var p = ref _profiles[lx, lz];
                    if (p.SoilStart < 0 || p.SoilEnd < p.SoilStart) { soilPossible = false; break; } // any column missing soil span -> impossible
                    if (p.SoilStart > soilStartMax) soilStartMax = p.SoilStart;
                    if (p.SoilEnd < soilEndMin) soilEndMin = p.SoilEnd;
                    if (p.StoneEnd >= 0 && p.StoneEnd > stoneEndMax) stoneEndMax = p.StoneEnd;
                }
            }
            if (soilPossible && soilStartMax <= soilEndMin)
            {
                int chunkSizeY = GameManager.settings.chunkMaxY;
                // Bmin = max(soilStartMax, stoneEndMax+1), Bmax = soilEndMin - (chunkSizeY-1)
                int baseMin = soilStartMax;
                if (stoneEndMax >= 0 && stoneEndMax + 1 > baseMin) baseMin = stoneEndMax + 1;
                int baseMax = soilEndMin - (chunkSizeY - 1);
                if (baseMin <= baseMax)
                {
                    // Convert base range to cy range.
                    int firstCy = (baseMin + chunkSizeY - 1) / chunkSizeY; // ceilDiv(baseMin)
                    int lastCy = baseMax / chunkSizeY;                     // floorDiv(baseMax)
                    if (firstCy <= lastCy)
                    {
                        _uniformSoilFirstCy = firstCy;
                        _uniformSoilLastCy = lastCy;
                    }
                }
            }

            _uniformRangesComputed = true;
        }

        // Ensure per-block column data exists for the specified chunk column (lazy build).
        private void EnsureBlockColumnsBuilt(int columnCx, int columnCz)
        {
            EnsureAggregatedProfile(columnCx, columnCz); // aggregated must precede block-level build
            var (lx, lz) = LocalIndices(columnCx, columnCz);
            ref var profile = ref _profiles[lx, lz];
            if (profile.BlockColumnsBuilt) return;
            if (!_seedSet) throw new InvalidOperationException("Seed not set before building block columns.");
            if (Biome == null) throw new InvalidOperationException("Biome must be set before building block columns.");

            int sizeX = GameManager.settings.chunkMaxX;
            int sizeZ = GameManager.settings.chunkMaxZ;
            profile.BlockColumns = new BlockColumnProfile[sizeX * sizeZ];

            int baseWorldX = columnCx * sizeX;
            int baseWorldZ = columnCz * sizeZ;
            var hm = GetOrCreateHeightmap(_seed, baseWorldX, baseWorldZ);

            int stoneFirstWorld = int.MaxValue;
            int stoneLastWorld = int.MinValue;
            int soilFirstWorld = int.MaxValue;
            int soilLastWorld = int.MinValue;

            // Build per-block columns and accumulate vertical band extremes.
            for (int x = 0; x < sizeX; x++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    int surface = (int)hm[x, z];

                    // Cheap slope at (x,z)
                    int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, sizeX - 1);
                    int z0 = Math.Max(z - 1, 0), z1 = Math.Min(z + 1, sizeZ - 1);
                    float dx = hm[x1, z] - hm[x0, z];
                    float dz = hm[x, z1] - hm[x, z0];
                    float grad = MathF.Sqrt(dx * dx + dz * dz);
                    float slope01 = MathF.Min(1f, grad / 6f);

                    var (stoneStart, stoneEnd, soilStart, soilEnd) =
                        TerrainGenerationUtils.DeriveWorldStoneSoilSpans(
                            surface,
                            Biome,
                            baseWorldX + x,
                            baseWorldZ + z,
                            _seed,
                            slope01);

                    profile.BlockColumns[x * sizeZ + z] = new BlockColumnProfile
                    {
                        Surface = surface,
                        StoneStart = stoneStart,
                        StoneEnd = stoneEnd,
                        SoilStart = soilStart,
                        SoilEnd = soilEnd
                    };

                    if (stoneStart >= 0 && stoneEnd >= stoneStart)
                    {
                        if (stoneStart < stoneFirstWorld) stoneFirstWorld = stoneStart;
                        if (stoneEnd > stoneLastWorld) stoneLastWorld = stoneEnd;
                    }
                    if (soilStart >= 0 && soilEnd >= soilStart)
                    {
                        if (soilStart < soilFirstWorld) soilFirstWorld = soilStart;
                        if (soilEnd > soilLastWorld) soilLastWorld = soilEnd;
                    }
                }
            }

            // Derive column vertical bands in chunk layer (cy) indices for early-out.
            // If no material present, bottomNonAirCy > topNonAirCy (sentinel configuration).
            ColumnVerticalBands bands;
            if (stoneFirstWorld == int.MaxValue && soilFirstWorld == int.MaxValue)
            {
                bands.bottomNonAirCy = 1; // sentinel: empty range
                bands.topNonAirCy = 0;
                bands.firstStoneCy = -1;
                bands.lastStoneCy = -1;
                bands.firstSoilCy = -1;
                bands.lastSoilCy = -1;
            }
            else
            {
                int sizeY = GameManager.settings.chunkMaxY; // chunk vertical span (world units per chunk layer)
                // Convert world Y to chunk layer indices using floor division.
                int bStoneCy = stoneFirstWorld == int.MaxValue ? int.MaxValue : FloorDiv(stoneFirstWorld, sizeY);
                int tStoneCy = stoneLastWorld == int.MinValue ? int.MinValue : FloorDiv(stoneLastWorld, sizeY);
                int bSoilCy = soilFirstWorld == int.MaxValue ? int.MaxValue : FloorDiv(soilFirstWorld, sizeY);
                int tSoilCy = soilLastWorld == int.MinValue ? int.MinValue : FloorDiv(soilLastWorld, sizeY);

                int bottom = Math.Min(bStoneCy, bSoilCy);
                int top = Math.Max(tStoneCy, tSoilCy);
                if (bottom == int.MaxValue && top == int.MinValue)
                {
                    bands.bottomNonAirCy = 1;
                    bands.topNonAirCy = 0;
                }
                else
                {
                    bands.bottomNonAirCy = (short)bottom;
                    bands.topNonAirCy = (short)top;
                }
                bands.firstStoneCy = stoneFirstWorld == int.MaxValue ? (short)-1 : (short)bStoneCy;
                bands.lastStoneCy = stoneLastWorld == int.MinValue ? (short)-1 : (short)tStoneCy;
                bands.firstSoilCy = soilFirstWorld == int.MaxValue ? (short)-1 : (short)bSoilCy;
                bands.lastSoilCy = soilLastWorld == int.MinValue ? (short)-1 : (short)tSoilCy;
            }
            _columnVerticalBands[(columnCx, columnCz)] = bands;

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

        // Helper accessor for vertical bands (assumes EnsureBlockColumnsBuilt already called).
        private ColumnVerticalBands GetColumnVerticalBands(int columnCx, int columnCz)
        {
            if (_columnVerticalBands.TryGetValue((columnCx, columnCz), out var b)) return b;
            // Force build if missing (should not happen in normal flow).
            EnsureBlockColumnsBuilt(columnCx, columnCz);
            return _columnVerticalBands[(columnCx, columnCz)];
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
        // Performs biome initialization (if unset), builds aggregated profiles (one‑time), applies precomputed uniform stone overrides,
        // classifies each remaining slab for uniform overrides, creates chunk instances, and invokes the registrar for external indexing.
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
            if (!_seedSet) { _seed = seed; _seedSet = true; }

            // Build aggregated profile for this column.
            EnsureAggregatedProfile(cx, cz);

            bool aggregatedComplete = _profilesBuilt; // grid ready for uniform range overrides

            bool haveUniformStone = aggregatedComplete && _uniformStoneFirstCy <= _uniformStoneLastCy;
            bool haveUniformSoil = aggregatedComplete && _uniformSoilFirstCy <= _uniformSoilLastCy;
            bool haveUniformAir = aggregatedComplete && _uniformAirFirstCy != int.MaxValue;

            int vMin = playerCy - verticalRange;
            int vMax = playerCy + verticalRange;
            if (vMin < -regionLimit) vMin = (int)-regionLimit;
            if (vMax > regionLimit) vMax = (int)regionLimit;

            int columnBaseX = cx * sizeX;
            int columnBaseZ = cz * sizeZ;
            float[,] columnHeightmap = null;
            BlockColumnProfile[] spanMap = null;

            for (int cy = vMin; cy <= vMax; cy++)
            {
                if (TryGetChunk(cx, cy, cz, out _)) continue;

                Chunk.UniformOverride overrideKind = Chunk.UniformOverride.None;

                // Priority: AllAir > AllStone > AllSoil (above-surface emptiness first, then solid stone, then soil)
                if (haveUniformAir && cy >= _uniformAirFirstCy)
                {
                    overrideKind = Chunk.UniformOverride.AllAir;
                }
                else if (haveUniformStone && cy >= _uniformStoneFirstCy && cy <= _uniformStoneLastCy)
                {
                    overrideKind = Chunk.UniformOverride.AllStone;
                }
                else if (haveUniformSoil && cy >= _uniformSoilFirstCy && cy <= _uniformSoilLastCy)
                {
                    overrideKind = Chunk.UniformOverride.AllSoil;
                }
                else if (aggregatedComplete)
                {
                    // Fallback to full quadrant classification only when not covered by the precomputed stone range.
                    if (ClassifyVerticalChunk(cy, sizeY, out var classified))
                    {
                        if (classified == UniformKind.AllAir) overrideKind = Chunk.UniformOverride.AllAir;
                        else if (classified == UniformKind.AllStone) overrideKind = Chunk.UniformOverride.AllStone;
                        else if (classified == UniformKind.AllSoil) overrideKind = Chunk.UniformOverride.AllSoil;
                    }
                }

                // Build per-column span map only if needed for non-uniform generation path.
                if (overrideKind == Chunk.UniformOverride.None)
                {
                    columnHeightmap ??= GetOrCreateHeightmap(seed, columnBaseX, columnBaseZ);
                    spanMap ??= GetOrBuildColumnSpanMap(cx, cz, sizeY, regionLimit);
                }

                var worldPos = new Vector3(columnBaseX, cy * sizeY, columnBaseZ);
                var chunk = new Chunk(worldPos, seed, chunkSaveDirectory, autoGenerate: true, uniformOverride: overrideKind, columnSpanMap: spanMap);
                AddOrReplaceChunk(chunk, cx, cy, cz);

                bool insideLod1 = Math.Abs(cx - playerCx) <= lodDist &&
                                  Math.Abs(cz - playerCz) <= lodDist &&
                                  Math.Abs(cy - playerCy) <= verticalRange;
                registrar((cx, cy, cz), chunk, insideLod1);
            }
        }
    }
}

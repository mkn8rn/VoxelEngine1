using System;
using System.Collections.Generic;
using System.Threading;
using OpenTK.Mathematics;

namespace MVGE_GEN.Terrain
{
    // ColumnProfile captures world‑space vertical material extents for a single (chunkX,chunkZ) column footprint.
    // surface     : highest surface/sample height used for quick all‑air rejection (chunks wholly above any surface become AllAir).
    // stoneStart/stoneEnd : inclusive world Y span for stone material in this column ( -1 when absent )
    // soilStart/soilEnd   : inclusive world Y span for soil material above stone ( -1 when absent )
    internal struct ColumnProfile
    {
        public int Surface;
        public int StoneStart, StoneEnd; // -1 if none
        public int SoilStart, SoilEnd;   // -1 if none
    }

    /// A Batch groups chunks in the horizontal plane in tiles of 32 x 32 chunk coordinates (cx,cz).
    /// All vertical layers (cy) for the horizontal footprint share the same batch object.
    /// Any request that causes one chunk of the batch to be generated or loaded brings the whole batch
    /// into memory (lazy generation per chunk still allowed internally but batch stays resident).
    internal sealed class Batch
    {
        public const int BATCH_SIZE = 32; // along X and Z in chunk coordinates

        // Batch index in chunk space (floor(cx/32), floor(cz/32))
        public readonly int batchX;
        public readonly int batchZ;

        // Chunks stored by (cx,cy,cz) tuple -> instance
        private readonly Dictionary<(int cx,int cy,int cz), Chunk> _chunks = new();

        // Track which horizontal columns (cx,cz) have at least one generated vertical layer.
        private readonly HashSet<(int cx,int cz)> _generatedColumns = new();

        // Track dirty flag so world save system can flush batch as a single file
        public volatile bool Dirty;

        // Simple lock for mutating structures (coarse – perf acceptable because operations are infrequent)
        private readonly object _lock = new();

        // ---------------- Uniform classification cache ----------------
        // Profiles are built once (on demand) for the batch footprint so vertical layer classification can be done
        // without re‑deriving per‑column spans for every chunk in the stack.
        private readonly ColumnProfile[,] _profiles = new ColumnProfile[BATCH_SIZE, BATCH_SIZE];
        private volatile bool _profilesBuilt; // marked true once every footprint cell initialized
        private readonly object _profileBuildLock = new();

        // Classification result enumeration used by higher level chunk constructor to short‑circuit generation.
        internal enum UniformKind { None = 0, AllAir = 1, AllStone = 2, AllSoil = 3 }

        public Batch(int batchX, int batchZ)
        {
            this.batchX = batchX;
            this.batchZ = batchZ;
        }

        public IEnumerable<Chunk> Chunks
        {
            get { lock (_lock) return new List<Chunk>(_chunks.Values); }
        }

        public bool TryGetChunk(int cx,int cy,int cz, out Chunk chunk)
        {
            lock (_lock) return _chunks.TryGetValue((cx,cy,cz), out chunk);
        }

        public void AddOrReplaceChunk(Chunk chunk, int cx,int cy,int cz)
        {
            lock (_lock)
            {
                _chunks[(cx,cy,cz)] = chunk;
                _generatedColumns.Add((cx,cz));
                Dirty = true;
            }
        }

        public void RemoveChunk(int cx,int cy,int cz)
        {
            lock (_lock)
            {
                if (_chunks.Remove((cx,cy,cz))) Dirty = true;
                // We intentionally do not remove column marker; once generated we treat column as existing.
            }
        }

        public bool IsEmpty
        {
            get { lock (_lock) return _chunks.Count == 0; }
        }

        // Returns true if any vertical layer for (cx,cz) exists in this batch.
        public bool HasColumn(int cx,int cz)
        {
            lock (_lock) return _generatedColumns.Contains((cx,cz));
        }

        public int GeneratedColumnCount
        {
            get { lock (_lock) return _generatedColumns.Count; }
        }

        public static (int bx,int bz) GetBatchIndices(int cx,int cz)
        {
            // FloorDiv for negatives to align with existing chunk key logic
            static int FloorDiv(int a,int b) => (int)Math.Floor((double)a / b);
            return (FloorDiv(cx, BATCH_SIZE), FloorDiv(cz, BATCH_SIZE));
        }

        public static (int localX,int localZ) LocalIndices(int cx,int cz)
        {
            int localX = (int)((uint)(cx % BATCH_SIZE + BATCH_SIZE) % BATCH_SIZE);
            int localZ = (int)((uint)(cz % BATCH_SIZE + BATCH_SIZE) % BATCH_SIZE);
            return (localX, localZ);
        }

        // Indicates batch generation has completed for initial pass (all in-range vertical layers created).
        // NOTE: Hybrid incremental strategy: we do not currently set this, but retain for potential future use.
        public volatile bool GenerationComplete;

        // ---------------- Profile building & classification API ----------------
        // BuildProfiles is supplied with a delegate that returns per (cx,cz) column material spans in world space.
        // The provider must return: (surface, stoneStart, stoneEnd, soilStart, soilEnd) with -1 for absent spans.
        public void BuildProfiles(Func<int,int,(int surface,int stoneStart,int stoneEnd,int soilStart,int soilEnd)> provider)
        {
            if (_profilesBuilt) return;
            lock (_profileBuildLock)
            {
                if (_profilesBuilt) return;
                for (int lx = 0; lx < BATCH_SIZE; lx++)
                {
                    for (int lz = 0; lz < BATCH_SIZE; lz++)
                    {
                        var (surface, stoneStart, stoneEnd, soilStart, soilEnd) = provider(batchX * BATCH_SIZE + lx, batchZ * BATCH_SIZE + lz);
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

        // Classify a vertical chunk layer at chunk index cy (chunk height sizeY) using cached profiles.
        // Returns true when profiles are available and produces a UniformKind classification (or None when mixed).
        public bool ClassifyVerticalChunk(int cy, int sizeY, out UniformKind kind)
        {
            kind = UniformKind.None;
            if (!_profilesBuilt) return false;
            int baseY = cy * sizeY;
            int topY = baseY + sizeY - 1;

            bool allAir = true;
            bool allStone = true;
            bool allSoil = true;

            for (int lx = 0; lx < BATCH_SIZE; lx++)
            {
                for (int lz = 0; lz < BATCH_SIZE; lz++)
                {
                    ref readonly ColumnProfile p = ref _profiles[lx, lz];

                    // AllAir: chunk slab lies strictly above surface.
                    if (!(baseY > p.Surface)) allAir = false;

                    // Stone coverage must exist and fully span [baseY,topY] with no soil intruding inside slab.
                    bool stoneCovers = p.StoneStart >= 0 && p.StoneStart <= baseY && p.StoneEnd >= topY;
                    if (!stoneCovers) allStone = false;

                    // Soil coverage must exist and fully span [baseY,topY] and there must be no stone reaching into the slab.
                    bool soilCovers = p.SoilStart >= 0 && p.SoilStart <= baseY && p.SoilEnd >= topY && !(p.StoneEnd >= baseY);
                    if (!soilCovers) allSoil = false;

                    if (!allAir && !allStone && !allSoil)
                    {
                        kind = UniformKind.None; // early exit
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
    }
}

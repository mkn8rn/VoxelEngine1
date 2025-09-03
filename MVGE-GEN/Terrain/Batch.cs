using System;
using System.Collections.Generic;
using System.Threading;
using OpenTK.Mathematics;

namespace MVGE_GEN.Terrain
{
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
    }
}

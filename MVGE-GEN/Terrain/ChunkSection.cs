using System.Collections.Generic;

namespace MVGE_GEN.Terrain
{
    public sealed class ChunkSection
    {
        public const int SECTION_SIZE = 16;
        public const ushort AIR = 0;

        // Multi-form representation kind (added for performance optimization / decode avoidance)
        // Legacy path uses Packed when BitData+Palette in variable bits form.
        public enum RepresentationKind : byte
        {
            Empty = 0,          // All air (IsAllAir == true, no storage)
            Uniform = 1,        // Single non-air block fills all voxels (uniformBlockId)
            Sparse = 2,         // Few voxels: sparse indices list with associated block ids
            DenseExpanded = 3,  // Expanded per-voxel block ids (ushort[] expandedDense)
            Packed = 4          // Legacy bit-packed palette indices (BitData / Palette)
        }

        public RepresentationKind Kind = RepresentationKind.Empty; // default

        public bool IsAllAir = true; // kept for backward compatibility with existing checks
        public bool CompletelyFull;  // set when NonAirCount == VoxelCount during generation to allow faster uniform detection

        public List<ushort> Palette;                // index -> blockId (used for Packed or when still convenient)
        public Dictionary<ushort, int> PaletteLookup; // blockId -> palette index

        public uint[] BitData; // legacy packed storage
        public int BitsPerIndex;
        public int VoxelCount;
        public int NonAirCount;

        // Uniform representation
        public ushort UniformBlockId; // valid when Kind==Uniform

        // Sparse representation (threshold-based). We store linear indices and block ids parallel arrays to avoid dictionary lookups.
        public int[] SparseIndices;   // linear voxel indices (0..4095) of non-air voxels
        public ushort[] SparseBlocks; // same length as SparseIndices

        // Dense expanded representation: direct block ids per voxel (length == VoxelCount)
        public ushort[] ExpandedDense; // valid when Kind==DenseExpanded

        // ---- metadata for fast flatten / exposure aggregation ----
        // Occupancy bitset (4096 bits => 64 ulongs) in section-local linear index order (y,z,x mapping defined in SectionUtils.LinearIndex)
        public ulong[] OccupancyBits; // null if not built
        // Face masks (each 256 bits => 4 ulongs) for quickly computing cross-section adjacency
        public ulong[] FaceNegXBits; // YZ plane (index = z*16 + y)
        public ulong[] FacePosXBits; // YZ plane
        public ulong[] FaceNegYBits; // XZ plane (index = x*16 + z)
        public ulong[] FacePosYBits; // XZ plane
        public ulong[] FaceNegZBits; // XY plane (index = x*16 + y)
        public ulong[] FacePosZBits; // XY plane
        // Precomputed internal exposure (6*N - 2*(adjX+adjY+adjZ)) ignoring cross-section overlaps
        public int InternalExposure;
        // Local voxel bounds of solids inside section (inclusive)
        public bool HasBounds;
        public byte MinLX, MinLY, MinLZ, MaxLX, MaxLY, MaxLZ;
        // Flag indicating metadata built
        public bool MetadataBuilt;

        // ---- Incremental fast-classification helpers (optional; safe to ignore if not using fast path) ----
        public bool PreclassUniformCandidate = true; // remains true while only one non-air block id seen
        public ushort PreclassFirstBlock;            // first non-air block encountered
        public bool PreclassMultipleBlocks;          // set true once a different block encountered
        public int DistinctNonAirBlocks;             // may be used later for heuristic decisions
        public List<int> TempSparseIndices;          // collected linear indices while still under sparse threshold
        public List<ushort> TempSparseBlocks;        // parallel block ids
        public bool BoundsInitialized;               // internal flag for incremental bounds capture
        // (Future: adjacency counters / occupancy bits could be added if deeper incremental exposure needed)

        // Incremental adjacency counters (negative-side neighbour counting)
        public int AdjPairsX, AdjPairsY, AdjPairsZ;
    }
}
using MVGE_GEN.Utils; // for SectionUtils
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GEN.Models
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
        public ulong[] OccupancyBits; // null if not built
        public ulong[] FaceNegXBits; // YZ plane (index = z*16 + y)
        public ulong[] FacePosXBits; // YZ plane
        public ulong[] FaceNegYBits; // XZ plane (index = x*16 + z)
        public ulong[] FacePosYBits; // XZ plane
        public ulong[] FaceNegZBits; // XY plane (index = x*16 + y)
        public ulong[] FacePosZBits; // XY plane
        public int InternalExposure;
        public bool HasBounds;
        public byte MinLX, MinLY, MinLZ, MaxLX, MaxLY, MaxLZ;
        public bool MetadataBuilt;

        // ---- Incremental fast-classification helpers (legacy, unused in new builder but retained for compatibility) ----
        public bool PreclassUniformCandidate = true; // remains true while only one non-air block id seen
        public ushort PreclassFirstBlock;            // first non-air block encountered
        public bool PreclassMultipleBlocks;          // set true once a different block encountered
        public int DistinctNonAirBlocks;             // may be used later for heuristic decisions
        public List<int> TempSparseIndices;          // collected linear indices while still under sparse threshold
        public List<ushort> TempSparseBlocks;        // parallel block ids
        public bool BoundsInitialized;               // internal flag for incremental bounds capture
        public int AdjPairsX, AdjPairsY, AdjPairsZ;   // incremental adjacency counters (unused after refactor)

        // Strongly-typed two-phase build scratch (internal use by SectionUtils)
        internal SectionBuildScratch BuildScratch;

        private const int COLUMN_COUNT = SECTION_SIZE * SECTION_SIZE; // 256 columns of 16 voxels each

        // Scratch structures for two-phase build
        public sealed class SectionBuildScratch
        {
            public ColumnData[] Columns = new ColumnData[COLUMN_COUNT];
            public ushort[] Distinct = new ushort[8]; // up to 8 distinct ids before we give up and escalate densely
            public int DistinctCount;
            public bool AnyEscalated;
            public bool AnyNonAir;
            public bool DistinctDirty; // when true rebuild distinct list at finalize
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset()
            {
                if (DistinctCount > 0)
                    System.Array.Clear(Distinct, 0, DistinctCount);
                DistinctCount = 0; AnyEscalated = false; AnyNonAir = false; DistinctDirty = false;
                for (int i = 0; i < COLUMN_COUNT; i++) Columns[i].RunCount = 0;
            }
        }
        public struct ColumnData
        {
            public byte RunCount; // 0,1,2 or 255 for escalated
            public ushort Id0, Id1;
            public byte Y0Start, Y0End, Y1Start, Y1End; // second run only if RunCount==2
            public ushort[] Escalated; // length 16 when escalated (RunCount==255)
        }
    }
}
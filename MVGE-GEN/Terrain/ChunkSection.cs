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
    }
}
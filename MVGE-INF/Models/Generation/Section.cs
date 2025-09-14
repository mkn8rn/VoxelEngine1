using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_INF.Generation.Models
{
    public sealed class Section
    {
        public const int SECTION_SIZE = 16;
        public const int VOXELS_PER_SECTION = SECTION_SIZE * SECTION_SIZE * SECTION_SIZE;
        public const ushort AIR = 0;

        // Multi-form representation kind (added for performance optimization / decode avoidance)
        public enum RepresentationKind : byte
        {
            Empty = 0,          // All air (IsAllAir == true, no storage)
            Uniform = 1,        // Single non-air block fills all voxels (uniformBlockId)
            Sparse = 2,         // Few voxels: sparse indices list with associated block ids
            Expanded = 3,  // Expanded per-voxel block ids (ushort[] expandedDense)
            Packed = 4,         // Single-id packed (also used for 1-bit partial fill)
            MultiPacked = 5     // Multi-id low-entropy packed (palette + variable bits per index)
        }

        public RepresentationKind Kind = RepresentationKind.Empty; // default

        public bool IsAllAir = true; // kept for backward compatibility with existing checks
        public bool CompletelyFull;  // set when NonAirCount == VoxelCount during generation to allow faster uniform detection

        public List<ushort> Palette;                // index -> blockId (used for Packed or when still convenient)
        public Dictionary<ushort, int> PaletteLookup; // blockId -> palette index

        public uint[] BitData; // packed storage
        public int BitsPerIndex;
        public int VoxelCount;
        public int OpaqueVoxelCount; // used to be non air count
        public int NonAirCount;      // includes transparent

        // Uniform representation
        public ushort UniformBlockId; // valid when Kind==Uniform

        // Dense expanded representation: direct block ids per voxel (length == VoxelCount)
        public ushort[] ExpandedDense; // valid when Kind==DenseExpanded

        // ---- metadata for fast flatten / exposure aggregation ----
        public ulong[] OpaqueBits; // null if not built (previously occupancybits)
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

        // ---- transparent voxel tracking (non-opaque, non-air). Populated in finalize steps.
        public ulong[] TransparentBits;            // bits set where voxel is transparent (e.g. water, glass). Uniform transparent fills all bits.
        public int TransparentCount;               // count of transparent voxels
        public ulong[] TransparentFaceNegXBits;    // optional per-face transparent boundary masks (same layout as opaque)
        public ulong[] TransparentFacePosXBits;
        public ulong[] TransparentFaceNegYBits;
        public ulong[] TransparentFacePosYBits;
        public ulong[] TransparentFaceNegZBits;
        public ulong[] TransparentFacePosZBits;
        public int[] TransparentPaletteIndices;    // palette indices whose block ids are transparent (non-air)
        public int[] TransparentSparseIndices;     // transparent-only indices for sparse sections (built on demand)

        // ---- explicit air (empty) tracking convenience (id == 0) ----
        public ulong[] EmptyBits;                  // bits set where voxel is air
        public int EmptyCount;                     // number of air voxels

        // Convenience flags
        public bool HasTransparent;
        public bool HasAir;

        // Strongly-typed two-phase build scratch (internal use by SectionUtils)
        public SectionBuildScratch BuildScratch;

        public bool BoundingBoxDirty; // legacy (unused currently)
        public bool StructuralDirty;   // geometry / occupancy changed (requires full finalize)
        public bool IdMapDirty;        // only ids changed (same occupancy) -> cheap finalize path
    }
}
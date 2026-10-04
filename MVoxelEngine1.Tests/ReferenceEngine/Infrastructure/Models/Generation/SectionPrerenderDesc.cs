using MVoxelEngine1.Infrastructure.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Generation
{
    public struct SectionPrerenderDesc
    {
        public byte Kind; // representation kind (0 Empty,1 Uniform,3 Expanded,4 Packed,5 MultiPacked)
        public ushort UniformBlockId;
        public int OpaqueCount;
        public ushort[] ExpandedDense;
        public uint[] PackedBitData;
        public List<ushort> Palette;
        public int BitsPerIndex;
        public ulong[] OpaqueBits; // opaque voxel occupancy
        public ulong[] FaceNegXBits;
        public ulong[] FacePosXBits;
        public ulong[] FaceNegYBits;
        public ulong[] FacePosYBits;
        public ulong[] FaceNegZBits;
        public ulong[] FacePosZBits;
        // ---- transparent voxel data (non-opaque, non-air) ----
        public int TransparentCount; // residual transparent voxel count (after dominant split if any)
        public ulong[] TransparentBits; // residual transparent bits (after dominant split if any)
        public ulong[] TransparentFaceNegXBits;
        public ulong[] TransparentFacePosXBits;
        public ulong[] TransparentFaceNegYBits;
        public ulong[] TransparentFacePosYBits;
        public ulong[] TransparentFaceNegZBits;
        public ulong[] TransparentFacePosZBits;
        public int[] TransparentPaletteIndices; // palette indices whose block ids are transparent (non-air)
        // Dominant transparent splitting (single-id fast path). When DominantTransparentId != 0 it represents the
        // transparent id whose voxels account for the majority share; DominantTransparentBits contains only those voxels.
        public ushort DominantTransparentId; // 0 when no dominant id chosen
        public int DominantTransparentCount; // number of voxels in DominantTransparentBits
        public ulong[] DominantTransparentBits; // bitset for dominant transparent id only (null if none)
        // Cached per-face tile indices for transparent palette ids: 6 entries per transparent palette index (NX, PX, NY, PY, NZ, PZ)
        public uint[] TransparentPaletteFaceTiles; // length == TransparentPaletteIndices.Length * 6 when not null
        // ---- explicit air tracking ----
        public int EmptyCount; // air voxel count
        public ulong[] EmptyBits; // bits for air voxels
        public bool HasBounds;
        public byte MinLX, MinLY, MinLZ, MaxLX, MaxLY, MaxLZ;
        public int SectionBaseX, SectionBaseY, SectionBaseZ; // world-local base
    }
}

using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Generation
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
        public int TransparentCount;                 // residual transparent voxel count (after dominant split if any)
        public ulong[] TransparentBits;              // residual transparent bits (after dominant split if any)
        public ulong[] TransparentFaceNegXBits;
        public ulong[] TransparentFacePosXBits;
        public ulong[] TransparentFaceNegYBits;
        public ulong[] TransparentFacePosYBits;
        public ulong[] TransparentFaceNegZBits;
        public ulong[] TransparentFacePosZBits;
        public int[] TransparentPaletteIndices;      // palette indices whose block ids are transparent (non-air)

        // Dominant transparent splitting (single-id fast path). When DominantTransparentId != 0 it represents the
        // transparent id whose voxels account for the majority share; DominantTransparentBits contains only those voxels.
        public ushort DominantTransparentId;         // 0 when no dominant id chosen
        public int DominantTransparentCount;         // number of voxels in DominantTransparentBits
        public ulong[] DominantTransparentBits;      // bitset for dominant transparent id only (null if none)

        // Cached per-face tile indices for transparent palette ids: 6 entries per transparent palette index (NX, PX, NY, PY, NZ, PZ)
        public uint[] TransparentPaletteFaceTiles;   // length == TransparentPaletteIndices.Length * 6 when not null

        // ---- explicit air tracking ----
        public int EmptyCount;        // air voxel count
        public ulong[] EmptyBits;     // bits for air voxels

        public bool HasBounds;
        public byte MinLX, MinLY, MinLZ, MaxLX, MaxLY, MaxLZ;
        public int SectionBaseX, SectionBaseY, SectionBaseZ; // world-local base
    }

    // Container for all pre-render flags and cached plane data passed from Chunk -> ChunkRender.
    public struct ChunkPrerenderData
    {
        // Face solidity flags for this chunk
        public bool FaceNegX;
        public bool FacePosX;
        public bool FaceNegY;
        public bool FacePosY;
        public bool FaceNegZ;
        public bool FacePosZ;

        // Neighbor opposing face solidity flags
        public bool NeighborNegXPosX;
        public bool NeighborPosXNegX;
        public bool NeighborNegYPosY;
        public bool NeighborPosYNegY;
        public bool NeighborNegZPosZ;
        public bool NeighborPosZNegZ;

        // Uniform single-block fast path flags
        public bool AllOneBlock;
        public ushort AllOneBlockId;

        // Prepass stats
        public int PrepassSolidCount;
        public int PrepassExposureEstimate;

        // Neighbor plane caches (bitsets). Null if neighbor absent / not cached.
        // Layouts follow existing renderer conventions:
        //  Neg/Pos X: YZ plane (index = z * dimY + y)
        //  Neg/Pos Y: XZ plane (index = x * dimZ + z)
        //  Neg/Pos Z: XY plane (index = x * dimY + y)
        public ulong[] NeighborPlaneNegX; // neighbor at -X, its +X face (opaque)
        public ulong[] NeighborPlanePosX; // neighbor at +X, its -X face (opaque)
        public ulong[] NeighborPlaneNegY; // neighbor at -Y, its +Y face (opaque)
        public ulong[] NeighborPlanePosY; // neighbor at +Y, its -Y face (opaque)
        public ulong[] NeighborPlaneNegZ; // neighbor at -Z, its +Z face (opaque)
        public ulong[] NeighborPlanePosZ; // neighbor at +Z, its -Z face (opaque)

        // Self plane caches (this chunk's opaque boundary planes)
        public ulong[] SelfPlaneNegX;
        public ulong[] SelfPlanePosX;
        public ulong[] SelfPlaneNegY;
        public ulong[] SelfPlanePosY;
        public ulong[] SelfPlaneNegZ;
        public ulong[] SelfPlanePosZ;

        // Transparent boundary id maps for this chunk (ushort ids, 0 = none). Layouts mirror opaque planes.
        public ushort[] SelfTransparentPlaneNegX;
        public ushort[] SelfTransparentPlanePosX;
        public ushort[] SelfTransparentPlaneNegY;
        public ushort[] SelfTransparentPlanePosY;
        public ushort[] SelfTransparentPlaneNegZ;
        public ushort[] SelfTransparentPlanePosZ;

        // Neighbor transparent boundary id maps (faces adjacent to this chunk). Null if neighbor missing or no transparent ids present.
        // Mapping: NeighborTransparentPlaneNegX = neighbor -X chunk's +X transparent plane, etc.
        public ushort[] NeighborTransparentPlaneNegX;
        public ushort[] NeighborTransparentPlanePosX;
        public ushort[] NeighborTransparentPlaneNegY;
        public ushort[] NeighborTransparentPlanePosY;
        public ushort[] NeighborTransparentPlaneNegZ;
        public ushort[] NeighborTransparentPlanePosZ;

        public ChunkData chunkData;

        public object SectionRender;

        public SectionPrerenderDesc[] SectionDescs;
        public int sectionsX, sectionsY, sectionsZ, sectionSize;
        public int maxX;
        public int maxY;
        public int maxZ;
    }
}

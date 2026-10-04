using MVoxelEngine1.Infrastructure.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Generation
{
    // Container for all pre-render flags and cached plane data passed from Chunk -> ChunkRender.
    internal struct ChunkPrerenderData
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
        public ulong[]? NeighborPlaneNegX; // neighbor at -X, its +X face (opaque)
        public ulong[]? NeighborPlanePosX; // neighbor at +X, its -X face (opaque)
        public ulong[]? NeighborPlaneNegY; // neighbor at -Y, its +Y face (opaque)
        public ulong[]? NeighborPlanePosY; // neighbor at +Y, its -Y face (opaque)
        public ulong[]? NeighborPlaneNegZ; // neighbor at -Z, its +Z face (opaque)
        public ulong[]? NeighborPlanePosZ; // neighbor at +Z, its -Z face (opaque)
        // Self plane caches (this chunk's opaque boundary planes)
        public ulong[]? SelfPlaneNegX;
        public ulong[]? SelfPlanePosX;
        public ulong[]? SelfPlaneNegY;
        public ulong[]? SelfPlanePosY;
        public ulong[]? SelfPlaneNegZ;
        public ulong[]? SelfPlanePosZ;
        // Transparent boundary id maps for this chunk (ushort ids, 0 = none). Layouts mirror opaque planes.
        public ushort[]? SelfTransparentPlaneNegX;
        public ushort[]? SelfTransparentPlanePosX;
        public ushort[]? SelfTransparentPlaneNegY;
        public ushort[]? SelfTransparentPlanePosY;
        public ushort[]? SelfTransparentPlaneNegZ;
        public ushort[]? SelfTransparentPlanePosZ;
        // Neighbor transparent boundary id maps (faces adjacent to this chunk). Null if neighbor missing or no transparent ids present.
        // Mapping: NeighborTransparentPlaneNegX = neighbor -X chunk's +X transparent plane, etc.
        public ushort[]? NeighborTransparentPlaneNegX;
        public ushort[]? NeighborTransparentPlanePosX;
        public ushort[]? NeighborTransparentPlaneNegY;
        public ushort[]? NeighborTransparentPlanePosY;
        public ushort[]? NeighborTransparentPlaneNegZ;
        public ushort[]? NeighborTransparentPlanePosZ;
        public ChunkData chunkData;
        public GeneratedChunkSpanData? GeneratedSpans;
        public SectionPrerenderDesc[]? SectionDescs;
        public int sectionsX, sectionsY, sectionsZ, sectionSize;
        public int maxX;
        public int maxY;
        public int maxZ;

        internal readonly SectionPrerenderDesc[] RequireSectionDescriptions() => SectionDescs ?? throw new InvalidOperationException("The section render descriptions have not been built.");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Generation
{
    // Container for all pre-render flags and cached plane data passed from Chunk -> ChunkRender.
    // NOTE: Pure data transfer object; no logic here. Rendering logic remains unchanged.
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
        public ulong[] NeighborPlaneNegX; // neighbor at -X, its +X face
        public ulong[] NeighborPlanePosX; // neighbor at +X, its -X face
        public ulong[] NeighborPlaneNegY; // neighbor at -Y, its +Y face
        public ulong[] NeighborPlanePosY; // neighbor at +Y, its -Y face
        public ulong[] NeighborPlaneNegZ; // neighbor at -Z, its +Z face
        public ulong[] NeighborPlanePosZ; // neighbor at +Z, its -Z face

        // Self plane caches (this chunk's boundary planes) always using same layouts.
        public ulong[] SelfPlaneNegX;
        public ulong[] SelfPlanePosX;
        public ulong[] SelfPlaneNegY;
        public ulong[] SelfPlanePosY;
        public ulong[] SelfPlaneNegZ;
        public ulong[] SelfPlanePosZ;
    }
}

using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        /// Emits face instances for a DenseExpanded (Kind==3) section using pre-computed
        /// occupancy + boundary plane masks (now generated during section finalization).
        /// Steps:
        ///  1. Produce internal face bitsets by shifting occupancy and removing occluded pairs
        ///     (skipping boundary voxels so boundary handling is centralized) now via BuildInternalFaceMasks.
        ///  2. Re-introduce boundary faces by consulting boundary voxel bitsets and external
        ///     chunk neighbor planes / adjacent section voxels using AddVisibleBoundaryFaces.
        ///  3. Iterate each face direction mask and emit an instance for every surviving bit
        ///     using EmitFacesFromMask with a TileIndexCache for per-voxel tiles.
        /// Returns true if handled; false signals fallback brute scan.
        private bool EmitDenseExpandedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            // Quick rejection / no-op
            if (desc.NonAirCount == 0 || desc.Kind != 3)
                return true; // nothing to emit (handled)

            // If occupancy is somehow missing (should be rare after refactor) fall back
            if (desc.OccupancyBits == null)
                return false; // ask caller to use brute fallback path

            // Initialize shared masks / decode tables.
            EnsureBoundaryMasks(); // direct call (legacy EnsureMasks retained but unused here)
            EnsureLiDecode();      // decode tables (lx, ly, lz)

            var occ = desc.OccupancyBits;          // 64 ulongs for 4096 voxels
            ushort[] dense = desc.ExpandedDense;   // dense block id array (length 4096)

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

            // Resolve local bounds to restrict work to tight region if present
            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);

            // Directional face masks (internal + boundary)
            Span<ulong> faceNX = stackalloc ulong[64]; // -X faces
            Span<ulong> facePX = stackalloc ulong[64]; // +X faces
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y faces
            Span<ulong> facePY = stackalloc ulong[64]; // +Y faces
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z faces
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z faces

            // 1. Internal faces (excluding outer boundary layers) using shared helper
            BuildInternalFaceMasks(occ, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

            // 2. Add boundary faces (only exposed ones) using shared helper
            AddVisibleBoundaryFaces(ref desc,
                                    baseX, baseY, baseZ,
                                    lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                                    data.SectionDescs,
                                    sx, sy, sz,
                                    data.sectionsX, data.sectionsY, data.sectionsZ,
                                    faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                                    data);

            // 3. Emit: per-voxel tile index caching to avoid repeated atlas lookups for same block & face
            var tileCache = new TileIndexCache();
            var localDense = dense; // local copy for lambda capture

            // Per-voxel emission lambda used by EmitFacesFromMask (returns block + tile index). Guard for bounds and air.
            (ushort block, uint tileIndex) PerVoxelDense(int li, int lx, int ly, int lz, byte faceDir)
            {
                if (localDense == null) return (0, 0);
                ushort b = localDense[li];
                if (b == 0) return (0, 0);
                uint tIndex = tileCache.Get(atlas, b, faceDir);
                return (b, tIndex);
            }

            // Emit for each direction using shared helper with per-voxel lambda
            EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxelDense(li, lx, ly, lz, 0), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxelDense(li, lx, ly, lz, 1), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxelDense(li, lx, ly, lz, 2), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxelDense(li, lx, ly, lz, 3), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxelDense(li, lx, ly, lz, 4), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxelDense(li, lx, ly, lz, 5), offsetList, tileIndexList, faceDirList);

            return true;
        }
    }
}

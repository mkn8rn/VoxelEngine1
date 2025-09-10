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
        /// Emits faces for a Packed section (Kind == 4).
        /// Packed: single-id palette packed bit representation
        private bool EmitSinglePackedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            // Preconditions: must be single‑id packed (BitsPerIndex=1, palette[1] is block id) with occupancy.
            if (desc.OccupancyBits == null || desc.NonAirCount == 0) return false;
            if (desc.Palette == null || desc.Palette.Count < 2 || desc.BitsPerIndex != 1) return false;

            EnsureBoundaryMasks();
            EnsureLiDecode();

            var occ = desc.OccupancyBits;          // 64 ulongs (4096 bits) occupancy
            ushort block = desc.Palette[1];        // single non‑air block id

            // Precompute per-face tile indices and reuse (fast path if all faces identical)
            Span<uint> faceTiles = stackalloc uint[6];
            PrecomputePerFaceTiles(block, out bool sameTileAllFaces, out uint singleTileIndex, faceTiles);

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

            // Resolve tight local bounds (lx/ly/lz) if present
            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);

            // Neighbor section descriptors (only for cross‑section boundary occlusion)
            int sxCount = data.sectionsX; int syCount = data.sectionsY; int szCount = data.sectionsZ;
            SectionPrerenderDesc[] allSecs = data.SectionDescs;

            // Face bitsets (internal faces + later added visible boundary faces)
            Span<ulong> faceNX = stackalloc ulong[64]; // -X
            Span<ulong> facePX = stackalloc ulong[64]; // +X
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y
            Span<ulong> facePY = stackalloc ulong[64]; // +Y
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z

            // 1. Internal faces (exclude boundary layers first) via shared helper
            BuildInternalFaceMasks(occ, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

            // 2. Boundary faces: add only visible boundary voxels (shared helper consolidates previous six similar loops)
            AddVisibleBoundaryFaces(ref desc,
                                    baseX, baseY, baseZ,
                                    lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                                    allSecs,
                                    sx, sy, sz,
                                    sxCount, syCount, szCount,
                                    faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                                    data);

            // 3. Emit faces from masks (single tile fast path or per-face tiles)
            if (sameTileAllFaces)
            {
                EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, singleTileIndex, offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, singleTileIndex, offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, singleTileIndex, offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, singleTileIndex, offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, singleTileIndex, offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, singleTileIndex, offsetList, tileIndexList, faceDirList);
            }
            else
            {
                EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTiles[0], offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTiles[1], offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTiles[2], offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTiles[3], offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTiles[4], offsetList, tileIndexList, faceDirList);
                EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTiles[5], offsetList, tileIndexList, faceDirList);
            }

            return true;
        }
    }
}

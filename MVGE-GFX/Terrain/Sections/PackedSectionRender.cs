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
        /// Emits face instances for a Packed single‑id section (Kind==4) using occupancy + precomputed boundary data.
        /// Preconditions:
        ///   - desc.OccupancyBits != null
        ///   - desc.BitsPerIndex == 1 and palette[1] is the single non‑air block
        /// Steps:
        ///  1. Resolve tight local voxel bounds (ResolveLocalBounds) to minimize work when section has partial fill metadata.
        ///  2. Build internal face masks (BuildInternalFaceMasks) to mark faces between occupied and non‑occupied voxels,
        ///     excluding outer boundary layers (those are added conditionally later).
        ///  3. Fast neighbor full‑solid classification (lightweight) marks faces that are completely occluded by a fully solid neighbor
        ///     so boundary face reinsertion and later emission for those directions are skipped entirely (internal mask bits for those
        ///     faces remain but are ignored to reduce per-bit enumeration cost).
        ///  4. Reintroduce only visible boundary faces (AddVisibleBoundaryFacesSelective) by testing neighbor sections and world
        ///     boundary plane bitsets (suppresses faces hidden by adjacent solid content) while honoring skip flags from step 3.
        ///  5. Fetch per‑face tile indices from the shared uniform face tile cache (GetUniformFaceTileSet) to avoid recomputing
        ///     atlas lookups for repeated block ids; if all six faces share a tile, use a fast single‑tile path.
        ///  6. Emit instances per direction (EmitFacesFromMask) for non‑skipped faces with either a single shared tile index or per‑face tile indices.
        /// Returns true if the packed fast path handled emission; false signals caller to attempt another path.
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

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

            // Resolve tight local bounds (lx/ly/lz) if present
            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);

            // Neighbor section descriptors (only for cross‑section boundary occlusion and full-solid classification)
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

            // 2. Neighbor full-solid fast classification (skip flags). Order: 0..5 (-X,+X,-Y,+Y,-Z,+Z)
            Span<bool> skipFace = stackalloc bool[6]; // initialized false
            // LEFT neighbor (-X)
            if (sx > 0)
            {
                ref var nLeft = ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)];
                if (NeighborFullySolid(ref nLeft)) skipFace[0] = true;
            }
            // RIGHT neighbor (+X)
            if (sx + 1 < sxCount)
            {
                ref var nRight = ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)];
                if (NeighborFullySolid(ref nRight)) skipFace[1] = true;
            }
            // DOWN neighbor (-Y)
            if (sy > 0)
            {
                ref var nDown = ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)];
                if (NeighborFullySolid(ref nDown)) skipFace[2] = true;
            }
            // UP neighbor (+Y)
            if (sy + 1 < syCount)
            {
                ref var nUp = ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)];
                if (NeighborFullySolid(ref nUp)) skipFace[3] = true;
            }
            // BACK neighbor (-Z)
            if (sz > 0)
            {
                ref var nBack = ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)];
                if (NeighborFullySolid(ref nBack)) skipFace[4] = true;
            }
            // FRONT neighbor (+Z)
            if (sz + 1 < szCount)
            {
                ref var nFront = ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)];
                if (NeighborFullySolid(ref nFront)) skipFace[5] = true;
            }

            // 3. Boundary faces: add only visible boundary voxels, respecting skip flags for fully occluded faces.
            AddVisibleBoundaryFacesSelective(ref desc,
                                             baseX, baseY, baseZ,
                                             lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                                             allSecs,
                                             sx, sy, sz,
                                             sxCount, syCount, szCount,
                                             skipFace,
                                             faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                                             data);

            // 4. Per-face tile indices via shared cache used also by uniform path.
            var tileSet = GetUniformFaceTileSet(block);
            bool sameTileAllFaces = tileSet.AllSame;
            uint[] faceTilesCached = sameTileAllFaces
                ? new uint[] { tileSet.SingleTile, tileSet.SingleTile, tileSet.SingleTile, tileSet.SingleTile, tileSet.SingleTile, tileSet.SingleTile }
                : new uint[] { tileSet.TileNX, tileSet.TilePX, tileSet.TileNY, tileSet.TilePY, tileSet.TileNZ, tileSet.TilePZ };

            // 5. Emit faces from masks (respect skip flags). Internal mask bits for skipped faces are ignored.
            if (sameTileAllFaces)
            {
                if (!skipFace[0]) EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                if (!skipFace[1]) EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                if (!skipFace[2]) EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                if (!skipFace[3]) EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                if (!skipFace[4]) EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                if (!skipFace[5]) EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
            }
            else
            {
                if (!skipFace[0]) EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[0], offsetList, tileIndexList, faceDirList);
                if (!skipFace[1]) EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[1], offsetList, tileIndexList, faceDirList);
                if (!skipFace[2]) EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[2], offsetList, tileIndexList, faceDirList);
                if (!skipFace[3]) EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[3], offsetList, tileIndexList, faceDirList);
                if (!skipFace[4]) EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[4], offsetList, tileIndexList, faceDirList);
                if (!skipFace[5]) EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[5], offsetList, tileIndexList, faceDirList);
            }

            return true;
        }
    }
}

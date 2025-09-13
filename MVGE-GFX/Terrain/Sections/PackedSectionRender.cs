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
        ///  3. Fast neighbor full‑solid classification marks directions whose boundary layer would be fully
        ///     occluded by a fully solid neighbor; ONLY boundary face reinsertion is skipped for those directions. Internal
        ///     faces (already computed in step 2) are still emitted for all directions to preserve walls of internal cavities.
        ///  4. Reintroduce only visible boundary faces (AddVisibleBoundaryFacesSelective) by testing neighbor sections and world
        ///     boundary plane bitsets (suppresses faces hidden by adjacent solid content) while honoring boundary skip flags
        ///     from step 3.
        ///  5. Apply bounds mask trimming (ApplyBoundsMask) when section has tight bounds, allowing subsequent emission without
        ///     per-bit bounds comparisons (EmitFacesFromMaskNoBounds fast path) for remaining masks.
        ///  6. Fetch per‑face tile indices from the shared uniform face tile cache (GetUniformFaceTileSet) to avoid recomputing
        ///     atlas lookups for repeated block ids; if all six faces share a tile, use a fast single‑tile path.
        ///  7. Emit instances per direction. Emission no longer checks the boundary skip flags; those affected only boundary
        ///     augmentation. Internal face masks always participate in emission if they contain bits.
        /// Returns true if the packed fast path handled emission; false signals caller to attempt another path.
        private bool EmitSinglePackedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            // Preconditions: must be single‑id packed (BitsPerIndex=1, palette[1] is block id) with occupancy.
            if (desc.OpaqueBits == null || desc.OpaqueCount == 0) return false;
            if (desc.Palette == null || desc.Palette.Count < 2 || desc.BitsPerIndex != 1) return false;

            EnsureBoundaryMasks();
            EnsureLiDecode();

            var occ = desc.OpaqueBits;          // 64 ulongs (4096 bits) occupancy
            ushort block = desc.Palette[1];        // single non‑air block id

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

            // Resolve tight local bounds (lx/ly/lz) if present
            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);
            bool hasTightBounds = !(lxMin == 0 && lxMax == 15 && lyMin == 0 && lyMax == 15 && lzMin == 0 && lzMax == 15);

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

            // 2. Neighbor full-solid fast classification (boundary skip flags). Order: 0..5 (-X,+X,-Y,+Y,-Z,+Z)
            Span<bool> skipBoundary = stackalloc bool[6]; // initialized false
            if (sx > 0)        { ref var nLeft  = ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)];  if (NeighborFullySolid(ref nLeft))  skipBoundary[0] = true; }
            if (sx + 1 < sxCount) { ref var nRight = ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)]; if (NeighborFullySolid(ref nRight)) skipBoundary[1] = true; }
            if (sy > 0)        { ref var nDown  = ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)];  if (NeighborFullySolid(ref nDown))  skipBoundary[2] = true; }
            if (sy + 1 < syCount) { ref var nUp    = ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)]; if (NeighborFullySolid(ref nUp))    skipBoundary[3] = true; }
            if (sz > 0)        { ref var nBack  = ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)];  if (NeighborFullySolid(ref nBack))  skipBoundary[4] = true; }
            if (sz + 1 < szCount) { ref var nFront = ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)]; if (NeighborFullySolid(ref nFront)) skipBoundary[5] = true; }

            // 3. Boundary faces: add only visible boundary voxels, respecting boundary skip flags (skipBoundary).
            AddVisibleBoundaryFacesSelective(ref desc,
                baseX, baseY, baseZ,
                lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                allSecs, sx, sy, sz,
                sxCount, syCount, szCount, skipBoundary,
                faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                data);

            // 4. Apply bounds mask trimming so emission can avoid per-bit bounds checks when tight bounds present.
            if (hasTightBounds)
            {
                ApplyBoundsMask(lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
            }

            // Reserve capacity exactly by popcount after boundary reinsertion and optional bounds trimming.
            int cNX = PopCountMask(faceNX);
            int cPX = PopCountMask(facePX);
            int cNY = PopCountMask(faceNY);
            int cPY = PopCountMask(facePY);
            int cNZ = PopCountMask(faceNZ);
            int cPZ = PopCountMask(facePZ);
            int addCount = cNX + cPX + cNY + cPY + cNZ + cPZ;
            if (addCount > 0)
            {
                offsetList.EnsureCapacity(offsetList.Count + addCount * 3);
                tileIndexList.EnsureCapacity(tileIndexList.Count + addCount);
                faceDirList.EnsureCapacity(faceDirList.Count + addCount);
            }

            // 5. Per-face tile indices via shared cache used also by uniform path.
            var tileSet = GetUniformFaceTileSet(block);
            bool sameTileAllFaces = tileSet.AllSame;
            Span<uint> faceTilesCached = stackalloc uint[6];
            if (!sameTileAllFaces)
            {
                faceTilesCached[0] = tileSet.TileNX;
                faceTilesCached[1] = tileSet.TilePX;
                faceTilesCached[2] = tileSet.TileNY;
                faceTilesCached[3] = tileSet.TilePY;
                faceTilesCached[4] = tileSet.TileNZ;
                faceTilesCached[5] = tileSet.TilePZ;
            }

            // 6. Emit faces from masks. Boundary skip flags are intentionally ignored here so internal cavity walls are preserved.
            if (sameTileAllFaces)
            {
                if (hasTightBounds)
                {
                    EmitFacesFromMaskNoBounds(faceNX, 0, baseX, baseY, baseZ, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(facePX, 1, baseX, baseY, baseZ, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(faceNY, 2, baseX, baseY, baseZ, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(facePY, 3, baseX, baseY, baseZ, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(faceNZ, 4, baseX, baseY, baseZ, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(facePZ, 5, baseX, baseY, baseZ, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                }
                else
                {
                    EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tileSet.SingleTile, offsetList, tileIndexList, faceDirList);
                }
            }
            else
            {
                if (hasTightBounds)
                {
                    EmitFacesFromMaskNoBounds(faceNX, 0, baseX, baseY, baseZ, faceTilesCached[0], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(facePX, 1, baseX, baseY, baseZ, faceTilesCached[1], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(faceNY, 2, baseX, baseY, baseZ, faceTilesCached[2], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(facePY, 3, baseX, baseY, baseZ, faceTilesCached[3], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(faceNZ, 4, baseX, baseY, baseZ, faceTilesCached[4], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMaskNoBounds(facePZ, 5, baseX, baseY, baseZ, faceTilesCached[5], offsetList, tileIndexList, faceDirList);
                }
                else
                {
                    EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[0], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[1], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[2], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[3], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[4], offsetList, tileIndexList, faceDirList);
                    EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceTilesCached[5], offsetList, tileIndexList, faceDirList);
                }
            }

            return true;
        }
    }
}

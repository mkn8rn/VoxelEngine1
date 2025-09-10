using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Numerics;
using System.Runtime.CompilerServices;
using MVGE_GFX.Models;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        /// Emits faces for a MultiPacked section (Kind == 5).
        /// MultiPacked: multi-id palette packed bit representation (BitsPerIndex >= 2 OR palette.Count > 2).
        /// Strategy mirrors single-id packed fast path but adds per-voxel block decode and tile index caching.
        private bool EmitMultiPackedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            // Validate preconditions.
            if (desc.Kind != 5) return false; // Not MultiPacked (caller will fallback / choose other path)
            if (desc.NonAirCount == 0 ||
                desc.OccupancyBits == null ||
                desc.PackedBitData == null ||
                desc.Palette == null) return false; // nothing to emit / cannot decode
            // If palette effectively single non‑air id with 1 bit index prefer the single packed path.
            if (desc.Palette.Count <= 2 && desc.BitsPerIndex == 1) return false;

            EnsureBoundaryMasks();   // boundary position masks
            EnsureLiDecode();        // linear index -> (lx,ly,lz) decode tables

            var occ = desc.OccupancyBits; // 64 * ulong => 4096 occupancy bits

            // World base coordinates for this section
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

            // Resolve tight local bounds (0..15) if present
            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);

            // Working face masks (internal + boundary) for each direction
            Span<ulong> faceNX = stackalloc ulong[64]; // -X faces
            Span<ulong> facePX = stackalloc ulong[64]; // +X faces
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y faces
            Span<ulong> facePY = stackalloc ulong[64]; // +Y faces
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z faces
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z faces

            // Internal faces: occupancy AND NOT(shifted occupancy) excluding boundary layer bits via shared helper
            BuildInternalFaceMasks(occ, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

            // Add boundary faces (only those visible w.r.t outside world / neighbor sections) via shared helper
            AddVisibleBoundaryFaces(ref desc,
                                    baseX, baseY, baseZ,
                                    lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                                    data.SectionDescs,
                                    sx, sy, sz,
                                    data.sectionsX, data.sectionsY, data.sectionsZ,
                                    faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                                    data);

            // Tile index cache: shared helper class (replaces local array)
            var tileCache = new TileIndexCache();
            var localDesc = desc; // copy for use inside delegate

            // Per-voxel lambda used by generic EmitFacesFromMask (decodes block + tile index) 
            (ushort block, uint tileIndex) PerVoxel(int li, int lx, int ly, int lz, byte faceDir)
            {
                ushort b = DecodePacked(ref localDesc, lx, ly, lz);
                if (b == 0) return (0, 0);
                uint tIndex = tileCache.Get(atlas, b, faceDir);
                return (b, tIndex);
            }

            // Emit in canonical direction order matching single packed path using generic helper with per-voxel tile selection
            EmitFacesFromMask(faceNX, 0, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxel(li, lx, ly, lz, 0), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(facePX, 1, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxel(li, lx, ly, lz, 1), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(faceNY, 2, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxel(li, lx, ly, lz, 2), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(facePY, 3, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxel(li, lx, ly, lz, 3), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(faceNZ, 4, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxel(li, lx, ly, lz, 4), offsetList, tileIndexList, faceDirList);
            EmitFacesFromMask(facePZ, 5, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                (li, lx, ly, lz) => PerVoxel(li, lx, ly, lz, 5), offsetList, tileIndexList, faceDirList);

            return true; // handled
        }
    }
}

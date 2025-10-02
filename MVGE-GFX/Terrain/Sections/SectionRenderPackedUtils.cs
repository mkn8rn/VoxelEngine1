using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using MVGE_INF.Models.Generation;
using MVGE_INF.Loaders;
using System.Numerics;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Helpers dedicated to packed sections.

        // Returns true when descriptor represents a packed section with exactly one non-air id (palette[1]).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetPackedSingleId(ref SectionPrerenderDesc desc, out ushort id)
        {
            id = 0;
            if (desc.Kind != 4) return false;
            if (desc.Palette == null || desc.Palette.Count < 2) return false; // expect AIR + single id
            id = desc.Palette[1];
            return id != 0;
        }

        // Iterate set bits in a 4096-bit face mask (64 ulongs) and invoke action(li)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ForEachSetBit(ReadOnlySpan<ulong> mask, Action<int> action)
        {
            for (int w = 0; w < 64; w++)
            {
                ulong m = mask[w];
                while (m != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(m);
                    m &= m - 1;
                    int li = (w << 6) + bit;
                    if ((uint)li < 4096u) action(li);
                }
            }
        }

        // Opaque multi-packed helper: builds internal face masks, applies neighbor-based boundary visibility and bounds trimming.
        // skipDir flags mark boundary directions whose entire boundary layer is fully occluded by a fully solid neighbor.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BuildOpaqueFaceMasksMultiPacked(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
            Span<bool> skipDir,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            // Internal faces (opaque only)
            BuildInternalFaceMasks(desc.OpaqueBits, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

            // Neighbor full-solid classification -> boundary skip flags (0..5 order)
            if (sx > 0)        { ref var n = ref data.SectionDescs[SecIndex(sx - 1, sy, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[0] = true; }
            if (sx + 1 < data.sectionsX) { ref var n = ref data.SectionDescs[SecIndex(sx + 1, sy, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[1] = true; }
            if (sy > 0)        { ref var n = ref data.SectionDescs[SecIndex(sx, sy - 1, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[2] = true; }
            if (sy + 1 < data.sectionsY) { ref var n = ref data.SectionDescs[SecIndex(sx, sy + 1, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[3] = true; }
            if (sz > 0)        { ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz - 1, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[4] = true; }
            if (sz + 1 < data.sectionsZ) { ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz + 1, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[5] = true; }

            // Reinsert only visible boundary faces (respects skipDir)
            AddVisibleBoundaryFacesSelective(ref desc,
                sx * S, sy * S, sz * S,
                lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                data.SectionDescs, sx, sy, sz,
                data.sectionsX, data.sectionsY, data.sectionsZ,
                skipDir,
                faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                data);
            // Bounds trim – removes out-of-range bits so emission can skip bounds checks.
            ApplyBoundsMask(lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
        }

        // Single packed (single-id) opaque helper: identical logic to multi-packed mask assembly but receives pre-known single id.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BuildOpaqueFaceMasksSinglePacked(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
            Span<bool> skipDir,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            // Internal faces from occupancy
            BuildInternalFaceMasks(desc.OpaqueBits, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

            // Neighbor full-solid classification -> boundary skip flags
            if (sx > 0)        { ref var n = ref data.SectionDescs[SecIndex(sx - 1, sy, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[0] = true; }
            if (sx + 1 < data.sectionsX) { ref var n = ref data.SectionDescs[SecIndex(sx + 1, sy, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[1] = true; }
            if (sy > 0)        { ref var n = ref data.SectionDescs[SecIndex(sx, sy - 1, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[2] = true; }
            if (sy + 1 < data.sectionsY) { ref var n = ref data.SectionDescs[SecIndex(sx, sy + 1, sz, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[3] = true; }
            if (sz > 0)        { ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz - 1, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[4] = true; }
            if (sz + 1 < data.sectionsZ) { ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz + 1, data.sectionsY, data.sectionsZ)]; if (NeighborFullySolid(ref n)) skipDir[5] = true; }

            // Reinsert boundary faces (respect skip flags)
            AddVisibleBoundaryFacesSelective(ref desc,
                sx * S, sy * S, sz * S,
                lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                data.SectionDescs, sx, sy, sz,
                data.sectionsX, data.sectionsY, data.sectionsZ,
                skipDir,
                faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                data);

            // Bounds trimming
            ApplyBoundsMask(lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
        }

        // Popcounts total visible opaque faces after mask assembly.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountOpaqueFaces(Span<ulong> faceNX, Span<ulong> facePX, Span<ulong> faceNY, Span<ulong> facePY, Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            return PopCountMask(faceNX) + PopCountMask(facePX) + PopCountMask(faceNY) + PopCountMask(facePY) + PopCountMask(faceNZ) + PopCountMask(facePZ);
        }

        // Emit opaque faces from masks with on-demand packed decode (multi-id). localDesc copy MUST be used (caller supplies it) to avoid ref alias issues.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitOpaqueMultiPackedMasks(
            ref SectionPrerenderDesc localDesc,
            int baseX, int baseY, int baseZ,
            Span<ulong> faceMask, byte faceDir,
            List<byte> offsets, List<uint> tiles, List<byte> faceDirs)
        {
            // Ensure decode tables available
            EnsureLiDecode();
            for (int w = 0; w < 64; w++)
            {
                ulong bits = faceMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    ushort id = DecodePackedLocal(ref localDesc, lx, ly, lz);
                    if (id == 0 || !TerrainLoader.IsOpaque(id)) continue; // safety guard
                    uint tile = _fallbackTileCache.Get(atlas, id, faceDir);
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tile, faceDir, offsets, tiles, faceDirs);
                }
            }
        }

        // Emit opaque single packed masks (no per-voxel decode needed; uniform id). Supports per-face distinct tiles.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitOpaqueSinglePackedMasks(
            int baseX, int baseY, int baseZ,
            Span<ulong> mask, byte faceDir, uint tile,
            List<byte> offsets, List<uint> tiles, List<byte> faceDirs)
        {
            // Ensure decode tables initialized to avoid NullReferenceException.
            EnsureLiDecode();
            for (int w = 0; w < 64; w++)
            {
                ulong bits = mask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tile, faceDir, offsets, tiles, faceDirs);
                }
            }
        }

        // Transparent adjacency test shared by packed transparent path (single id). Mirrors uniform logic but parameterized.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TransparentPackedFaceVisible(
            ushort blockId,
            int nx, int ny, int nz,
            int curX, int curY, int curZ,
            int maxX, int maxY, int maxZ,
            ulong[] planeNegX, ulong[] planePosX,
            ulong[] planeNegY, ulong[] planePosY,
            ulong[] planeNegZ, ulong[] planePosZ,
            ushort[] tNegX, ushort[] tPosX,
            ushort[] tNegY, ushort[] tPosY,
            ushort[] tNegZ, ushort[] tPosZ)
        {
            // World boundary cases
            if (nx < 0)
            {
                int idx = curZ * maxY + curY;
                if (PlaneBit(planeNegX, idx)) return false;
                if (tNegX != null && (uint)idx < (uint)tNegX.Length && tNegX[idx] == blockId) return false;
                return true;
            }
            if (nx >= maxX)
            {
                int idx = curZ * maxY + curY;
                if (PlaneBit(planePosX, idx)) return false;
                if (tPosX != null && (uint)idx < (uint)tPosX.Length && tPosX[idx] == blockId) return false;
                return true;
            }
            if (ny < 0)
            {
                int idx = curX * maxZ + curZ;
                if (PlaneBit(planeNegY, idx)) return false;
                if (tNegY != null && (uint)idx < (uint)tNegY.Length && tNegY[idx] == blockId) return false;
                return true;
            }
            if (ny >= maxY)
            {
                int idx = curX * maxZ + curZ;
                if (PlaneBit(planePosY, idx)) return false;
                if (tPosY != null && (uint)idx < (uint)tPosY.Length && tPosY[idx] == blockId) return false;
                return true;
            }
            if (nz < 0)
            {
                int idx = curX * maxY + curY;
                if (PlaneBit(planeNegZ, idx)) return false;
                if (tNegZ != null && (uint)idx < (uint)tNegZ.Length && tNegZ[idx] == blockId) return false;
                return true;
            }
            if (nz >= maxZ)
            {
                int idx = curX * maxY + curY;
                if (PlaneBit(planePosZ, idx)) return false;
                if (tPosZ != null && (uint)idx < (uint)tPosZ.Length && tPosZ[idx] == blockId) return false;
                return true;
            }
            // Inside chunk
            ushort nb = GetBlock(nx, ny, nz);
            if (nb == 0) return true;              // air shows face
            if (TerrainLoader.IsOpaque(nb)) return false; // opaque hides
            if (nb == blockId) return false;       // same transparent id hides seam
            return true;                           // different transparent id
        }
    }
}

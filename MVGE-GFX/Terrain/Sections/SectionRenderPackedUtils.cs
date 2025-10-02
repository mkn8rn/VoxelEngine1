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
        private static readonly ulong[] _zeroMask64 = new ulong[64]; // used when an occupancy mask is absent
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
            if (sx > 0) {
                ref var n = ref data.SectionDescs[SecIndex(sx - 1, sy, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[0] = true;
            }
            if (sx + 1 < data.sectionsX) {
                ref var n = ref data.SectionDescs[SecIndex(sx + 1, sy, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[1] = true;
            }
            if (sy > 0) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy - 1, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[2] = true;
            }
            if (sy + 1 < data.sectionsY) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy + 1, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[3] = true;
            }
            if (sz > 0) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz - 1, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[4] = true;
            }
            if (sz + 1 < data.sectionsZ) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz + 1, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[5] = true;
            }

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
            ApplyBoundsMask(lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                            faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
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
            if (sx > 0) {
                ref var n = ref data.SectionDescs[SecIndex(sx - 1, sy, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[0] = true;
            }
            if (sx + 1 < data.sectionsX) {
                ref var n = ref data.SectionDescs[SecIndex(sx + 1, sy, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[1] = true;
            }
            if (sy > 0) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy - 1, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[2] = true;
            }
            if (sy + 1 < data.sectionsY) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy + 1, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[3] = true;
            }
            if (sz > 0) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz - 1, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[4] = true;
            }
            if (sz + 1 < data.sectionsZ) {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz + 1, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[5] = true;
            }

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
        private static int CountOpaqueFaces(
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
            => PopCountMask(faceNX) + PopCountMask(facePX) +
               PopCountMask(faceNY) + PopCountMask(facePY) +
               PopCountMask(faceNZ) + PopCountMask(facePZ);

        // Emit opaque faces from masks with on-demand packed decode (multi-id). localDesc copy MUST be used (caller supplies it) to avoid ref alias issues.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitOpaqueMultiPackedMasks(
            ref SectionPrerenderDesc localDesc,
            int baseX, int baseY, int baseZ,
            Span<ulong> faceMask, byte faceDir,
            List<byte> offsets, List<uint> tiles, List<byte> faceDirs)
        {
            EnsureLiDecode(); // Ensure decode tables available
            for (int w = 0; w < 64; w++)
            {
                ulong bits = faceMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
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
            EnsureLiDecode(); // Ensure decode tables initialized
            for (int w = 0; w < 64; w++)
            {
                ulong bits = mask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tile, faceDir, offsets, tiles, faceDirs);
                }
            }
        }

        // ---------------------- Transparent single-id bitset face derivation ----------------------
        // Builds directional transparent face masks for a single transparent id against both transparent self-neighbors (same-id seam suppression)
        // and opaque neighbors (opaque masks hide faces). Boundary bits are included; world-edge / neighbor-chunk occlusion is handled during emission.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BuildTransparentFaceMasksSingleId(
            ReadOnlySpan<ulong> transparentBits,
            ReadOnlySpan<ulong> opaqueBits,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            if (opaqueBits.Length != 64) opaqueBits = _zeroMask64; // Fallback to zero mask
            Span<ulong> shift = stackalloc ulong[64];
            Span<ulong> tmp   = stackalloc ulong[64]; // reused for opaque neighbor shift
            // -X
            BitsetShiftRight(transparentBits, STRIDE_X, shift);
            BitsetShiftRight(opaqueBits, STRIDE_X, tmp);
            for (int i = 0; i < 64; i++) faceNX[i] = transparentBits[i] & ~shift[i] & ~tmp[i];
            // +X
            BitsetShiftLeft(transparentBits, STRIDE_X, shift);
            BitsetShiftLeft(opaqueBits, STRIDE_X, tmp);
            for (int i = 0; i < 64; i++) facePX[i] = transparentBits[i] & ~shift[i] & ~tmp[i];
            // -Y
            BitsetShiftRight(transparentBits, STRIDE_Y, shift);
            BitsetShiftRight(opaqueBits, STRIDE_Y, tmp);
            for (int i = 0; i < 64; i++) faceNY[i] = transparentBits[i] & ~shift[i] & ~tmp[i];
            // +Y
            BitsetShiftLeft(transparentBits, STRIDE_Y, shift);
            BitsetShiftLeft(opaqueBits, STRIDE_Y, tmp);
            for (int i = 0; i < 64; i++) facePY[i] = transparentBits[i] & ~shift[i] & ~tmp[i];
            // -Z
            BitsetShiftRight(transparentBits, STRIDE_Z, shift);
            BitsetShiftRight(opaqueBits, STRIDE_Z, tmp);
            for (int i = 0; i < 64; i++) faceNZ[i] = transparentBits[i] & ~shift[i] & ~tmp[i];
            // +Z
            BitsetShiftLeft(transparentBits, STRIDE_Z, shift);
            BitsetShiftLeft(opaqueBits, STRIDE_Z, tmp);
            for (int i = 0; i < 64; i++) facePZ[i] = transparentBits[i] & ~shift[i] & ~tmp[i];
        }

        // Emits transparent single-id faces from bit masks applying world-boundary / neighbor-chunk occlusion tests for boundary cells only.
        private void EmitTransparentSingleIdMasks(
            ushort blockId,
            int baseX, int baseY, int baseZ,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ,
            List<byte> offsets, List<uint> tiles, List<byte> dirs)
        {
            EnsureLiDecode();
            // World plane (opaque) + neighbor transparent id arrays
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
            var tNegX = data.NeighborTransparentPlaneNegX; var tPosX = data.NeighborTransparentPlanePosX;
            var tNegY = data.NeighborTransparentPlaneNegY; var tPosY = data.NeighborTransparentPlanePosY;
            var tNegZ = data.NeighborTransparentPlaneNegZ; var tPosZ = data.NeighborTransparentPlanePosZ;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            void EmitMask(ReadOnlySpan<ulong> mask, byte faceDir)
            {
                for (int w = 0; w < 64; w++)
                {
                    ulong bits = mask[w];
                    while (bits != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                        int li = (w << 6) + bit;
                        int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                        int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                        bool boundary = (lx == 0 || lx == 15 || ly == 0 || ly == 15 || lz == 0 || lz == 15);
                        if (boundary)
                        {
                            bool culled = false; int planeIndex;
                            switch (faceDir)
                            {
                                case 0: // -X
                                    if (wx == 0)
                                    {
                                        planeIndex = wz * maxY + wy;
                                        if (PlaneBit(planeNegX, planeIndex) 
                                            || (tNegX != null 
                                            && (uint)planeIndex < (uint)tNegX.Length 
                                            && tNegX[planeIndex] == blockId)) 
                                            culled = true;
                                    }
                                    else
                                    {
                                        ushort nb = GetBlock(wx - 1, wy, wz); 
                                        if (nb == blockId 
                                            || TerrainLoader.IsOpaque(nb)) 
                                            culled = true;
                                    }
                                    break;
                                case 1: // +X
                                    if (wx == maxX - 1)
                                    {
                                        planeIndex = wz * maxY + wy;
                                        if (PlaneBit(planePosX, planeIndex) 
                                            || (tPosX != null 
                                            && (uint)planeIndex < (uint)tPosX.Length 
                                            && tPosX[planeIndex] == blockId)) 
                                            culled = true;
                                    }
                                    else
                                    {
                                        ushort nb = GetBlock(wx + 1, wy, wz); 
                                        if (nb == blockId 
                                            || TerrainLoader.IsOpaque(nb)) 
                                            culled = true;
                                    }
                                    break;
                                case 2: // -Y
                                    if (wy == 0)
                                    {
                                        planeIndex = (baseX + lx) * maxZ + (baseZ + lz);
                                        if (PlaneBit(planeNegY, planeIndex) 
                                            || (tNegY != null 
                                            && (uint)planeIndex < (uint)tNegY.Length 
                                            && tNegY[planeIndex] == blockId)) culled = true;
                                    }
                                    else
                                    {
                                        ushort nb = GetBlock(wx, wy - 1, wz); 
                                        if (nb == blockId 
                                            || TerrainLoader.IsOpaque(nb)) 
                                            culled = true;
                                    }
                                    break;
                                case 3: // +Y
                                    if (wy == maxY - 1)
                                    {
                                        planeIndex = (baseX + lx) * maxZ + (baseZ + lz);
                                        if (PlaneBit(planePosY, planeIndex) 
                                            || (tPosY != null 
                                            && (uint)planeIndex < (uint)tPosY.Length 
                                            && tPosY[planeIndex] == blockId)) 
                                            culled = true;
                                    }
                                    else
                                    {
                                        ushort nb = GetBlock(wx, wy + 1, wz); 
                                        if (nb == blockId 
                                            || TerrainLoader.IsOpaque(nb)) 
                                            culled = true;
                                    }
                                    break;
                                case 4: // -Z
                                    if (wz == baseZ)
                                    {
                                        planeIndex = (baseX + lx) * maxY + (baseY + ly);
                                        if (PlaneBit(planeNegZ, planeIndex) 
                                            || (tNegZ != null && (uint)planeIndex < (uint)tNegZ.Length 
                                            && tNegZ[planeIndex] == blockId)) 
                                            culled = true;
                                    }
                                    else
                                    {
                                        ushort nb = GetBlock(wx, wy, wz - 1); 
                                        if (nb == blockId 
                                            || TerrainLoader.IsOpaque(nb)) 
                                            culled = true;
                                    }
                                    break;
                                case 5: // +Z
                                    if (wz == maxZ - 1)
                                    {
                                        planeIndex = (baseX + lx) * maxY + (baseY + ly);
                                        if (PlaneBit(planePosZ, planeIndex) 
                                            || (tPosZ != null 
                                            && (uint)planeIndex < (uint)tPosZ.Length 
                                            && tPosZ[planeIndex] == blockId)) 
                                            culled = true;
                                    }
                                    else
                                    {
                                        ushort nb = GetBlock(wx, wy, wz + 1); 
                                        if (nb == blockId 
                                            || TerrainLoader.IsOpaque(nb)) 
                                            culled = true;
                                    }
                                    break;
                            }
                            if (culled) continue;
                        }
                        uint tile = _fallbackTileCache.Get(atlas, blockId, faceDir);
                        EmitOneInstance(wx, wy, wz, tile, faceDir, offsets, tiles, dirs);
                    }
                }
            }

            EmitMask(faceNX, 0); EmitMask(facePX, 1);
            EmitMask(faceNY, 2); EmitMask(facePY, 3);
            EmitMask(faceNZ, 4); EmitMask(facePZ, 5);
        }
    
        // Generic transparent face mask builder for a per-id voxel mask (voxelMask) against opaque occlusion & same-id seam suppression.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BuildTransparentFaceMasksGeneric(
            ReadOnlySpan<ulong> voxelMask,
            ReadOnlySpan<ulong> opaqueMask,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            Span<ulong> shift = stackalloc ulong[64];
            // -X
            BitsetShiftRight(voxelMask, STRIDE_X, shift); for (int i = 0; i < 64; i++) faceNX[i] = voxelMask[i] & ~shift[i];
            BitsetShiftRight(opaqueMask, STRIDE_X, shift); for (int i = 0; i < 64; i++) faceNX[i] &= ~shift[i];
            // +X
            BitsetShiftLeft(voxelMask, STRIDE_X, shift);  for (int i = 0; i < 64; i++) facePX[i] = voxelMask[i] & ~shift[i];
            BitsetShiftLeft(opaqueMask, STRIDE_X, shift); for (int i = 0; i < 64; i++) facePX[i] &= ~shift[i];
            // -Y
            BitsetShiftRight(voxelMask, STRIDE_Y, shift); for (int i = 0; i < 64; i++) faceNY[i] = voxelMask[i] & ~shift[i];
            BitsetShiftRight(opaqueMask, STRIDE_Y, shift);for (int i = 0; i < 64; i++) faceNY[i] &= ~shift[i];
            // +Y
            BitsetShiftLeft(voxelMask, STRIDE_Y, shift);  for (int i = 0; i < 64; i++) facePY[i] = voxelMask[i] & ~shift[i];
            BitsetShiftLeft(opaqueMask, STRIDE_Y, shift); for (int i = 0; i < 64; i++) facePY[i] &= ~shift[i];
            // -Z
            BitsetShiftRight(voxelMask, STRIDE_Z, shift); for (int i = 0; i < 64; i++) faceNZ[i] = voxelMask[i] & ~shift[i];
            BitsetShiftRight(opaqueMask, STRIDE_Z, shift);for (int i = 0; i < 64; i++) faceNZ[i] &= ~shift[i];
            // +Z
            BitsetShiftLeft(voxelMask, STRIDE_Z, shift);  for (int i = 0; i < 64; i++) facePZ[i] = voxelMask[i] & ~shift[i];
            BitsetShiftLeft(opaqueMask, STRIDE_Z, shift); for (int i = 0; i < 64; i++) facePZ[i] &= ~shift[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitTransparentMasked(
            ReadOnlySpan<ulong> voxelMask, ulong[] faceMask, ushort id,
            int baseX, int baseY, int baseZ, byte faceDir,
            List<byte> offsets, List<uint> tiles, List<byte> dirs)
        {
            if (faceMask == null) return;
            EnsureLiDecode();
            for (int w = 0; w < 64; w++)
            {
                ulong bits = faceMask[w] & voxelMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                    bool boundary = (lx == 0 || lx == 15 || ly == 0 || ly == 15 || lz == 0 || lz == 15); // Cross-section same-id / opaque suppression
                    if (boundary)
                    {
                        int nx = wx, ny = wy, nz = wz;
                        switch (faceDir)
                        {
                            case 0: nx = wx - 1; break; case 1: nx = wx + 1; break;
                            case 2: ny = wy - 1; break; case 3: ny = wy + 1; break;
                            case 4: nz = wz - 1; break; case 5: nz = wz + 1; break;
                        }
                        if (nx >= 0 && ny >= 0 && nz >= 0 && nx < data.maxX && ny < data.maxY && nz < data.maxZ)
                        {
                            ushort nb = GetBlock(nx, ny, nz); if (nb == id || TerrainLoader.IsOpaque(nb)) continue;
                        }
                    }
                    uint tile = _fallbackTileCache.Get(atlas, id, faceDir);
                    EmitOneInstance(wx, wy, wz, tile, faceDir, offsets, tiles, dirs);
                }
            }
        }
    }
}

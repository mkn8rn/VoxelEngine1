using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_INF.Models.Generation;
using MVGE_INF.Loaders;
using System.Numerics;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Fallback zero mask used when an occupancy/opaque mask is absent.
        private static readonly ulong[] _zeroMask64 = new ulong[64];

        // Local packed (multi-id) decode (reused by MultiPacked + helpers)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort DecodePackedLocal(ref SectionPrerenderDesc d, int lx, int ly, int lz)
        {
            if (d.PackedBitData == null || d.Palette == null || d.BitsPerIndex <= 0) return 0;
            int li = ((lz * 16 + lx) << 4) + ly;
            int bpi = d.BitsPerIndex;
            long bitPos = (long)li * bpi;
            int word = (int)(bitPos >> 5);
            int bitOffset = (int)(bitPos & 31);
            uint value = d.PackedBitData[word] >> bitOffset;
            int rem = 32 - bitOffset;
            if (rem < bpi) value |= d.PackedBitData[word + 1] << rem;
            int mask = (1 << bpi) - 1;
            int pi = (int)(value & mask);
            if ((uint)pi >= (uint)d.Palette.Count) return 0;
            return d.Palette[pi];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetPackedSingleId(ref SectionPrerenderDesc desc, out ushort id)
        {
            id = 0;
            if (desc.Kind != 4) return false;
            if (desc.Palette == null || desc.Palette.Count < 2) return false; // expect AIR + single id
            id = desc.Palette[1];
            return id != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PopCountMask(Span<ulong> mask)
        {
            int c = 0;
            for (int i = 0; i < 64; i++) c += BitOperations.PopCount(mask[i]);
            return c;
        }

        // Count visible opaque faces across six directional masks.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountOpaqueFaces(
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
            => PopCountMask(faceNX) + PopCountMask(facePX) +
               PopCountMask(faceNY) + PopCountMask(facePY) +
               PopCountMask(faceNZ) + PopCountMask(facePZ);

        // --- Unified helper: build opaque face masks for packed sections (single- or multi-id) ---
        // - occupancy: occupancy mask to use for internal-face derivation (pass desc.OpaqueBits for both single/multi).
        // - skipDir is set by neighbor full-solid checks by the caller or can be computed here if desired.
        // Behavior: builds internal face masks, reinserts boundary faces selectively and applies bounds trimming.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BuildPackedOpaqueFaceMasks(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
            Span<bool> skipDir,
            ReadOnlySpan<ulong> occupancy,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            // Internal faces from occupancy.
            BuildInternalFaceMasks(occupancy, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

            // Neighbor full-solid classification -> boundary skip flags.
            if (sx > 0)
            {
                ref var n = ref data.SectionDescs[SecIndex(sx - 1, sy, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[0] = true;
            }
            if (sx + 1 < data.sectionsX)
            {
                ref var n = ref data.SectionDescs[SecIndex(sx + 1, sy, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[1] = true;
            }
            if (sy > 0)
            {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy - 1, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[2] = true;
            }
            if (sy + 1 < data.sectionsY)
            {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy + 1, sz, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[3] = true;
            }
            if (sz > 0)
            {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz - 1, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[4] = true;
            }
            if (sz + 1 < data.sectionsZ)
            {
                ref var n = ref data.SectionDescs[SecIndex(sx, sy, sz + 1, data.sectionsY, data.sectionsZ)];
                if (NeighborFullySolid(ref n)) skipDir[5] = true;
            }

            // Reinsert boundary faces (respect skip flags). Uses existing utility that handles world-edge/neighbor checks.
            AddVisibleBoundaryFacesSelective(ref desc,
                sx * S, sy * S, sz * S,
                lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                data.SectionDescs, sx, sy, sz,
                data.sectionsX, data.sectionsY, data.sectionsZ,
                skipDir,
                faceNX, facePX, faceNY, facePY, faceNZ, facePZ,
                data);

            // Bounds trimming to remove out-of-range bits so emission can skip bounds checks.
            ApplyBoundsMask(lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                            faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
        }

        // --- Unified opaque emitter ---
        // Emits instances for an opaque face mask.
        // Two modes:
        //  - uniformId != 0 : single pre-known block id used for every voxel in mask. tileProvider should return tile for (id, faceDir).
        //  - uniformId == 0 : use decodeVoxel to decode per-voxel id; skip non-opaque or zero ids.
        // decodeVoxel is only used when per-voxel decode is required.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitOpaqueMasks(
            ref SectionPrerenderDesc localDesc,
            int baseX, int baseY, int baseZ,
            ReadOnlySpan<ulong> faceMask, byte faceDir,
            ushort uniformId,
            Func<int, int, int, ushort> decodeVoxel,
            Func<ushort, byte, uint> tileProvider,
            List<byte> offsets, List<uint> tilesOut, List<byte> faceDirs)
        {
            EnsureLiDecode();

            for (int w = 0; w < 64; w++)
            {
                ulong bits = faceMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];

                    ushort id = uniformId;
                    if (uniformId == 0)
                    {
                        // per-voxel decode
                        id = decodeVoxel(lx, ly, lz);
                        if (id == 0 || !TerrainLoader.IsOpaque(id)) continue;
                    }

                    uint tile = tileProvider(id, faceDir);
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tile, faceDir, offsets, tilesOut, faceDirs);
                }
            }
        }

        // Instance tile provider that matches original fallback cache usage.
        // Made non-static to allow access to instance members.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint FallbackTileProvider(ushort id, byte faceDir)
            => _fallbackTileCache.Get(atlas, id, faceDir);

        // --- Unified transparent face mask builder ---
        // Builds directional transparent face masks for a voxelMask while suppressing same-id seams and opaque occlusion.
        // If opaqueMask is null or length != 64, _zeroMask64 is used.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BuildTransparentFaceMasks(
            ReadOnlySpan<ulong> voxelMask,
            ReadOnlySpan<ulong> opaqueMask,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            if (opaqueMask.Length != 64) opaqueMask = _zeroMask64;
            Span<ulong> shift = stackalloc ulong[64];
            Span<ulong> tmp = stackalloc ulong[64];

            // -X
            BitsetShiftRight(voxelMask, STRIDE_X, shift);
            BitsetShiftRight(opaqueMask, STRIDE_X, tmp);
            for (int i = 0; i < 64; i++) faceNX[i] = voxelMask[i] & ~shift[i] & ~tmp[i];

            // +X
            BitsetShiftLeft(voxelMask, STRIDE_X, shift);
            BitsetShiftLeft(opaqueMask, STRIDE_X, tmp);
            for (int i = 0; i < 64; i++) facePX[i] = voxelMask[i] & ~shift[i] & ~tmp[i];

            // -Y
            BitsetShiftRight(voxelMask, STRIDE_Y, shift);
            BitsetShiftRight(opaqueMask, STRIDE_Y, tmp);
            for (int i = 0; i < 64; i++) faceNY[i] = voxelMask[i] & ~shift[i] & ~tmp[i];

            // +Y
            BitsetShiftLeft(voxelMask, STRIDE_Y, shift);
            BitsetShiftLeft(opaqueMask, STRIDE_Y, tmp);
            for (int i = 0; i < 64; i++) facePY[i] = voxelMask[i] & ~shift[i] & ~tmp[i];

            // -Z
            BitsetShiftRight(voxelMask, STRIDE_Z, shift);
            BitsetShiftRight(opaqueMask, STRIDE_Z, tmp);
            for (int i = 0; i < 64; i++) faceNZ[i] = voxelMask[i] & ~shift[i] & ~tmp[i];

            // +Z
            BitsetShiftLeft(voxelMask, STRIDE_Z, shift);
            BitsetShiftLeft(opaqueMask, STRIDE_Z, tmp);
            for (int i = 0; i < 64; i++) facePZ[i] = voxelMask[i] & ~shift[i] & ~tmp[i];
        }

        // --- Unified transparent emitter ---
        // Supports:
        //  - Masked fast-path: pass faceMask arrays (precomputed) and voxelMask (dominant bits) and provide uniformId.
        //  - Per-id masks: pass faceMask arrays computed from per-id voxelMask and uniformId == id.
        // This routine enforces boundary neighbor tests only for voxels on the section edge.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitTransparentMasks(
            ushort uniformId, // non-zero when emitting for a known single id
            int baseX, int baseY, int baseZ,
            ReadOnlySpan<ulong> faceNX, ReadOnlySpan<ulong> facePX,
            ReadOnlySpan<ulong> faceNY, ReadOnlySpan<ulong> facePY,
            ReadOnlySpan<ulong> faceNZ, ReadOnlySpan<ulong> facePZ,
            ReadOnlySpan<ulong> voxelMask, // optional mask to AND with face masks; pass default if not needed
            List<byte> offsets, List<uint> tilesOut, List<byte> dirs)
        {
            EnsureLiDecode();

            // neighbor / plane tables cached locally for speed (as original single-id emitter did)
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
            var tNegX = data.NeighborTransparentPlaneNegX; var tPosX = data.NeighborTransparentPlanePosX;
            var tNegY = data.NeighborTransparentPlaneNegY; var tPosY = data.NeighborTransparentPlanePosY;
            var tNegZ = data.NeighborTransparentPlaneNegZ; var tPosZ = data.NeighborTransparentPlanePosZ;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // Inline the emission logic per-direction to avoid capturing ref-like spans in a local function.
            // Direction 0: -X
            for (int w = 0; w < 64; w++)
            {
                ulong bits = faceNX[w];
                if (!voxelMask.IsEmpty) bits &= voxelMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                    bool culled = false;
                    if (lx == 0)
                    {
                        int planeIndex = wz * maxY + wy;
                        if (PlaneBit(planeNegX, planeIndex)
                            || (tNegX != null && (uint)planeIndex < (uint)tNegX.Length && tNegX[planeIndex] == uniformId))
                            culled = true;
                    }
                    else
                    {
                        ushort nb = GetBlock(wx - 1, wy, wz);
                        if (nb == uniformId || TerrainLoader.IsOpaque(nb)) culled = true;
                    }
                    if (culled) continue;

                    uint tile = _fallbackTileCache.Get(atlas, uniformId, 0);
                    EmitOneInstance(wx, wy, wz, tile, 0, offsets, tilesOut, dirs);
                }
            }

            // Direction 1: +X
            for (int w = 0; w < 64; w++)
            {
                ulong bits = facePX[w];
                if (!voxelMask.IsEmpty) bits &= voxelMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                    bool culled = false;
                    if (lx == 15)
                    {
                        int planeIndex = wz * maxY + wy;
                        if (PlaneBit(planePosX, planeIndex)
                            || (tPosX != null && (uint)planeIndex < (uint)tPosX.Length && tPosX[planeIndex] == uniformId))
                            culled = true;
                    }
                    else
                    {
                        ushort nb = GetBlock(wx + 1, wy, wz);
                        if (nb == uniformId || TerrainLoader.IsOpaque(nb)) culled = true;
                    }
                    if (culled) continue;

                    uint tile = _fallbackTileCache.Get(atlas, uniformId, 1);
                    EmitOneInstance(wx, wy, wz, tile, 1, offsets, tilesOut, dirs);
                }
            }

            // Direction 2: -Y
            for (int w = 0; w < 64; w++)
            {
                ulong bits = faceNY[w];
                if (!voxelMask.IsEmpty) bits &= voxelMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                    bool culled = false;
                    if (ly == 0)
                    {
                        int planeIndex = (baseX + lx) * maxZ + (baseZ + lz);
                        if (PlaneBit(planeNegY, planeIndex)
                            || (tNegY != null && (uint)planeIndex < (uint)tNegY.Length && tNegY[planeIndex] == uniformId))
                            culled = true;
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy - 1, wz);
                        if (nb == uniformId || TerrainLoader.IsOpaque(nb)) culled = true;
                    }
                    if (culled) continue;

                    uint tile = _fallbackTileCache.Get(atlas, uniformId, 2);
                    EmitOneInstance(wx, wy, wz, tile, 2, offsets, tilesOut, dirs);
                }
            }

            // Direction 3: +Y
            for (int w = 0; w < 64; w++)
            {
                ulong bits = facePY[w];
                if (!voxelMask.IsEmpty) bits &= voxelMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                    bool culled = false;
                    if (ly == 15)
                    {
                        int planeIndex = (baseX + lx) * maxZ + (baseZ + lz);
                        if (PlaneBit(planePosY, planeIndex)
                            || (tPosY != null && (uint)planeIndex < (uint)tPosY.Length && tPosY[planeIndex] == uniformId))
                            culled = true;
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy + 1, wz);
                        if (nb == uniformId || TerrainLoader.IsOpaque(nb)) culled = true;
                    }
                    if (culled) continue;

                    uint tile = _fallbackTileCache.Get(atlas, uniformId, 3);
                    EmitOneInstance(wx, wy, wz, tile, 3, offsets, tilesOut, dirs);
                }
            }

            // Direction 4: -Z
            for (int w = 0; w < 64; w++)
            {
                ulong bits = faceNZ[w];
                if (!voxelMask.IsEmpty) bits &= voxelMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                    bool culled = false;
                    if (lz == 0)
                    {
                        int planeIndex = (baseX + lx) * maxY + (baseY + ly);
                        if (PlaneBit(planeNegZ, planeIndex)
                            || (tNegZ != null && (uint)planeIndex < (uint)tNegZ.Length && tNegZ[planeIndex] == uniformId))
                            culled = true;
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy, wz - 1);
                        if (nb == uniformId || TerrainLoader.IsOpaque(nb)) culled = true;
                    }
                    if (culled) continue;

                    uint tile = _fallbackTileCache.Get(atlas, uniformId, 4);
                    EmitOneInstance(wx, wy, wz, tile, 4, offsets, tilesOut, dirs);
                }
            }

            // Direction 5: +Z
            for (int w = 0; w < 64; w++)
            {
                ulong bits = facePZ[w];
                if (!voxelMask.IsEmpty) bits &= voxelMask[w];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int li = (w << 6) + bit;
                    int lx = _lxFromLi[li]; int ly = _lyFromLi[li]; int lz = _lzFromLi[li];
                    int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                    bool culled = false;
                    if (lz == maxZ - 1)
                    {
                        int planeIndex = (baseX + lx) * maxY + (baseY + ly);
                        if (PlaneBit(planePosZ, planeIndex)
                            || (tPosZ != null && (uint)planeIndex < (uint)tPosZ.Length && tPosZ[planeIndex] == uniformId))
                            culled = true;
                    }
                    else
                    {
                        ushort nb = GetBlock(wx, wy, wz + 1);
                        if (nb == uniformId || TerrainLoader.IsOpaque(nb)) culled = true;
                    }
                    if (culled) continue;

                    uint tile = _fallbackTileCache.Get(atlas, uniformId, 5);
                    EmitOneInstance(wx, wy, wz, tile, 5, offsets, tilesOut, dirs);
                }
            }
        }
    }
}

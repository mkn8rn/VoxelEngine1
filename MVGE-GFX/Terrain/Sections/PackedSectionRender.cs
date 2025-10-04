using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using MVGE_INF.Loaders;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        /// Emits face instances for a Packed single‑id section (Kind==4) with a single non‑air block id (palette[1]).
        /// Preconditions:
        ///   - desc.Kind == 4
        ///   - Palette contains exactly AIR + one non‑air id (TryGetPackedSingleId succeeds)
        ///   - Opaque or transparent occupancy bitset present
        /// Overview / Steps:
        ///  1. Resolve local bounds (ResolveLocalBounds) for potential tight region trimming.
        ///  2. Fetch per‑face tile indices from uniform face tile cache (GetFaceTileSet); enable single‑tile usage when all faces share the same tile.
        ///  3. If the block id is opaque:
        ///       a. Build internal + boundary face masks via BuildOpaqueFaceMasksSinglePacked (internal faces + selective boundary + bounds trim).
        ///       b. Popcount visible faces to reserve exact output capacity.
        ///       c. Emit faces by iterating mask bits (EmitOpaqueSinglePackedMasks) with per‑direction tile index (no per-voxel decode cost).
        ///  4. If the block id is transparent:
        ///       a. Derive directional transparent face masks (BuildTransparentFaceMasksSingleId) using transparent and opaque bitsets (seam suppression + opaque occlusion) for the single id.
        ///       b. Apply bounds trimming (ApplyBoundsMask) to restrict emission to tight bounds.
        ///       c. Emit faces from masks (EmitTransparentSingleIdMasks) applying world-edge and neighbor-chunk boundary suppression only for boundary cells.
        ///  5. Return true when handled; return false only if preconditions fail (caller will fallback).
        private bool EmitPackedSectionInstances(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
            List<byte> opaqueOffsetList, List<uint> opaqueTileIndexList, List<byte> opaqueFaceDirList,
            List<byte> transparentOffsetList, List<uint> transparentTileIndexList, List<byte> transparentFaceDirList)
        {
            if (!TryGetPackedSingleId(ref desc, out ushort id) || id == 0)
                return false; // not a valid single-id packed section -> let fallback for now

            bool isOpaque = TerrainLoader.IsOpaque(id);

            // Bounds
            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);
            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;

            // Precompute face tiles – reuse uniform tile cache for consistency.
            var tileSet = GetFaceTileSet(id);
            bool sameTile = tileSet.AllSame;
            uint tileNX = sameTile ? tileSet.SingleTile : tileSet.TileNX;
            uint tilePX = sameTile ? tileSet.SingleTile : tileSet.TilePX;
            uint tileNY = sameTile ? tileSet.SingleTile : tileSet.TileNY;
            uint tilePY = sameTile ? tileSet.SingleTile : tileSet.TilePY;
            uint tileNZ = sameTile ? tileSet.SingleTile : tileSet.TileNZ;
            uint tilePZ = sameTile ? tileSet.SingleTile : tileSet.TilePZ;

            // Fast path occupancy sources
            var occOpaque = desc.OpaqueBits;
            var occTransparent = desc.TransparentBits;

            if (isOpaque)
            {
                if (occOpaque == null || desc.OpaqueCount == 0) return true; // nothing opaque to emit

                Span<ulong> faceNX = stackalloc ulong[64];
                Span<ulong> facePX = stackalloc ulong[64];
                Span<ulong> faceNY = stackalloc ulong[64];
                Span<ulong> facePY = stackalloc ulong[64];
                Span<ulong> faceNZ = stackalloc ulong[64];
                Span<ulong> facePZ = stackalloc ulong[64];
                Span<bool> skipDir = stackalloc bool[6]; // initialized false

                // Build masks using unified builder
                BuildPackedOpaqueFaceMasks(ref desc, sx, sy, sz, S,
                    lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                    skipDir,
                    occOpaque.AsSpan(),
                    faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

                int addFaces = CountOpaqueFaces(faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
                if (addFaces > 0)
                {
                    opaqueOffsetList.EnsureCapacity(opaqueOffsetList.Count + addFaces * 3);
                    opaqueTileIndexList.EnsureCapacity(opaqueTileIndexList.Count + addFaces);
                    opaqueFaceDirList.EnsureCapacity(opaqueFaceDirList.Count + addFaces);

                    // Provide a tileProvider that returns the precomputed per-face tile.
                    uint TileProvider(ushort _id, byte faceDir) => faceDir switch
                    {
                        0 => tileNX,
                        1 => tilePX,
                        2 => tileNY,
                        3 => tilePY,
                        4 => tileNZ,
                        5 => tilePZ,
                        _ => tileNX
                    };

                    // Emit per direction
                    if (PopCountMask(faceNX) > 0) EmitOpaqueMasks(ref desc, baseX, baseY, baseZ, faceNX, 0, id, null, TileProvider, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (PopCountMask(facePX) > 0) EmitOpaqueMasks(ref desc, baseX, baseY, baseZ, facePX, 1, id, null, TileProvider, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (PopCountMask(faceNY) > 0) EmitOpaqueMasks(ref desc, baseX, baseY, baseZ, faceNY, 2, id, null, TileProvider, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (PopCountMask(facePY) > 0) EmitOpaqueMasks(ref desc, baseX, baseY, baseZ, facePY, 3, id, null, TileProvider, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (PopCountMask(faceNZ) > 0) EmitOpaqueMasks(ref desc, baseX, baseY, baseZ, faceNZ, 4, id, null, TileProvider, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (PopCountMask(facePZ) > 0) EmitOpaqueMasks(ref desc, baseX, baseY, baseZ, facePZ, 5, id, null, TileProvider, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                }
                return true;
            }
            else
            {
                // Transparent single-id path
                if (occTransparent == null || desc.TransparentCount == 0) return true; // no transparent content

                Span<ulong> tFaceNX = stackalloc ulong[64];
                Span<ulong> tFacePX = stackalloc ulong[64];
                Span<ulong> tFaceNY = stackalloc ulong[64];
                Span<ulong> tFacePY = stackalloc ulong[64];
                Span<ulong> tFaceNZ = stackalloc ulong[64];
                Span<ulong> tFacePZ = stackalloc ulong[64];

                BuildTransparentFaceMasks(occTransparent.AsSpan(), (occOpaque != null) ? occOpaque.AsSpan() : _zeroMask64.AsSpan(),
                    tFaceNX, tFacePX, tFaceNY, tFacePY, tFaceNZ, tFacePZ);

                ApplyBoundsMask(lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, tFaceNX, tFacePX, tFaceNY, tFacePY, tFaceNZ, tFacePZ);

                int predicted = PopCountMask(tFaceNX) + PopCountMask(tFacePX) + PopCountMask(tFaceNY) + PopCountMask(tFacePY) + PopCountMask(tFaceNZ) + PopCountMask(tFacePZ);
                if (predicted > 0)
                {
                    transparentOffsetList.EnsureCapacity(transparentOffsetList.Count + predicted * 3);
                    transparentTileIndexList.EnsureCapacity(transparentTileIndexList.Count + predicted);
                    transparentFaceDirList.EnsureCapacity(transparentFaceDirList.Count + predicted);

                    // Emit transparent faces using unified emitter. directional masks already limited to this id.
                    EmitTransparentMasks(id, baseX, baseY, baseZ,
                        tFaceNX, tFacePX, tFaceNY, tFacePY, tFaceNZ, tFacePZ,
                        default,
                        transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                }
                return true;
            }
        }
    }
}

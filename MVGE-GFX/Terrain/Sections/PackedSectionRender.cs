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
        ///       a. Iterate every set bit in desc.TransparentBits within bounds (each bit = transparent voxel of this single id).
        ///       b. For each voxel test six neighbors with TransparentPackedFaceVisible (world boundary + neighbor opaque + same‑id seam suppression rules).
        ///       c. Emit a face for each direction whose neighbor test passes.
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
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // Precompute face tiles – reuse uniform tile cache for consistency.
            var tileSet = GetFaceTileSet(id);
            bool sameTile = tileSet.AllSame;
            Span<uint> faceTiles = stackalloc uint[6]
            {
                sameTile ? tileSet.SingleTile : tileSet.TileNX,
                sameTile ? tileSet.SingleTile : tileSet.TilePX,
                sameTile ? tileSet.SingleTile : tileSet.TileNY,
                sameTile ? tileSet.SingleTile : tileSet.TilePY,
                sameTile ? tileSet.SingleTile : tileSet.TileNZ,
                sameTile ? tileSet.SingleTile : tileSet.TilePZ
            };

            // Fast path occupancy sources (opaque vs transparent bitsets already built in finalize for packed)
            // For opaque we rely on desc.OpaqueBits; for transparent we use desc.TransparentBits (all bits for uniform transparent-like case of single id spread sparsely by bitset).
            var occOpaque = desc.OpaqueBits;
            var occTransparent = desc.TransparentBits;

            if (isOpaque)
            {
                if (occOpaque == null || desc.OpaqueCount == 0) return true; // nothing opaque to emit

                // Build masks (internal + boundary) with skip classification and bounds trimming
                Span<ulong> faceNX = stackalloc ulong[64];
                Span<ulong> facePX = stackalloc ulong[64];
                Span<ulong> faceNY = stackalloc ulong[64];
                Span<ulong> facePY = stackalloc ulong[64];
                Span<ulong> faceNZ = stackalloc ulong[64];
                Span<ulong> facePZ = stackalloc ulong[64];
                Span<bool> skipDir = stackalloc bool[6]; // initialized false

                BuildOpaqueFaceMasksSinglePacked(ref desc, sx, sy, sz, S,
                    lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                    skipDir,
                    faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

                // Exact capacity reserve by popcount (only visible bits)
                int addFaces = CountOpaqueFaces(faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
                if (addFaces > 0)
                {
                    opaqueOffsetList.EnsureCapacity(opaqueOffsetList.Count + addFaces * 3);
                    opaqueTileIndexList.EnsureCapacity(opaqueTileIndexList.Count + addFaces);
                    opaqueFaceDirList.EnsureCapacity(opaqueFaceDirList.Count + addFaces);

                    // Emit per direction (skip empty masks to avoid extra overhead)
                    if (!faceNX.IsEmpty && PopCountMask(faceNX) > 0) EmitOpaqueSinglePackedMasks(baseX, baseY, baseZ, faceNX, 0, faceTiles[0], opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (!facePX.IsEmpty && PopCountMask(facePX) > 0) EmitOpaqueSinglePackedMasks(baseX, baseY, baseZ, facePX, 1, faceTiles[1], opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (!faceNY.IsEmpty && PopCountMask(faceNY) > 0) EmitOpaqueSinglePackedMasks(baseX, baseY, baseZ, faceNY, 2, faceTiles[2], opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (!facePY.IsEmpty && PopCountMask(facePY) > 0) EmitOpaqueSinglePackedMasks(baseX, baseY, baseZ, facePY, 3, faceTiles[3], opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (!faceNZ.IsEmpty && PopCountMask(faceNZ) > 0) EmitOpaqueSinglePackedMasks(baseX, baseY, baseZ, faceNZ, 4, faceTiles[4], opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    if (!facePZ.IsEmpty && PopCountMask(facePZ) > 0) EmitOpaqueSinglePackedMasks(baseX, baseY, baseZ, facePZ, 5, faceTiles[5], opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                }
                return true;
            }
            else
            {
                // Transparent single-id path
                if (occTransparent == null || desc.TransparentCount == 0) return true; // nothing to emit

                // Neighbor plane references (opaque + transparent)
                var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
                var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
                var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
                var tNegX = data.NeighborTransparentPlaneNegX; var tPosX = data.NeighborTransparentPlanePosX;
                var tNegY = data.NeighborTransparentPlaneNegY; var tPosY = data.NeighborTransparentPlanePosY;
                var tNegZ = data.NeighborTransparentPlaneNegZ; var tPosZ = data.NeighborTransparentPlanePosZ;

                // Iterate every occupied transparent voxel within bounds and test 6 neighbors (cheap since single id).
                EnsureLiDecode();
                // Heuristic capacity (transparent tends to emit fewer than opaque full masks) -> 2 faces per voxel
                int heuristicFaces = Math.Min(desc.TransparentCount * 2, 4096 * 6);
                if (heuristicFaces > 0)
                {
                    transparentOffsetList.EnsureCapacity(transparentOffsetList.Count + heuristicFaces * 3);
                    transparentTileIndexList.EnsureCapacity(transparentTileIndexList.Count + heuristicFaces);
                    transparentFaceDirList.EnsureCapacity(transparentFaceDirList.Count + heuristicFaces);
                }

                for (int w = 0; w < 64; w++)
                {
                    ulong word = occTransparent[w];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int li = (w << 6) + bit;
                        int lx = _lxFromLi[li]; if (lx < lxMin || lx > lxMax) continue;
                        int ly = _lyFromLi[li]; if (ly < lyMin || ly > lyMax) continue;
                        int lz = _lzFromLi[li]; if (lz < lzMin || lz > lzMax) continue;
                        int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;

                        // -X
                        if (TransparentPackedFaceVisible(id, wx - 1, wy, wz, wx, wy, wz, maxX, maxY, maxZ,
                            planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                            tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            EmitOneInstance(wx, wy, wz, faceTiles[0], 0, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // +X
                        if (TransparentPackedFaceVisible(id, wx + 1, wy, wz, wx, wy, wz, maxX, maxY, maxZ,
                            planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                            tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            EmitOneInstance(wx, wy, wz, faceTiles[1], 1, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // -Y
                        if (TransparentPackedFaceVisible(id, wx, wy - 1, wz, wx, wy, wz, maxX, maxY, maxZ,
                            planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                            tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            EmitOneInstance(wx, wy, wz, faceTiles[2], 2, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // +Y
                        if (TransparentPackedFaceVisible(id, wx, wy + 1, wz, wx, wy, wz, maxX, maxY, maxZ,
                            planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                            tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            EmitOneInstance(wx, wy, wz, faceTiles[3], 3, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // -Z
                        if (TransparentPackedFaceVisible(id, wx, wy, wz - 1, wx, wy, wz, maxX, maxY, maxZ,
                            planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                            tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            EmitOneInstance(wx, wy, wz, faceTiles[4], 4, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // +Z
                        if (TransparentPackedFaceVisible(id, wx, wy, wz + 1, wx, wy, wz, maxX, maxY, maxZ,
                            planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                            tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            EmitOneInstance(wx, wy, wz, faceTiles[5], 5, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                    }
                }
                return true;
            }
        }
    }
}

using MVGE_GFX.Models;
using MVGE_GFX.Textures;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_INF.Loaders;
using System.Numerics;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {

        /// Emits face instances for a MultiPacked section (Kind==5) with multiple block ids 
        /// (opaque + transparent) in packed storage.
        /// Preconditions:
        ///   - desc.Kind == 5
        ///   - desc.PackedBitData, desc.Palette, desc.BitsPerIndex valid (>0)
        ///   - desc.OpaqueBits (opaque occupancy) and/or desc.TransparentBits (transparent occupancy)
        /// Steps:
        ///  OPAQUE PATH (runs only when desc.OpaqueBits != null and OpaqueCount > 0):
        ///   1. Resolve tight local bounds (ResolveLocalBounds) (lx/ly/lz min/max derived from section metadata when present).
        ///   2. Build internal face masks from opaque occupancy (BuildOpaqueFaceMasksMultiPacked) – internal faces + boundary reinsertion + neighbor full-solid skip flags + bounds trimming in one pipeline.
        ///   3. Popcount total visible opaque faces (CountOpaqueFaces) and reserve exact capacity for output lists.
        ///   4. Iterate each directional face mask; for every set bit decode the voxel id (DecodePackedLocal) and emit if it is still opaque.
        ///  TRANSPARENT PATH (runs only when desc.TransparentBits != null and TransparentCount > 0):
        ///   5. Heuristically reserve capacity (approx 2 faces per transparent voxel) to reduce reallocations.
        ///   6. Iterate all set bits in desc.TransparentBits inside bounds; decode id; skip if opaque or air.
        ///   7. For each of the 6 directions apply TransparentPackedFaceVisible to decide face visibility (culls when neighbor is opaque or same transparent id, reveals against air or different transparent id, respects world boundary planes and neighbor transparent plane id maps).
        ///   8. Emit visible transparent faces with cached per (block,face) tile indices.
        /// Returns true (always handles multi‑packed) unless descriptor invalid -> false to allow fallback.
        private bool EmitMultiPackedSectionInstances(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
            List<byte> opaqueOffsetList, List<uint> opaqueTileIndexList, List<byte> opaqueFaceDirList,
            List<byte> transparentOffsetList, List<uint> transparentTileIndexList, List<byte> transparentFaceDirList)
        {
            if (desc.Kind != 5 || desc.PackedBitData == null || desc.Palette == null || desc.BitsPerIndex <= 0)
                return false; // not multi-packed – let caller fallback / other path

            bool hasOpaque = desc.OpaqueBits != null && desc.OpaqueCount > 0;
            bool hasTransparent = desc.TransparentBits != null && desc.TransparentCount > 0;
            if (!hasOpaque && !hasTransparent) return true; // handled (no faces)

            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);
            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            EnsureLiDecode();

            // ---------------- OPAQUE PATH ----------------
            Span<ulong> faceNX = stackalloc ulong[64];
            Span<ulong> facePX = stackalloc ulong[64];
            Span<ulong> faceNY = stackalloc ulong[64];
            Span<ulong> facePY = stackalloc ulong[64];
            Span<ulong> faceNZ = stackalloc ulong[64];
            Span<ulong> facePZ = stackalloc ulong[64];
            Span<bool> skipDir = stackalloc bool[6]; // initialized false

            if (hasOpaque)
            {
                BuildOpaqueFaceMasksMultiPacked(ref desc, sx, sy, sz, S,
                    lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                    skipDir,
                    faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
                int opaqueFaces = CountOpaqueFaces(faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
                if (opaqueFaces > 0)
                {
                    opaqueOffsetList.EnsureCapacity(opaqueOffsetList.Count + opaqueFaces * 3);
                    opaqueTileIndexList.EnsureCapacity(opaqueTileIndexList.Count + opaqueFaces);
                    opaqueFaceDirList.EnsureCapacity(opaqueFaceDirList.Count + opaqueFaces);

                    var localDesc = desc; // local copy required
                    EmitOpaqueMultiPackedMasks(ref localDesc, baseX, baseY, baseZ, faceNX, 0, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    EmitOpaqueMultiPackedMasks(ref localDesc, baseX, baseY, baseZ, facePX, 1, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    EmitOpaqueMultiPackedMasks(ref localDesc, baseX, baseY, baseZ, faceNY, 2, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    EmitOpaqueMultiPackedMasks(ref localDesc, baseX, baseY, baseZ, facePY, 3, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    EmitOpaqueMultiPackedMasks(ref localDesc, baseX, baseY, baseZ, faceNZ, 4, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    EmitOpaqueMultiPackedMasks(ref localDesc, baseX, baseY, baseZ, facePZ, 5, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                }
            }

            // ---------------- TRANSPARENT PATH ----------------
            if (hasTransparent)
            {
                // Neighbor opaque planes & transparent planes
                var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
                var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
                var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
                var tNegX = data.NeighborTransparentPlaneNegX; var tPosX = data.NeighborTransparentPlanePosX;
                var tNegY = data.NeighborTransparentPlaneNegY; var tPosY = data.NeighborTransparentPlanePosY;
                var tNegZ = data.NeighborTransparentPlaneNegZ; var tPosZ = data.NeighborTransparentPlanePosZ;

                var tBits = desc.TransparentBits;
                if (tBits != null)
                {
                    // Worst-case prediction – each transparent voxel could emit up to 6 faces. Reserve proportional capacity (heuristic: 2 faces avg).
                    int heuristicFaces = Math.Min(desc.TransparentCount * 2, 4096 * 6);
                    if (heuristicFaces > 0)
                    {
                        transparentOffsetList.EnsureCapacity(transparentOffsetList.Count + heuristicFaces * 3);
                        transparentTileIndexList.EnsureCapacity(transparentTileIndexList.Count + heuristicFaces);
                        transparentFaceDirList.EnsureCapacity(transparentFaceDirList.Count + heuristicFaces);
                    }

                    for (int w = 0; w < 64; w++)
                    {
                        ulong word = tBits[w];
                        while (word != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(word);
                            word &= word - 1;
                            int li = (w << 6) + bit;
                            int lx = _lxFromLi[li]; if (lx < lxMin || lx > lxMax) continue;
                            int ly = _lyFromLi[li]; if (ly < lyMin || ly > lyMax) continue;
                            int lz = _lzFromLi[li]; if (lz < lzMin || lz > lzMax) continue;
                            int wx = baseX + lx; int wy = baseY + ly; int wz = baseZ + lz;
                            ushort id = DecodePackedLocal(ref desc, lx, ly, lz);
                            if (id == 0 || TerrainLoader.IsOpaque(id)) continue;

                            // For each face direction test visibility and emit if visible.
                            // -X
                            if (TransparentPackedFaceVisible(id, wx - 1, wy, wz, wx, wy, wz, maxX, maxY, maxZ,
                                planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                                tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            {
                                uint tile = _fallbackTileCache.Get(atlas, id, 0);
                                EmitOneInstance(wx, wy, wz, tile, 0, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                            }
                            // +X
                            if (TransparentPackedFaceVisible(id, wx + 1, wy, wz, wx, wy, wz, maxX, maxY, maxZ,
                                planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                                tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            {
                                uint tile = _fallbackTileCache.Get(atlas, id, 1);
                                EmitOneInstance(wx, wy, wz, tile, 1, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                            }
                            // -Y
                            if (TransparentPackedFaceVisible(id, wx, wy - 1, wz, wx, wy, wz, maxX, maxY, maxZ,
                                planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                                tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            {
                                uint tile = _fallbackTileCache.Get(atlas, id, 2);
                                EmitOneInstance(wx, wy, wz, tile, 2, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                            }
                            // +Y
                            if (TransparentPackedFaceVisible(id, wx, wy + 1, wz, wx, wy, wz, maxX, maxY, maxZ,
                                planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                                tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            {
                                uint tile = _fallbackTileCache.Get(atlas, id, 3);
                                EmitOneInstance(wx, wy, wz, tile, 3, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                            }
                            // -Z
                            if (TransparentPackedFaceVisible(id, wx, wy, wz - 1, wx, wy, wz, maxX, maxY, maxZ,
                                planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                                tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            {
                                uint tile = _fallbackTileCache.Get(atlas, id, 4);
                                EmitOneInstance(wx, wy, wz, tile, 4, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                            }
                            // +Z
                            if (TransparentPackedFaceVisible(id, wx, wy, wz + 1, wx, wy, wz, maxX, maxY, maxZ,
                                planeNegX, planePosX, planeNegY, planePosY, planeNegZ, planePosZ,
                                tNegX, tPosX, tNegY, tPosY, tNegZ, tPosZ))
                            {
                                uint tile = _fallbackTileCache.Get(atlas, id, 5);
                                EmitOneInstance(wx, wy, wz, tile, 5, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                            }
                        }
                    }
                }
            }

            return true;
        }
    }
}

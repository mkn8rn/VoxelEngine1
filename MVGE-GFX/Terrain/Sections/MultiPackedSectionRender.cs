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
        ///   1. Resolve tight local bounds (ResolveLocalBounds)
        ///   2. Build internal face masks from opaque occupancy + boundary reinsertion + neighbor skip
        ///   3. Popcount & emit opaque faces
        ///  TRANSPARENT PATH:
        ///   * Dominant single transparent id (if present) uses pre-built transparent face masks (internal + boundary) without per-voxel neighbor checks.
        ///   * Residual transparent voxels (multi-id) build per-id voxel masks then derive directional face masks via bitwise adjacency (same-id seam suppression + opaque occlusion) and emit.
        private bool EmitMultiPackedSectionInstances(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
            List<byte> opaqueOffsetList, List<uint> opaqueTileIndexList, List<byte> opaqueFaceDirList,
            List<byte> transparentOffsetList, List<uint> transparentTileIndexList, List<byte> transparentFaceDirList)
        {
            if (desc.Kind != 5 || desc.PackedBitData == null || desc.Palette == null || desc.BitsPerIndex <= 0)
                return false; // not multi-packed – let caller fallback / other path

            bool hasOpaque = desc.OpaqueBits != null && desc.OpaqueCount > 0;
            bool hasResidualTransparent = desc.TransparentBits != null && desc.TransparentCount > 0;
            bool hasDominantTransparent = desc.DominantTransparentBits != null && desc.DominantTransparentCount > 0;
            if (!hasOpaque && !hasResidualTransparent && !hasDominantTransparent) return true; // nothing

            ResolveLocalBounds(in desc, S, out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax);
            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;

            EnsureLiDecode();

            // ---------------- OPAQUE PATH ----------------
            if (hasOpaque)
            {
                Span<ulong> faceNX = stackalloc ulong[64];
                Span<ulong> facePX = stackalloc ulong[64];
                Span<ulong> faceNY = stackalloc ulong[64];
                Span<ulong> facePY = stackalloc ulong[64];
                Span<ulong> faceNZ = stackalloc ulong[64];
                Span<ulong> facePZ = stackalloc ulong[64];
                Span<bool> skipDir = stackalloc bool[6]; // initialized false

                // New unified builder: pass occupancy mask (desc.OpaqueBits)
                BuildPackedOpaqueFaceMasks(ref desc, sx, sy, sz, S,
                    lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                    skipDir,
                    desc.OpaqueBits.AsSpan(),
                    faceNX, facePX, faceNY, facePY, faceNZ, facePZ);

                int opaqueFaces = CountOpaqueFaces(faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
                if (opaqueFaces > 0)
                {
                    opaqueOffsetList.EnsureCapacity(opaqueOffsetList.Count + opaqueFaces * 3);
                    opaqueTileIndexList.EnsureCapacity(opaqueTileIndexList.Count + opaqueFaces);
                    opaqueFaceDirList.EnsureCapacity(opaqueFaceDirList.Count + opaqueFaces);

                    var localDesc = desc; // local copy required for decoding closure

                    // per-voxel decoder wrapper that avoids capturing a ref-local in a way that would be illegal.
                    // It takes the copied descriptor by value so inner DecodePackedLocal may be called safely.
                    ushort DecodePackedLocalValue(int lx, int ly, int lz)
                    {
                        var d = localDesc; // copy to local variable
                        return DecodePackedLocal(ref d, lx, ly, lz);
                    }

                    // Use unified emitter. uniformId==0 signals per-voxel decode.
                    EmitOpaqueMasks(ref localDesc, baseX, baseY, baseZ, faceNX, 0, 0,
                        (lx, ly, lz) => DecodePackedLocalValue(lx, ly, lz),
                        FallbackTileProvider,
                        opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);

                    EmitOpaqueMasks(ref localDesc, baseX, baseY, baseZ, facePX, 1, 0,
                        (lx, ly, lz) => DecodePackedLocalValue(lx, ly, lz),
                        FallbackTileProvider,
                        opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);

                    EmitOpaqueMasks(ref localDesc, baseX, baseY, baseZ, faceNY, 2, 0,
                        (lx, ly, lz) => DecodePackedLocalValue(lx, ly, lz),
                        FallbackTileProvider,
                        opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);

                    EmitOpaqueMasks(ref localDesc, baseX, baseY, baseZ, facePY, 3, 0,
                        (lx, ly, lz) => DecodePackedLocalValue(lx, ly, lz),
                        FallbackTileProvider,
                        opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);

                    EmitOpaqueMasks(ref localDesc, baseX, baseY, baseZ, faceNZ, 4, 0,
                        (lx, ly, lz) => DecodePackedLocalValue(lx, ly, lz),
                        FallbackTileProvider,
                        opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);

                    EmitOpaqueMasks(ref localDesc, baseX, baseY, baseZ, facePZ, 5, 0,
                        (lx, ly, lz) => DecodePackedLocalValue(lx, ly, lz),
                        FallbackTileProvider,
                        opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                }
            }

            // --------------- DOMINANT TRANSPARENT FAST PATH (mask based) ---------------
            if (hasDominantTransparent)
            {
                // Pre-built transparent directional face masks exist in desc.*TransparentFace* arrays.
                // Emit only faces where DominantTransparentBits is set by ANDing with precomputed face masks.
                var dom = desc.DominantTransparentBits.AsSpan();
                if (desc.TransparentFaceNegXBits != null)
                {
                    EmitTransparentMasks(desc.DominantTransparentId, baseX, baseY, baseZ,
                        desc.TransparentFaceNegXBits.AsSpan(), desc.TransparentFacePosXBits.AsSpan(),
                        desc.TransparentFaceNegYBits.AsSpan(), desc.TransparentFacePosYBits.AsSpan(),
                        desc.TransparentFaceNegZBits.AsSpan(), desc.TransparentFacePosZBits.AsSpan(),
                        dom,
                        transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                }
            }

            // --------------- RESIDUAL TRANSPARENT MULTI-ID BITSET PATH ---------------
            if (hasResidualTransparent)
            {
                var residualBits = desc.TransparentBits; // residual transparent occupancy (multiple ids)
                var opaqueBits = desc.OpaqueBits;         // opaque occupancy for occlusion
                var palette = desc.Palette;
                var transparentPaletteIndices = desc.TransparentPaletteIndices; // indices referencing palette positions that are transparent overall
                if (residualBits != null && transparentPaletteIndices != null && transparentPaletteIndices.Length > 0)
                {
                    int tCount = transparentPaletteIndices.Length;
                    var perIdMasks = new ulong[tCount][]; // lazily allocate when first voxel of that id encountered

                    var idToMaskIndex = new Dictionary<ushort, int>(tCount);
                    for (int i = 0; i < tCount; i++)
                    {
                        ushort bid = palette[transparentPaletteIndices[i]];
                        if (!idToMaskIndex.ContainsKey(bid)) idToMaskIndex.Add(bid, i);
                    }

                    // Single scan of residual transparent bits; decode palette id; set bit in its per-id mask.
                    for (int w = 0; w < 64; w++)
                    {
                        ulong word = residualBits[w];
                        while (word != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(word);
                            word &= word - 1;
                            int li = (w << 6) + bit;
                            int ly = li & 15; int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                            ushort id = DecodePackedLocal(ref desc, lx, ly, lz);
                            if (id == 0 || TerrainLoader.IsOpaque(id) || id == desc.DominantTransparentId) continue;
                            if (!idToMaskIndex.TryGetValue(id, out int mi)) continue;
                            var mask = perIdMasks[mi];
                            if (mask == null) { mask = perIdMasks[mi] = new ulong[64]; }
                            mask[w] |= 1UL << bit;
                        }
                    }

                    // For each per-id mask build directional face masks & emit.
                    Span<ulong> fNX = stackalloc ulong[64];
                    Span<ulong> fPX = stackalloc ulong[64];
                    Span<ulong> fNY = stackalloc ulong[64];
                    Span<ulong> fPY = stackalloc ulong[64];
                    Span<ulong> fNZ = stackalloc ulong[64];
                    Span<ulong> fPZ = stackalloc ulong[64];

                    for (int i = 0; i < tCount; i++)
                    {
                        var voxelMask = perIdMasks[i];
                        if (voxelMask == null) continue;

                        // Clear face masks
                        for (int j = 0; j < 64; j++) fNX[j] = fPX[j] = fNY[j] = fPY[j] = fNZ[j] = fPZ[j] = 0UL;

                        // Use unified transparent builder. supply opaqueBits (or zero mask) as ReadOnlySpan.
                        BuildTransparentFaceMasks(voxelMask.AsSpan(), (opaqueBits != null) ? opaqueBits.AsSpan() : _zeroMask64.AsSpan(),
                            fNX, fPX, fNY, fPY, fNZ, fPZ);

                        ApplyBoundsMask(lxMin, lxMax, lyMin, lyMax, lzMin, lzMax, fNX, fPX, fNY, fPY, fNZ, fPZ);

                        int add = PopCountMask(fNX) + PopCountMask(fPX) + PopCountMask(fNY) + PopCountMask(fPY) + PopCountMask(fNZ) + PopCountMask(fPZ);
                        if (add == 0) continue;

                        transparentOffsetList.EnsureCapacity(transparentOffsetList.Count + add * 3);
                        transparentTileIndexList.EnsureCapacity(transparentTileIndexList.Count + add);
                        transparentFaceDirList.EnsureCapacity(transparentFaceDirList.Count + add);

                        ushort id = palette[transparentPaletteIndices[i]];
                        // directional masks already restricted to this id so pass an empty voxelMask.
                        EmitTransparentMasks(id, baseX, baseY, baseZ,
                            fNX, fPX, fNY, fPY, fNZ, fPZ,
                            default, // no extra &-mask needed
                            transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                    }
                }
            }

            return true;
        }
    }
}

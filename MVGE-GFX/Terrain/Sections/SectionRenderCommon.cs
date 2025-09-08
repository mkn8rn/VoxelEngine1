using MVGE_GFX.Models;
using MVGE_GFX.Textures;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Shared constants for 16x16x16 sections
        // Note: linear index li = ((z * 16 + x) * 16) + y (column-major in Y)
        internal const int SECTION_SIZE = 16;
        internal const int STRIDE_X = 16;   // +X neighbor in linear space
        internal const int STRIDE_Y = 1;    // +Y neighbor in linear space
        internal const int STRIDE_Z = 256;  // +Z neighbor in linear space (16*16)

        // ------------------------------------------------------------------------------------
        // Ensure boundary masks (shared static arrays)
        // ------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureBoundaryMasks()
        {
            if (_boundaryMasksInit) return;

            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        int li = ((z * 16 + x) * 16) + y;  // linear index
                        int w = li >> 6;                  // word index (0..63)
                        int b = li & 63;                  // bit index inside word
                        ulong bit = 1UL << b;

                        if (x == 0) _maskX0[w] |= bit; else if (x == 15) _maskX15[w] |= bit;
                        if (y == 0) _maskY0[w] |= bit; else if (y == 15) _maskY15[w] |= bit;
                        if (z == 0) _maskZ0[w] |= bit; else if (z == 15) _maskZ15[w] |= bit;
                    }
                }
            }
            _boundaryMasksInit = true;
        }

        // ------------------------------------------------------------------------------------
        // Encoding/decoding linear index <-> (lx,ly,lz)
        // ------------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureLiDecode()
        {
            if (_liDecodeInit) return;

            _lxFromLi = new byte[4096];
            _lyFromLi = new byte[4096];
            _lzFromLi = new byte[4096];

            for (int li = 0; li < 4096; li++)
            {
                int ly = li & 15;
                int t = li >> 4;
                int lx = t & 15;
                int lz = t >> 4;
                _lxFromLi[li] = (byte)lx;
                _lyFromLi[li] = (byte)ly;
                _lzFromLi[li] = (byte)lz;
            }
            _liDecodeInit = true;
        }

        // ------------------------------------------------------------------------------------
        // Bitset utilities
        // ------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void BitsetShiftLeft(ReadOnlySpan<ulong> src, int bits, Span<ulong> dst)
        {
            int wordShift = bits >> 6;
            int bitShift = bits & 63;
            for (int i = 63; i >= 0; i--)
            {
                ulong v = 0;
                int si = i - wordShift;
                if (si >= 0)
                {
                    v = src[si];
                    if (bitShift != 0)
                    {
                        ulong carry = (si - 1 >= 0) ? src[si - 1] : 0UL;
                        v = (v << bitShift) | (carry >> (64 - bitShift));
                    }
                }
                dst[i] = v;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void BitsetShiftRight(ReadOnlySpan<ulong> src, int bits, Span<ulong> dst)
        {
            int wordShift = bits >> 6;
            int bitShift = bits & 63;
            for (int i = 0; i < 64; i++)
            {
                ulong v = 0;
                int si = i + wordShift;
                if (si < 64)
                {
                    v = src[si];
                    if (bitShift != 0)
                    {
                        ulong carry = (si + 1 < 64) ? src[si + 1] : 0UL;
                        v = (v >> bitShift) | (carry << (64 - bitShift));
                    }
                }
                dst[i] = v;
            }
        }

        // Iterate all set bits in a 4096-bit mask (64 ulongs) and invoke a callback with the linear index (li).
        // Optional bounds check (lx/ly/lz mins/maxs) avoids per-caller duplicate guards.
        internal static void ForEachSetBit(
            Span<ulong> mask,
            int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
            Action<int /*li*/, int /*lx*/, int /*ly*/, int /*lz*/> onBit)
        {
            // decode tables exist in the class (EnsureLiDecode defined in another partial)
            EnsureLiDecode();
            for (int wi = 0; wi < 64; wi++)
            {
                ulong word = mask[wi];
                while (word != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;

                    int ly = _lyFromLi[li];
                    int t = li >> 4;
                    int lx = t & 15;
                    int lz = t >> 4;

                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                    onBit(li, lx, ly, lz);
                }
            }
        }

        // ------------------------------------------------------------------------------------
        // Neighbor section helpers
        // ------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int SecIndex(int sxL, int syL, int szL, int syCount, int szCount)
            => ((sxL * syCount) + syL) * szCount + szL;

        // Returns a ref to the neighbor descriptor when in-bounds; otherwise returns ref to 'self'.
        // 'exists' indicates whether a real neighbor exists.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref SectionPrerenderDesc NeighborOrSelf(
            SectionPrerenderDesc[] allSecs,
            int sx, int sy, int sz,
            int dx, int dy, int dz,
            int sxCount, int syCount, int szCount,
            ref SectionPrerenderDesc self,
            out bool exists)
        {
            int nsx = sx + dx, nsy = sy + dy, nsz = sz + dz;
            if ((uint)nsx < (uint)sxCount && (uint)nsy < (uint)syCount && (uint)nsz < (uint)szCount)
            {
                exists = true;
                return ref allSecs[SecIndex(nsx, nsy, nsz, syCount, szCount)];
            }
            exists = false;
            return ref self;
        }

        // Fallback solid test against arbitrary neighbor descriptor (shared across paths).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool NeighborVoxelSolid(ref SectionPrerenderDesc n, int lx, int ly, int lz)
        {
            if (n.Kind == 0 || n.NonAirCount == 0) return false;
            switch (n.Kind)
            {
                case 1: // Uniform
                    return n.UniformBlockId != 0;
                case 2: // Sparse
                    if (n.SparseIndices != null)
                    {
                        int li = ((lz * 16 + lx) * 16) + ly;
                        var arr = n.SparseIndices;
                        for (int i = 0; i < arr.Length; i++) if (arr[i] == li) return true;
                    }
                    return false;
                case 3: // DenseExpanded
                    if (n.ExpandedDense != null)
                    {
                        int liD = ((lz * 16 + lx) * 16) + ly;
                        return n.ExpandedDense[liD] != 0;
                    }
                    return false;
                case 4: // Packed
                case 5: // MultiPacked
                    if (n.OccupancyBits != null)
                    {
                        int li = ((lz * 16 + lx) * 16) + ly;
                        return (n.OccupancyBits[li >> 6] & (1UL << (li & 63))) != 0UL;
                    }
                    return false;
                default:
                    return false;
            }
        }

        // Neighbor boundary probe using its precomputed face bitsets (with per-voxel fallback).
        // faceDir: 0..5 (-X,+X,-Y,+Y,-Z,+Z)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool NeighborBoundarySolid(ref SectionPrerenderDesc n, int faceDir, int x, int y, int z)
        {
            ulong[] mask = null;
            int localIndex = 0;
            int lx = x, ly = y, lz = z;
            switch (faceDir)
            {
                case 0: mask = n.FacePosXBits; localIndex = z * 16 + y; lx = 15; break; // neighbor +X face
                case 1: mask = n.FaceNegXBits; localIndex = z * 16 + y; lx = 0;  break; // neighbor -X face
                case 2: mask = n.FacePosYBits; localIndex = x * 16 + z; ly = 15; break; // neighbor +Y
                case 3: mask = n.FaceNegYBits; localIndex = x * 16 + z; ly = 0;  break; // neighbor -Y
                case 4: mask = n.FacePosZBits; localIndex = x * 16 + y; lz = 15; break; // neighbor +Z
                case 5: mask = n.FaceNegZBits; localIndex = x * 16 + y; lz = 0;  break; // neighbor -Z
            }
            if (mask != null)
            {
                int w = localIndex >> 6; int b = localIndex & 63;
                if ((mask[w] & (1UL << b)) != 0UL) return true;
            }
            return NeighborVoxelSolid(ref n, lx, ly, lz);
        }

        // ------------------------------------------------------------------------------------
        // Texture atlas helpers
        // ------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint ComputeTileIndex(BlockTextureAtlas atlas, ushort blk, Faces face)
        {
            // Use the minimum (x,y) tile of the 4 UVs, same as existing call sites
            var uvFace = atlas.GetBlockUVs(blk, face);
            byte minTileX = 255, minTileY = 255;
            for (int i = 0; i < 4; i++)
            {
                if (uvFace[i].x < minTileX) minTileX = uvFace[i].x;
                if (uvFace[i].y < minTileY) minTileY = uvFace[i].y;
            }
            return (uint)(minTileY * atlas.tilesX + minTileX);
        }

        // Light-weight per-chunk/per-section cache used by MultiPacked (or anywhere).
        internal sealed class TileIndexCache
        {
            private readonly uint[] _cache;
            public TileIndexCache(int capacity = 2048)
            {
                _cache = new uint[capacity];
                for (int i = 0; i < _cache.Length; i++) _cache[i] = 0xFFFFFFFFu;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public uint Get(BlockTextureAtlas atlas, ushort block, byte faceDir)
            {
                int key = (block << 3) | faceDir;
                if ((uint)key >= (uint)_cache.Length) key = 0; // clamp bucket
                uint cached = _cache[key];
                if (cached != 0xFFFFFFFFu) return cached;
                cached = ComputeTileIndex(atlas, block, (Faces)faceDir);
                _cache[key] = cached;
                return cached;
            }
        }

        // ------------------------------------------------------------------------------------
        // Emission helper (small convenience to reduce call-site noise)
        // ------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void EmitOneInstance(
            int wx, int wy, int wz, uint tileIndex, byte faceDir,
            List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            offsetList.Add((byte)wx);
            offsetList.Add((byte)wy);
            offsetList.Add((byte)wz);
            tileIndexList.Add(tileIndex);
            faceDirList.Add(faceDir);
        }

        // ------------------------------------------------------------------------------------
        // Bounds helper (world-base-clamped 0..15 local bounds)
        // ------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResolveLocalBounds(
            in SectionPrerenderDesc desc, int S,
            out int lxMin, out int lxMax, out int lyMin, out int lyMax, out int lzMin, out int lzMax)
        {
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }
            else
            {
                lxMin = 0; lxMax = S - 1;
                lyMin = 0; lyMax = S - 1;
                lzMin = 0; lzMax = S - 1;
            }
        }

        // Precompute tileIndex per face (detects if all faces share the same tile and keeps a fast path in that case).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint ComputeTileIndex(ushort blk, Faces face)
        {
            var uvFace = atlas.GetBlockUVs(blk, face);
            byte minTileX = 255, minTileY = 255;
            for (int i = 0; i < 4; i++)
            {
                if (uvFace[i].x < minTileX) minTileX = uvFace[i].x;
                if (uvFace[i].y < minTileY) minTileY = uvFace[i].y;
            }
            return (uint)(minTileY * atlas.tilesX + minTileX);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void DecodeIndex(int li, out int lx, out int ly, out int lz)
        { ly = li & 15; int rest = li >> 4; lx = rest & 15; lz = rest >> 4; }
    }
}

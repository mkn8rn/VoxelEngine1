using MVGE_GFX.Models;
using MVGE_GFX.Textures;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Precomputed boundary masks for 16x16x16 section (column-major layout: li = ((z*16 + x)*16)+y )
        private static readonly ulong[] _maskX0 = new ulong[64];
        private static readonly ulong[] _maskX15 = new ulong[64];
        private static readonly ulong[] _maskY0 = new ulong[64];
        private static readonly ulong[] _maskY15 = new ulong[64];
        private static readonly ulong[] _maskZ0 = new ulong[64];
        private static readonly ulong[] _maskZ15 = new ulong[64];
        private static bool _boundaryMasksInit;

        // li -> local coordinate decode tables
        private static byte[] _lxFromLi; // length 4096
        private static byte[] _lyFromLi;
        private static byte[] _lzFromLi;
        private static bool _liDecodeInit;

        // Optional prebuilt vertex patterns (currently unused in this method)
        private static byte[][] _faceVertexBytes; // index by (int)Faces
        private static bool _faceVertexInit;

        // Shared constants for 16x16x16 sections
        // Note: linear index li = ((z * 16 + x) * 16) + y (column-major in Y)
        internal const int SECTION_SIZE = 16;
        internal const int STRIDE_X = 16;   // +X neighbor in linear space
        internal const int STRIDE_Y = 1;    // +Y neighbor in linear space
        internal const int STRIDE_Z = 256;  // +Z neighbor in linear space (16*16)

        // ------------------------------------------------------------------------------------
        // Uniform face tile cache (per block id) to avoid recomputing PrecomputePerFaceTiles for
        // repeated uniform sections of the same block. Cache entries store whether all faces
        // share a single tile and each individual face tile index. This is global because the
        // atlas layout is global for all SectionRender instances.
        // ------------------------------------------------------------------------------------
        private struct UniformFaceTileSet
        {
            public bool AllSame;
            public uint SingleTile;
            public uint TileNX, TilePX, TileNY, TilePY, TileNZ, TilePZ;
        }
        private static readonly ConcurrentDictionary<ushort, UniformFaceTileSet> _uniformFaceTileCache = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private UniformFaceTileSet GetUniformFaceTileSet(ushort blockId)
        {
            if (_uniformFaceTileCache.TryGetValue(blockId, out var set)) return set;
            Span<uint> tmp = stackalloc uint[6];
            PrecomputePerFaceTiles(blockId, out bool allSame, out uint shared, tmp);
            set = new UniformFaceTileSet
            {
                AllSame = allSame,
                SingleTile = shared,
                TileNX = tmp[0],
                TilePX = tmp[1],
                TileNY = tmp[2],
                TilePY = tmp[3],
                TileNZ = tmp[4],
                TilePZ = tmp[5]
            };
            return _uniformFaceTileCache.GetOrAdd(blockId, set);
        }

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
        // Emits a single instance (voxel face) into the output lists.
        // ------------------------------------------------------------------------------------
        // Popcount of a 4096-bit mask (64 ulongs)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PopCountMask(ReadOnlySpan<ulong> mask)
        {
            int c = 0;
            for (int i = 0; i < 64; i++) c += BitOperations.PopCount(mask[i]);
            return c;
        }

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

        // populates perFace[6] and reports if all faces share the same tile.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PrecomputePerFaceTiles(ushort block, out bool allSame, out uint shared, Span<uint> perFace)
        {
            perFace[0] = ComputeTileIndex(block, Faces.LEFT);
            perFace[1] = ComputeTileIndex(block, Faces.RIGHT);
            perFace[2] = ComputeTileIndex(block, Faces.BOTTOM);
            perFace[3] = ComputeTileIndex(block, Faces.TOP);
            perFace[4] = ComputeTileIndex(block, Faces.BACK);
            perFace[5] = ComputeTileIndex(block, Faces.FRONT);
            shared = perFace[0];
            allSame = perFace[1] == shared && perFace[2] == shared && perFace[3] == shared && perFace[4] == shared && perFace[5] == shared;
        }

        // Helper: build an allowed-bounds mask (bits set only where lx/ly/lz within provided inclusive ranges) then AND with each face mask.
        // This removes the need for per-bit bounds checks during emission for trimmed masks.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ApplyBoundsMask(int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
                                             Span<ulong> faceNX, Span<ulong> facePX, Span<ulong> faceNY, Span<ulong> facePY,
                                             Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            // Fast exit when bounds cover whole section.
            if (lxMin == 0 && lxMax == 15 && lyMin == 0 && lyMax == 15 && lzMin == 0 && lzMax == 15) return;
            Span<ulong> allowed = stackalloc ulong[64];
            for (int z = lzMin; z <= lzMax; z++)
            {
                for (int x = lxMin; x <= lxMax; x++)
                {
                    int baseLi = (z * 16 + x) * 16; // start y=0
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int li = baseLi + y;
                        int w = li >> 6; int b = li & 63;
                        allowed[w] |= 1UL << b;
                    }
                }
            }
            for (int i = 0; i < 64; i++)
            {
                ulong m = allowed[i];
                faceNX[i] &= m; facePX[i] &= m; faceNY[i] &= m; facePY[i] &= m; faceNZ[i] &= m; facePZ[i] &= m;
            }
        }

        // single shared tile index for all bits in mask (with bounds check retained)
        internal static void EmitFacesFromMask
            (Span<ulong> mask, byte faceDir,
            int baseX, int baseY, int baseZ,
            int lxMin, int lxMax, int lyMin,
            int lyMax, int lzMin, int lzMax, uint tileIndex,
            List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
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
                    int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tileIndex, faceDir, offsetList, tileIndexList, faceDirList);
                }
            }
        }

        // No-bounds variant used after ApplyBoundsMask so per-bit coordinate comparisons are eliminated.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void EmitFacesFromMaskNoBounds(
            Span<ulong> mask, byte faceDir,
            int baseX, int baseY, int baseZ,
            uint tileIndex,
            List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
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
                    int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tileIndex, faceDir, offsetList, tileIndexList, faceDirList);
                }
            }
        }

        // per-voxel tile selection (supports multi-id decoding)
        internal static void EmitFacesFromMask(
            Span<ulong> mask, byte faceDir,
            int baseX, int baseY, int baseZ,
            int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
            Func<int /*li*/, int /*lx*/, int /*ly*/, int /*lz*/, (ushort block, uint tileIndex)> perVoxel,
            List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            EnsureLiDecode();
            for (int wi = 0; wi < 64; wi++)
            {
                ulong word = mask[wi];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;
                    int ly = _lyFromLi[li];
                    int t = li >> 4; int lx = t & 15; int lz = t >> 4;
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                    var (block, tileIndex) = perVoxel(li, lx, ly, lz);
                    if (block == 0) continue; // guard
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tileIndex, faceDir, offsetList, tileIndexList, faceDirList);
                }
            }
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

        // Plane bit helper (used by boundary reintroduction logic across paths).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool PlaneBit(ulong[] plane, int index)
        {
            if (plane == null) return false;
            int w = index >> 6; int b = index & 63; if (w >= plane.Length) return false;
            return (plane[w] & (1UL << b)) != 0UL;
        }
    }
}

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

        // 1. BuildInternalFaceMasks: fills directional face masks for internal faces only (excludes boundary layers).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void BuildInternalFaceMasks(ReadOnlySpan<ulong> occ,
                                                    Span<ulong> faceNX, Span<ulong> facePX,
                                                    Span<ulong> faceNY, Span<ulong> facePY,
                                                    Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            EnsureBoundaryMasks();
            Span<ulong> shift = stackalloc ulong[64];
            // -X
            BitsetShiftLeft(occ, STRIDE_X, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskX0[i];
                faceNX[i] = candidates & ~shift[i];
            }
            // +X
            BitsetShiftRight(occ, STRIDE_X, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskX15[i];
                facePX[i] = candidates & ~shift[i];
            }
            // -Y
            BitsetShiftLeft(occ, STRIDE_Y, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskY0[i];
                faceNY[i] = candidates & ~shift[i];
            }
            // +Y
            BitsetShiftRight(occ, STRIDE_Y, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskY15[i];
                facePY[i] = candidates & ~shift[i];
            }
            // -Z
            BitsetShiftLeft(occ, STRIDE_Z, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskZ0[i];
                faceNZ[i] = candidates & ~shift[i];
            }
            // +Z
            BitsetShiftRight(occ, STRIDE_Z, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong candidates = occ[i] & ~_maskZ15[i];
                facePZ[i] = candidates & ~shift[i];
            }
        }

        // Plane bit helper centralized here (used by boundary reintroduction logic across paths).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool PlaneBit(ulong[] plane, int index)
        {
            if (plane == null) return false;
            int w = index >> 6; int b = index & 63; if (w >= plane.Length) return false;
            return (plane[w] & (1UL << b)) != 0UL;
        }

        // convenience to query a section's boundary face bitsets uniformly.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool FaceBitIsSet(ref SectionPrerenderDesc desc, int faceDir, int planeIndex)
        {
            ulong[] arr = faceDir switch
            {
                0 => desc.FaceNegXBits,
                1 => desc.FacePosXBits,
                2 => desc.FaceNegYBits,
                3 => desc.FacePosYBits,
                4 => desc.FaceNegZBits,
                5 => desc.FacePosZBits,
                _ => null
            };
            if (arr == null) return false;
            int w = planeIndex >> 6; int b = planeIndex & 63; if (w >= arr.Length) return false;
            return (arr[w] & (1UL << b)) != 0UL;
        }

        // reintroduces boundary faces that are exposed (not occluded by world planes or neighbor sections).
        // face bit is added directly into the provided faceNX..facePZ masks.
        internal static void AddVisibleBoundaryFaces(ref SectionPrerenderDesc desc,
                                                     int baseX, int baseY, int baseZ,
                                                     int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
                                                     SectionPrerenderDesc[] allSecs,
                                                     int sx, int sy, int sz,
                                                     int sxCount, int syCount, int szCount,
                                                     Span<ulong> faceNX, Span<ulong> facePX,
                                                     Span<ulong> faceNY, Span<ulong> facePY,
                                                     Span<ulong> faceNZ, Span<ulong> facePZ,
                                                     ChunkPrerenderData data)
        {
            // Neighbor descriptors (bounded fetch)
            bool hasLeft = sx > 0;   ref SectionPrerenderDesc leftSec = ref hasLeft  ? ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasRight = sx + 1 < sxCount; ref SectionPrerenderDesc rightSec = ref hasRight ? ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasDown = sy > 0;   ref SectionPrerenderDesc downSec = ref hasDown  ? ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)] : ref desc;
            bool hasUp = sy + 1 < syCount; ref SectionPrerenderDesc upSec = ref hasUp ? ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)] : ref desc;
            bool hasBack = sz > 0;   ref SectionPrerenderDesc backSec = ref hasBack  ? ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)] : ref desc;
            bool hasFront = sz + 1 < szCount; ref SectionPrerenderDesc frontSec = ref hasFront ? ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)] : ref desc;

            // World boundary plane bitsets
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // LEFT boundary (x=0)
            if (lxMin == 0 && desc.FaceNegXBits != null)
            {
                int wx = baseX;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = z * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((desc.FaceNegXBits[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wx == 0)
                        {
                            hidden = PlaneBit(planeNegX, (baseZ + z) * maxY + (baseY + y));
                        }
                        else if (hasLeft && NeighborBoundarySolid(ref leftSec, 0, 15, y, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16) + 0) * 16 + y;
                            faceNX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // RIGHT boundary (x=15)
            if (lxMax == 15 && desc.FacePosXBits != null)
            {
                int wxRight = baseX + 15;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = z * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((desc.FacePosXBits[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wxRight == maxX - 1)
                        {
                            hidden = PlaneBit(planePosX, (baseZ + z) * maxY + (baseY + y));
                        }
                        else if (hasRight && NeighborBoundarySolid(ref rightSec, 1, 0, y, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16 + 15) * 16) + y;
                            facePX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // BOTTOM boundary (y=0)
            if (lyMin == 0 && desc.FaceNegYBits != null)
            {
                int wy = baseY;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int idx = x * 16 + z; int w = idx >> 6; int b = idx & 63;
                        if ((desc.FaceNegYBits[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wy == 0)
                        {
                            hidden = PlaneBit(planeNegY, (baseX + x) * maxZ + (baseZ + z));
                        }
                        else if (hasDown && NeighborBoundarySolid(ref downSec, 2, x, 15, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 0;
                            faceNY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // TOP boundary (y=15)
            if (lyMax == 15 && desc.FacePosYBits != null)
            {
                int wyTop = baseY + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int idx = x * 16 + z; int w = idx >> 6; int b = idx & 63;
                        if ((desc.FacePosYBits[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wyTop == maxY - 1)
                        {
                            hidden = PlaneBit(planePosY, (baseX + x) * maxZ + (baseZ + z));
                        }
                        else if (hasUp && NeighborBoundarySolid(ref upSec, 3, x, 0, z)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 15;
                            facePY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // BACK boundary (z=0)
            if (lzMin == 0 && desc.FaceNegZBits != null)
            {
                int wz = baseZ;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = x * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((desc.FaceNegZBits[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wz == 0)
                        {
                            hidden = PlaneBit(planeNegZ, (baseX + x) * maxY + (baseY + y));
                        }
                        else if (hasBack && NeighborBoundarySolid(ref backSec, 4, x, y, 15)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((0 * 16 + x) * 16) + y;
                            faceNZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
            // FRONT boundary (z=15)
            if (lzMax == 15 && desc.FacePosZBits != null)
            {
                int wzFront = baseZ + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = x * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((desc.FacePosZBits[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (wzFront == maxZ - 1)
                        {
                            hidden = PlaneBit(planePosZ, (baseX + x) * maxY + (baseY + y));
                        }
                        else if (hasFront && NeighborBoundarySolid(ref frontSec, 5, x, y, 0)) hidden = true;
                        if (!hidden)
                        {
                            int li = ((15 * 16 + x) * 16) + y;
                            facePZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }
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

        // single shared tile index for all bits in mask
        internal static void EmitFacesFromMask(Span<ulong> mask, byte faceDir,
                                               int baseX, int baseY, int baseZ,
                                               int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
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
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                    EmitOneInstance(baseX + lx, baseY + ly, baseZ + lz, tileIndex, faceDir, offsetList, tileIndexList, faceDirList);
                }
            }
        }

        // per-voxel tile selection (supports multi-id decoding)
        internal static void EmitFacesFromMask(Span<ulong> mask, byte faceDir,
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
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
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
    }
}

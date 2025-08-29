using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
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

        // li -> local coordinate decode tables (optimization 13)
        private static byte[] _lxFromLi; // length 4096
        private static byte[] _lyFromLi;
        private static byte[] _lzFromLi;
        private static bool _liDecodeInit;

        // Prebuilt relative vertex byte patterns per face: 4 verts * (x,y,z) = 12 bytes (optimization 9)
        // Generated lazily from RawFaceData the first time needed.
        private static byte[][] _faceVertexBytes; // index by (int)Faces
        private static bool _faceVertexInit;

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
                        int w = li >> 6;                  // word index
                        int b = li & 63;                  // bit index within word
                        ulong bit = 1UL << b;

                        if (x == 0)
                            _maskX0[w] |= bit;
                        else if (x == 15)
                            _maskX15[w] |= bit;

                        if (y == 0)
                            _maskY0[w] |= bit;
                        else if (y == 15)
                            _maskY15[w] |= bit;

                        if (z == 0)
                            _maskZ0[w] |= bit;
                        else if (z == 15)
                            _maskZ15[w] |= bit;
                    }
                }
            }
            _boundaryMasksInit = true;
        }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureFaceVertexBytes()
        {
            if (_faceVertexInit) return;

            _faceVertexBytes = new byte[6][];
            for (int f = 0; f < 6; f++)
            {
                var face = (Faces)f;
                var vtx = RawFaceData.rawVertexData[face];
                var arr = new byte[12];
                for (int i = 0; i < 4; i++)
                {
                    int o = i * 3;
                    arr[o + 0] = (byte)vtx[i].x;
                    arr[o + 1] = (byte)vtx[i].y;
                    arr[o + 2] = (byte)vtx[i].z;
                }
                _faceVertexBytes[f] = arr;
            }
            _faceVertexInit = true;
        }

        private void EmitPackedSection(
            ref SectionPrerenderDesc desc,
            int sx,
            int sy,
            int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            int S = data.sectionSize;            // section dimension (16)
            int baseX = sx * S;                  // world base of section
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;                // chunk dimensions
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            var occ = desc.OccupancyBits; // Occupancy bitset (4096 bits => 64 ulongs)
            if (occ == null) return;

            EnsureBoundaryMasks();
            EnsureLiDecode();
            EnsureFaceVertexBytes();

            // Expect palette[0]=air, palette[1]=single block id for this optimized path
            if (desc.Palette == null || desc.Palette.Count < 2) return;
            ushort block = desc.Palette[1];
            bool opaque = BlockProperties.IsOpaque(block);

            // Neighbor chunk outer planes (used only at absolute chunk edges)
            var planeNegX = data.NeighborPlaneNegX;
            var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY;
            var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ;
            var planePosZ = data.NeighborPlanePosZ;

            // Section-local bounding region (tight sub-box if available)
            int lxMin = 0, lxMax = S - 1;
            int lyMin = 0, lyMax = S - 1;
            int lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            // Precompute plane index stride tables
            Span<int> yzZStride = stackalloc int[16];
            int baseYAdd = baseY;
            for (int lz = 0; lz < 16; lz++)
            {
                yzZStride[lz] = (baseZ + lz) * maxY;
            }
            Span<int> xzXStride = stackalloc int[16];
            for (int lx = 0; lx < 16; lx++)
            {
                xzXStride[lx] = (baseX + lx) * maxZ;
            }
            Span<int> xyXStride = stackalloc int[16];
            for (int lx = 0; lx < 16; lx++)
            {
                xyXStride[lx] = (baseX + lx) * maxY;
            }

            // Precompute UV bytes once per face
            byte[][] uvBytes = new byte[6][];
            for (int f = 0; f < 6; f++)
            {
                var face = (Faces)f;
                var uvFace = atlas.GetBlockUVs(block, face);
                var arr = new byte[8];
                for (int i = 0; i < 4; i++)
                {
                    int o = i * 2;
                    arr[o + 0] = uvFace[i].x;
                    arr[o + 1] = uvFace[i].y;
                }
                uvBytes[f] = arr;
            }

            // Section descriptors for neighbor face fast-path
            SectionPrerenderDesc[] allSecs = data.SectionDescs;
            int sxCount = data.sectionsX;
            int syCount = data.sectionsY;
            int szCount = data.sectionsZ;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int SecIndex(int sxL, int syL, int szL, int syC, int szC)
                => ((sxL * syC) + syL) * szC + szL;

            ulong[] edgePosXFromNegX = null; bool edgePosXOpaque = false;
            ulong[] edgeNegXFromPosX = null; bool edgeNegXOpaque = false;
            ulong[] edgePosYFromNegY = null; bool edgePosYOpaque = false;
            ulong[] edgeNegYFromPosY = null; bool edgeNegYOpaque = false;
            ulong[] edgePosZFromNegZ = null; bool edgePosZOpaque = false;
            ulong[] edgeNegZFromPosZ = null; bool edgeNegZOpaque = false;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void AcquireNeighborFace(
                ref ulong[] target,
                ref bool opaqueFlag,
                int nsx,
                int nsy,
                int nsz,
                Faces neededFace)
            {
                if (!opaque) return;
                if ((uint)nsx >= (uint)sxCount ||
                    (uint)nsy >= (uint)syCount ||
                    (uint)nsz >= (uint)szCount) return;

                ref var nd = ref allSecs[SecIndex(nsx, nsy, nsz, syCount, szCount)];
                if (nd.NonAirCount == 0) return;

                if (nd.Kind == 1 && BlockProperties.IsOpaque(nd.UniformBlockId))
                {
                    target = new ulong[4];
                    target[0] = target[1] = target[2] = target[3] = ulong.MaxValue;
                    opaqueFlag = true;
                    return;
                }

                if (nd.Kind == 4 &&
                    nd.BitsPerIndex == 1 &&
                    nd.Palette != null && nd.Palette.Count == 2 &&
                    nd.OccupancyBits != null &&
                    BlockProperties.IsOpaque(nd.Palette[1]))
                {
                    ulong[] src = neededFace switch
                    {
                        Faces.LEFT => nd.FacePosXBits,
                        Faces.RIGHT => nd.FaceNegXBits,
                        Faces.BOTTOM => nd.FacePosYBits,
                        Faces.TOP => nd.FaceNegYBits,
                        Faces.BACK => nd.FacePosZBits,
                        Faces.FRONT => nd.FaceNegZBits,
                        _ => null
                    };
                    if (src != null)
                    {
                        target = new ulong[4];
                        Array.Copy(src, target, 4);
                        opaqueFlag = true;
                    }
                }
            }

            if (sx > 0)
                AcquireNeighborFace(ref edgePosXFromNegX, ref edgePosXOpaque, sx - 1, sy, sz, Faces.LEFT);
            if (sx + 1 < sxCount)
                AcquireNeighborFace(ref edgeNegXFromPosX, ref edgeNegXOpaque, sx + 1, sy, sz, Faces.RIGHT);
            if (sy > 0)
                AcquireNeighborFace(ref edgePosYFromNegY, ref edgePosYOpaque, sx, sy - 1, sz, Faces.BOTTOM);
            if (sy + 1 < syCount)
                AcquireNeighborFace(ref edgeNegYFromPosY, ref edgeNegYOpaque, sx, sy + 1, sz, Faces.TOP);
            if (sz > 0)
                AcquireNeighborFace(ref edgePosZFromNegZ, ref edgePosZOpaque, sx, sy, sz - 1, Faces.BACK);
            if (sz + 1 < szCount)
                AcquireNeighborFace(ref edgeNegZFromPosZ, ref edgeNegZOpaque, sx, sy, sz + 1, Faces.FRONT);

            Span<ulong> shift = stackalloc ulong[64];
            Span<ulong> faceNegX = stackalloc ulong[64];
            Span<ulong> facePosX = stackalloc ulong[64];
            Span<ulong> faceNegY = stackalloc ulong[64];
            Span<ulong> facePosY = stackalloc ulong[64];
            Span<ulong> faceNegZ = stackalloc ulong[64];
            Span<ulong> facePosZ = stackalloc ulong[64];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftLeft(ReadOnlySpan<ulong> src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6;
                int bitShift = shiftBits & 63;
                for (int i = 63; i >= 0; i--)
                {
                    ulong val = 0;
                    int si = i - wordShift;
                    if (si >= 0)
                    {
                        val = src[si];
                        if (bitShift != 0)
                        {
                            if (si - 1 >= 0)
                                val = (val << bitShift) | (src[si - 1] >> (64 - bitShift));
                            else
                                val <<= bitShift;
                        }
                    }
                    dst[i] = val;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ShiftRight(ReadOnlySpan<ulong> src, int shiftBits, Span<ulong> dst)
            {
                int wordShift = shiftBits >> 6;
                int bitShift = shiftBits & 63;
                for (int i = 0; i < 64; i++)
                {
                    ulong val = 0;
                    int si = i + wordShift;
                    if (si < 64)
                    {
                        val = src[si];
                        if (bitShift != 0)
                        {
                            if (si + 1 < 64)
                                val = (val >> bitShift) | (src[si + 1] << (64 - bitShift));
                            else
                                val >>= bitShift;
                        }
                    }
                    dst[i] = val;
                }
            }

            const int strideX = 16;
            const int strideY = 1;
            const int strideZ = 256;

            ShiftLeft(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX0[i];
                faceNegX[i] = cand & ~shift[i];
            }

            ShiftRight(occ, strideX, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskX15[i];
                facePosX[i] = cand & ~shift[i];
            }

            ShiftLeft(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY0[i];
                faceNegY[i] = cand & ~shift[i];
            }

            ShiftRight(occ, strideY, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskY15[i];
                facePosY[i] = cand & ~shift[i];
            }

            ShiftLeft(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ0[i];
                faceNegZ[i] = cand & ~shift[i];
            }

            ShiftRight(occ, strideZ, shift);
            for (int i = 0; i < 64; i++)
            {
                ulong cand = occ[i] & ~_maskZ15[i];
                facePosZ[i] = cand & ~shift[i];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool PlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return false;
                int w = index >> 6;
                int b = index & 63;
                return w < plane.Length && (plane[w] & (1UL << b)) != 0UL;
            }

            // ------------------------------------------------------------
            // Boundary integration: evaluate boundary voxels and extend masks
            // ------------------------------------------------------------

            // LEFT boundary (x == 0)
            if (lxMin == 0)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskX0[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;

                        int ly = _lyFromLi[li];
                        int lz = _lzFromLi[li];
                        if (ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                            continue;

                        bool hidden = false;
                        if (opaque)
                        {
                            int planeIdx = yzZStride[lz] + baseYAdd + ly;
                            if (baseX == 0)
                            {
                                if (PlaneBit(planeNegX, planeIdx)) hidden = true;
                            }
                            else if (edgePosXFromNegX != null && edgePosXOpaque)
                            {
                                int yz = lz * 16 + ly;
                                if ((edgePosXFromNegX[yz >> 6] & (1UL << (yz & 63))) != 0UL) hidden = true;
                            }
                            else if (sx > 0)
                            {
                                ushort nb = GetBlock(baseX - 1, baseY + ly, baseZ + lz);
                                if (BlockProperties.IsOpaque(nb)) hidden = true;
                            }
                        }
                        if (!hidden)
                            faceNegX[wi] |= 1UL << bit;
                    }
                }
            }

            // RIGHT boundary (x == 15)
            if (lxMax == S - 1)
            {
                int worldX = baseX + (S - 1);
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskX15[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;

                        int ly = _lyFromLi[li];
                        int lz = _lzFromLi[li];
                        if (ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                            continue;

                        bool hidden = false;
                        if (opaque)
                        {
                            int planeIdx = yzZStride[lz] + baseYAdd + ly;
                            if (worldX == maxX - 1)
                            {
                                if (PlaneBit(planePosX, planeIdx)) hidden = true;
                            }
                            else if (edgeNegXFromPosX != null && edgeNegXOpaque)
                            {
                                int yz = lz * 16 + ly;
                                if ((edgeNegXFromPosX[yz >> 6] & (1UL << (yz & 63))) != 0UL) hidden = true;
                            }
                            else if (sx + 1 < sxCount)
                            {
                                ushort nb = GetBlock(worldX + 1, baseY + ly, baseZ + lz);
                                if (BlockProperties.IsOpaque(nb)) hidden = true;
                            }
                        }
                        if (!hidden)
                            facePosX[wi] |= 1UL << bit;
                    }
                }
            }

            // BOTTOM boundary (y == 0)
            if (lyMin == 0)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskY0[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;

                        int lx = _lxFromLi[li];
                        int lz = _lzFromLi[li];
                        if (lx < lxMin || lx > lxMax || lz < lzMin || lz > lzMax)
                            continue;

                        bool hidden = false;
                        if (opaque)
                        {
                            int planeIdx = xzXStride[lx] + (baseZ + lz);
                            if (baseY == 0)
                            {
                                if (PlaneBit(planeNegY, planeIdx)) hidden = true;
                            }
                            else if (edgePosYFromNegY != null && edgePosYOpaque)
                            {
                                int xz = lx * 16 + lz;
                                if ((edgePosYFromNegY[xz >> 6] & (1UL << (xz & 63))) != 0UL) hidden = true;
                            }
                            else if (sy > 0)
                            {
                                ushort nb = GetBlock(baseX + lx, baseY - 1, baseZ + lz);
                                if (BlockProperties.IsOpaque(nb)) hidden = true;
                            }
                        }
                        if (!hidden)
                            faceNegY[wi] |= 1UL << bit;
                    }
                }
            }

            // TOP boundary (y == 15)
            if (lyMax == S - 1)
            {
                int worldY = baseY + (S - 1);
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskY15[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;

                        int lx = _lxFromLi[li];
                        int lz = _lzFromLi[li];
                        if (lx < lxMin || lx > lxMax || lz < lzMin || lz > lzMax)
                            continue;

                        bool hidden = false;
                        if (opaque)
                        {
                            int planeIdx = xzXStride[lx] + (baseZ + lz);
                            if (worldY == maxY - 1)
                            {
                                if (PlaneBit(planePosY, planeIdx)) hidden = true;
                            }
                            else if (edgeNegYFromPosY != null && edgeNegYOpaque)
                            {
                                int xz = lx * 16 + lz;
                                if ((edgeNegYFromPosY[xz >> 6] & (1UL << (xz & 63))) != 0UL) hidden = true;
                            }
                            else if (sy + 1 < syCount)
                            {
                                ushort nb = GetBlock(baseX + lx, worldY + 1, baseZ + lz);
                                if (BlockProperties.IsOpaque(nb)) hidden = true;
                            }
                        }
                        if (!hidden)
                            facePosY[wi] |= 1UL << bit;
                    }
                }
            }

            // BACK boundary (z == 0)
            if (lzMin == 0)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskZ0[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;

                        int lx = _lxFromLi[li];
                        int ly = _lyFromLi[li];
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax)
                            continue;

                        bool hidden = false;
                        if (opaque)
                        {
                            int planeIdx = xyXStride[lx] + baseYAdd + ly;
                            if (baseZ == 0)
                            {
                                if (PlaneBit(planeNegZ, planeIdx)) hidden = true;
                            }
                            else if (edgePosZFromNegZ != null && edgePosZOpaque)
                            {
                                int xy = lx * 16 + ly;
                                if ((edgePosZFromNegZ[xy >> 6] & (1UL << (xy & 63))) != 0UL) hidden = true;
                            }
                            else if (sz > 0)
                            {
                                ushort nb = GetBlock(baseX + lx, baseY + ly, baseZ - 1);
                                if (BlockProperties.IsOpaque(nb)) hidden = true;
                            }
                        }
                        if (!hidden)
                            faceNegZ[wi] |= 1UL << bit;
                    }
                }
            }

            // FRONT boundary (z == 15)
            if (lzMax == S - 1)
            {
                int worldZ = baseZ + (S - 1);
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = occ[wi] & _maskZ15[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        int li = (wi << 6) + bit;
                        word &= word - 1;

                        int lx = _lxFromLi[li];
                        int ly = _lyFromLi[li];
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax)
                            continue;

                        bool hidden = false;
                        if (opaque)
                        {
                            int planeIdx = xyXStride[lx] + baseYAdd + ly;
                            if (worldZ == maxZ - 1)
                            {
                                if (PlaneBit(planePosZ, planeIdx)) hidden = true;
                            }
                            else if (edgeNegZFromPosZ != null && edgeNegZOpaque)
                            {
                                int xy = lx * 16 + ly;
                                if ((edgeNegZFromPosZ[xy >> 6] & (1UL << (xy & 63))) != 0UL) hidden = true;
                            }
                            else if (sz + 1 < szCount)
                            {
                                ushort nb = GetBlock(baseX + lx, baseY + ly, worldZ + 1);
                                if (BlockProperties.IsOpaque(nb)) hidden = true;
                            }
                        }
                        if (!hidden)
                            facePosZ[wi] |= 1UL << bit;
                    }
                }
            }

            // ---------------- Emission (decode proper local coords for each face) ----------------
            int vbInt = (int)vertBase;

            byte[] faceVertsLeft = _faceVertexBytes[(int)Faces.LEFT];
            byte[] faceVertsRight = _faceVertexBytes[(int)Faces.RIGHT];
            byte[] faceVertsBottom = _faceVertexBytes[(int)Faces.BOTTOM];
            byte[] faceVertsTop = _faceVertexBytes[(int)Faces.TOP];
            byte[] faceVertsBack = _faceVertexBytes[(int)Faces.BACK];
            byte[] faceVertsFront = _faceVertexBytes[(int)Faces.FRONT];

            byte[] uvLeft = uvBytes[(int)Faces.LEFT];
            byte[] uvRight = uvBytes[(int)Faces.RIGHT];
            byte[] uvBottom = uvBytes[(int)Faces.BOTTOM];
            byte[] uvTop = uvBytes[(int)Faces.TOP];
            byte[] uvBack = uvBytes[(int)Faces.BACK];
            byte[] uvFront = uvBytes[(int)Faces.FRONT];

            for (int wi = 0; wi < 64; wi++)
            {
                // -X
                ulong wNX = faceNegX[wi];
                while (wNX != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(wNX);
                    int li = (wi << 6) + bit;
                    wNX &= wNX - 1;

                    int lx = _lxFromLi[li];
                    int ly = _lyFromLi[li];
                    int lz = _lzFromLi[li];
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                        continue;

                    int wx = baseX + lx;
                    int wy = baseY + ly;
                    int wz = baseZ + lz;

                    vertList.Add((byte)(faceVertsLeft[0] + wx));
                    vertList.Add((byte)(faceVertsLeft[1] + wy));
                    vertList.Add((byte)(faceVertsLeft[2] + wz));
                    vertList.Add((byte)(faceVertsLeft[3] + wx));
                    vertList.Add((byte)(faceVertsLeft[4] + wy));
                    vertList.Add((byte)(faceVertsLeft[5] + wz));
                    vertList.Add((byte)(faceVertsLeft[6] + wx));
                    vertList.Add((byte)(faceVertsLeft[7] + wy));
                    vertList.Add((byte)(faceVertsLeft[8] + wz));
                    vertList.Add((byte)(faceVertsLeft[9] + wx));
                    vertList.Add((byte)(faceVertsLeft[10] + wy));
                    vertList.Add((byte)(faceVertsLeft[11] + wz));

                    uvList.Add(uvLeft[0]);
                    uvList.Add(uvLeft[1]);
                    uvList.Add(uvLeft[2]);
                    uvList.Add(uvLeft[3]);
                    uvList.Add(uvLeft[4]);
                    uvList.Add(uvLeft[5]);
                    uvList.Add(uvLeft[6]);
                    uvList.Add(uvLeft[7]);

                    uint b = (uint)vbInt;
                    idxList.Add(b); idxList.Add(b + 1); idxList.Add(b + 2);
                    idxList.Add(b + 2); idxList.Add(b + 3); idxList.Add(b);
                    vbInt += 4;
                }

                // +X
                ulong wPX = facePosX[wi];
                while (wPX != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(wPX);
                    int li = (wi << 6) + bit;
                    wPX &= wPX - 1;

                    int lx = _lxFromLi[li];
                    int ly = _lyFromLi[li];
                    int lz = _lzFromLi[li];
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                        continue;

                    int wx = baseX + lx;
                    int wy = baseY + ly;
                    int wz = baseZ + lz;

                    vertList.Add((byte)(faceVertsRight[0] + wx));
                    vertList.Add((byte)(faceVertsRight[1] + wy));
                    vertList.Add((byte)(faceVertsRight[2] + wz));
                    vertList.Add((byte)(faceVertsRight[3] + wx));
                    vertList.Add((byte)(faceVertsRight[4] + wy));
                    vertList.Add((byte)(faceVertsRight[5] + wz));
                    vertList.Add((byte)(faceVertsRight[6] + wx));
                    vertList.Add((byte)(faceVertsRight[7] + wy));
                    vertList.Add((byte)(faceVertsRight[8] + wz));
                    vertList.Add((byte)(faceVertsRight[9] + wx));
                    vertList.Add((byte)(faceVertsRight[10] + wy));
                    vertList.Add((byte)(faceVertsRight[11] + wz));

                    uvList.Add(uvRight[0]);
                    uvList.Add(uvRight[1]);
                    uvList.Add(uvRight[2]);
                    uvList.Add(uvRight[3]);
                    uvList.Add(uvRight[4]);
                    uvList.Add(uvRight[5]);
                    uvList.Add(uvRight[6]);
                    uvList.Add(uvRight[7]);

                    uint b = (uint)vbInt;
                    idxList.Add(b); idxList.Add(b + 1); idxList.Add(b + 2);
                    idxList.Add(b + 2); idxList.Add(b + 3); idxList.Add(b);
                    vbInt += 4;
                }

                // -Y
                ulong wNY = faceNegY[wi];
                while (wNY != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(wNY);
                    int li = (wi << 6) + bit;
                    wNY &= wNY - 1;

                    int lx = _lxFromLi[li];
                    int ly = _lyFromLi[li];
                    int lz = _lzFromLi[li];
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                        continue;

                    int wx = baseX + lx;
                    int wy = baseY + ly;
                    int wz = baseZ + lz;

                    vertList.Add((byte)(faceVertsBottom[0] + wx));
                    vertList.Add((byte)(faceVertsBottom[1] + wy));
                    vertList.Add((byte)(faceVertsBottom[2] + wz));
                    vertList.Add((byte)(faceVertsBottom[3] + wx));
                    vertList.Add((byte)(faceVertsBottom[4] + wy));
                    vertList.Add((byte)(faceVertsBottom[5] + wz));
                    vertList.Add((byte)(faceVertsBottom[6] + wx));
                    vertList.Add((byte)(faceVertsBottom[7] + wy));
                    vertList.Add((byte)(faceVertsBottom[8] + wz));
                    vertList.Add((byte)(faceVertsBottom[9] + wx));
                    vertList.Add((byte)(faceVertsBottom[10] + wy));
                    vertList.Add((byte)(faceVertsBottom[11] + wz));

                    uvList.Add(uvBottom[0]);
                    uvList.Add(uvBottom[1]);
                    uvList.Add(uvBottom[2]);
                    uvList.Add(uvBottom[3]);
                    uvList.Add(uvBottom[4]);
                    uvList.Add(uvBottom[5]);
                    uvList.Add(uvBottom[6]);
                    uvList.Add(uvBottom[7]);

                    uint b = (uint)vbInt;
                    idxList.Add(b); idxList.Add(b + 1); idxList.Add(b + 2);
                    idxList.Add(b + 2); idxList.Add(b + 3); idxList.Add(b);
                    vbInt += 4;
                }

                // +Y
                ulong wPY = facePosY[wi];
                while (wPY != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(wPY);
                    int li = (wi << 6) + bit;
                    wPY &= wPY - 1;

                    int lx = _lxFromLi[li];
                    int ly = _lyFromLi[li];
                    int lz = _lzFromLi[li];
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                        continue;

                    int wx = baseX + lx;
                    int wy = baseY + ly;
                    int wz = baseZ + lz;

                    vertList.Add((byte)(faceVertsTop[0] + wx));
                    vertList.Add((byte)(faceVertsTop[1] + wy));
                    vertList.Add((byte)(faceVertsTop[2] + wz));
                    vertList.Add((byte)(faceVertsTop[3] + wx));
                    vertList.Add((byte)(faceVertsTop[4] + wy));
                    vertList.Add((byte)(faceVertsTop[5] + wz));
                    vertList.Add((byte)(faceVertsTop[6] + wx));
                    vertList.Add((byte)(faceVertsTop[7] + wy));
                    vertList.Add((byte)(faceVertsTop[8] + wz));
                    vertList.Add((byte)(faceVertsTop[9] + wx));
                    vertList.Add((byte)(faceVertsTop[10] + wy));
                    vertList.Add((byte)(faceVertsTop[11] + wz));

                    uvList.Add(uvTop[0]);
                    uvList.Add(uvTop[1]);
                    uvList.Add(uvTop[2]);
                    uvList.Add(uvTop[3]);
                    uvList.Add(uvTop[4]);
                    uvList.Add(uvTop[5]);
                    uvList.Add(uvTop[6]);
                    uvList.Add(uvTop[7]);

                    uint b = (uint)vbInt;
                    idxList.Add(b); idxList.Add(b + 1); idxList.Add(b + 2);
                    idxList.Add(b + 2); idxList.Add(b + 3); idxList.Add(b);
                    vbInt += 4;
                }

                // -Z
                ulong wNZ = faceNegZ[wi];
                while (wNZ != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(wNZ);
                    int li = (wi << 6) + bit;
                    wNZ &= wNZ - 1;

                    int lx = _lxFromLi[li];
                    int ly = _lyFromLi[li];
                    int lz = _lzFromLi[li];
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                        continue;

                    int wx = baseX + lx;
                    int wy = baseY + ly;
                    int wz = baseZ + lz;

                    vertList.Add((byte)(faceVertsBack[0] + wx));
                    vertList.Add((byte)(faceVertsBack[1] + wy));
                    vertList.Add((byte)(faceVertsBack[2] + wz));
                    vertList.Add((byte)(faceVertsBack[3] + wx));
                    vertList.Add((byte)(faceVertsBack[4] + wy));
                    vertList.Add((byte)(faceVertsBack[5] + wz));
                    vertList.Add((byte)(faceVertsBack[6] + wx));
                    vertList.Add((byte)(faceVertsBack[7] + wy));
                    vertList.Add((byte)(faceVertsBack[8] + wz));
                    vertList.Add((byte)(faceVertsBack[9] + wx));
                    vertList.Add((byte)(faceVertsBack[10] + wy));
                    vertList.Add((byte)(faceVertsBack[11] + wz));

                    uvList.Add(uvBack[0]);
                    uvList.Add(uvBack[1]);
                    uvList.Add(uvBack[2]);
                    uvList.Add(uvBack[3]);
                    uvList.Add(uvBack[4]);
                    uvList.Add(uvBack[5]);
                    uvList.Add(uvBack[6]);
                    uvList.Add(uvBack[7]);

                    uint b = (uint)vbInt;
                    idxList.Add(b); idxList.Add(b + 1); idxList.Add(b + 2);
                    idxList.Add(b + 2); idxList.Add(b + 3); idxList.Add(b);
                    vbInt += 4;
                }

                // +Z
                ulong wPZ = facePosZ[wi];
                while (wPZ != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(wPZ);
                    int li = (wi << 6) + bit;
                    wPZ &= wPZ - 1;

                    int lx = _lxFromLi[li];
                    int ly = _lyFromLi[li];
                    int lz = _lzFromLi[li];
                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax)
                        continue;

                    int wx = baseX + lx;
                    int wy = baseY + ly;
                    int wz = baseZ + lz;

                    vertList.Add((byte)(faceVertsFront[0] + wx));
                    vertList.Add((byte)(faceVertsFront[1] + wy));
                    vertList.Add((byte)(faceVertsFront[2] + wz));
                    vertList.Add((byte)(faceVertsFront[3] + wx));
                    vertList.Add((byte)(faceVertsFront[4] + wy));
                    vertList.Add((byte)(faceVertsFront[5] + wz));
                    vertList.Add((byte)(faceVertsFront[6] + wx));
                    vertList.Add((byte)(faceVertsFront[7] + wy));
                    vertList.Add((byte)(faceVertsFront[8] + wz));
                    vertList.Add((byte)(faceVertsFront[9] + wx));
                    vertList.Add((byte)(faceVertsFront[10] + wy));
                    vertList.Add((byte)(faceVertsFront[11] + wz));

                    uvList.Add(uvFront[0]);
                    uvList.Add(uvFront[1]);
                    uvList.Add(uvFront[2]);
                    uvList.Add(uvFront[3]);
                    uvList.Add(uvFront[4]);
                    uvList.Add(uvFront[5]);
                    uvList.Add(uvFront[6]);
                    uvList.Add(uvFront[7]);

                    uint b = (uint)vbInt;
                    idxList.Add(b); idxList.Add(b + 1); idxList.Add(b + 2);
                    idxList.Add(b + 2); idxList.Add(b + 3); idxList.Add(b);
                    vbInt += 4;
                }
            }

            vertBase = (uint)vbInt; // write back updated vertex base
        }
    }
}

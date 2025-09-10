using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
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
        // reintroduces boundary faces that are exposed (not occluded by world planes or neighbor sections).
        // face bit is added directly into the provided faceNX..facePZ masks.
        internal static void AddVisibleBoundaryFaces(
            ref SectionPrerenderDesc desc,
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
            bool hasLeft = sx > 0; ref SectionPrerenderDesc leftSec = ref hasLeft ? ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasRight = sx + 1 < sxCount; ref SectionPrerenderDesc rightSec = ref hasRight ? ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasDown = sy > 0; ref SectionPrerenderDesc downSec = ref hasDown ? ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)] : ref desc;
            bool hasUp = sy + 1 < syCount; ref SectionPrerenderDesc upSec = ref hasUp ? ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)] : ref desc;
            bool hasBack = sz > 0; ref SectionPrerenderDesc backSec = ref hasBack ? ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)] : ref desc;
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
    }
}

using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

        // Selective variant: identical to AddVisibleBoundaryFaces but skips specific face directions when skipDir[faceDir] is true.
        // This allows packed path neighbor full-solid classification to suppress boundary reinsertion for occluded faces while
        // preserving internal face masks. Internal mask emission still occurs; only boundary augmentation is bypassed for skipped faces.
        internal static void AddVisibleBoundaryFacesSelective(
            ref SectionPrerenderDesc desc,
            int baseX, int baseY, int baseZ,
            int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
            SectionPrerenderDesc[] allSecs,
            int sx, int sy, int sz,
            int sxCount, int syCount, int szCount,
            Span<bool> skipDir,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ,
            ChunkPrerenderData data)
        {
            // Quick path: if no skips requested delegate to original method (avoids duplicate logic cost when classification found nothing).
            if (!skipDir[0] && !skipDir[1] && !skipDir[2] && !skipDir[3] && !skipDir[4] && !skipDir[5])
            {
                AddVisibleBoundaryFaces(ref desc, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                                        allSecs, sx, sy, sz, sxCount, syCount, szCount,
                                        faceNX, facePX, faceNY, facePY, faceNZ, facePZ, data);
                return;
            }

            // Neighbor descriptors (bounded fetch)
            bool hasLeft = sx > 0; ref SectionPrerenderDesc leftSec = ref hasLeft ? ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasRight = sx + 1 < sxCount; ref SectionPrerenderDesc rightSec = ref hasRight ? ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasDown = sy > 0; ref SectionPrerenderDesc downSec = ref hasDown ? ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)] : ref desc;
            bool hasUp = sy + 1 < syCount; ref SectionPrerenderDesc upSec = ref hasUp ? ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)] : ref desc;
            bool hasBack = sz > 0; ref SectionPrerenderDesc backSec = ref hasBack ? ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)] : ref desc;
            bool hasFront = sz + 1 < szCount; ref SectionPrerenderDesc frontSec = ref hasFront ? ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)] : ref desc;

            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // LEFT boundary (x=0)
            if (!skipDir[0] && lxMin == 0 && desc.FaceNegXBits != null)
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
            if (!skipDir[1] && lxMax == 15 && desc.FacePosXBits != null)
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
            if (!skipDir[2] && lyMin == 0 && desc.FaceNegYBits != null)
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
            if (!skipDir[3] && lyMax == 15 && desc.FacePosYBits != null)
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
            if (!skipDir[4] && lzMin == 0 && desc.FaceNegZBits != null)
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
            if (!skipDir[5] && lzMax == 15 && desc.FacePosZBits != null)
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
                case 1: mask = n.FaceNegXBits; localIndex = z * 16 + y; lx = 0; break; // neighbor -X face
                case 2: mask = n.FacePosYBits; localIndex = x * 16 + z; ly = 15; break; // neighbor +Y
                case 3: mask = n.FaceNegYBits; localIndex = x * 16 + z; ly = 0; break; // neighbor -Y
                case 4: mask = n.FacePosZBits; localIndex = x * 16 + y; lz = 15; break; // neighbor +Z
                case 5: mask = n.FaceNegZBits; localIndex = x * 16 + y; lz = 0; break; // neighbor -Z
            }
            if (mask != null)
            {
                int w = localIndex >> 6; int b = localIndex & 63; if (w < mask.Length && (mask[w] & (1UL << b)) != 0UL) return true;
            }
            return NeighborVoxelSolid(ref n, lx, ly, lz);
        }

        // Neighbor section queries for whole-face occlusion
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref SectionPrerenderDesc Neighbor(int nsx, int nsy, int nsz)
        {
            int idx = ((nsx * data.sectionsY) + nsy) * data.sectionsZ + nsz;
            return ref data.SectionDescs[idx];
        }

        // Helper: treat neighbor as fully solid if it is uniform non-air OR a single-id packed (Kind 4) fully filled OR a multi-packed (Kind 5) with palette indicating full occupancy (NonAirCount==4096).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool NeighborFullySolid(ref SectionPrerenderDesc n)
        {
            if (n.Kind == 1 && n.UniformBlockId != 0) return true; // uniform solid
            if ((n.Kind == 4 || n.Kind == 5) && n.NonAirCount == 4096) return true;
            return false;
        }

        // Neighbor mask popcount (occluded cells) for predicted capacity. Guard length to plane size (<=256 bits)
        int MaskOcclusionCount(ulong[] mask)
        {
            if (mask == null) return 0;
            int occluded = 0; int neededWords = 4; // 256 bits -> 4 * 64
            for (int i = 0; i < mask.Length && i < neededWords; i++) occluded += BitOperations.PopCount(mask[i]);
            return occluded;
        }

        // Helper for world-edge plane quick skip if fully occluded in the SxS window.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsWorldPlaneFullySet(ulong[] plane, int startA, int startB, int countA, int countB, int strideB)
        {
            if (plane == null) return false;
            for (int a = 0; a < countA; a++)
            {
                int baseIndex = (startA + a) * strideB + startB;
                int remaining = countB;
                int idx = baseIndex;
                while (remaining-- > 0)
                {
                    int w = idx >> 6; int b = idx & 63; if (w >= plane.Length) return false;
                    if ((plane[w] & (1UL << b)) == 0UL) return false; // found a hole
                    idx++;
                }
            }
            return true;
        }

        // Count visible cells for world boundary face (used for capacity prediction)
        int CountVisibleWorldBoundary(ulong[] plane, int startA, int startB, int countA, int countB, int strideB)
        {
            int total = countA * countB;
            if (total <= 0) return 0;
            if (plane == null) return total; // no occlusion plane -> all visible
            int visible = 0;
            for (int a = 0; a < countA; a++)
            {
                int baseIndex = (startA + a) * strideB + startB;
                int idx = baseIndex;
                for (int b = 0; b < countB; b++, idx++)
                {
                    int w = idx >> 6; int bit = idx & 63; if (w >= plane.Length) { visible++; continue; }
                    if ((plane[w] & (1UL << bit)) == 0UL) visible++; // hole -> visible
                }
            }
            return visible;
        }
    }
}

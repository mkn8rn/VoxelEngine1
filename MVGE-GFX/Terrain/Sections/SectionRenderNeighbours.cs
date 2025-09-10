using MVGE_INF.Models.Generation;
using System;
using System.Buffers.Text;
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
                int w = localIndex >> 6; int b = localIndex & 63;
                if ((mask[w] & (1UL << b)) != 0UL) return true;
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

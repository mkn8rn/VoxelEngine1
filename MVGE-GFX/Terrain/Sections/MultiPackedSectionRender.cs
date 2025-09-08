using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Numerics;
using System.Runtime.CompilerServices;
using MVGE_GFX.Models;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        /// Emits faces for a MultiPacked section (Kind == 5).
        /// MultiPacked: multi-id palette packed bit representation (BitsPerIndex >= 2 OR palette.Count > 2).
        /// Strategy mirrors single-id packed fast path but adds per-voxel block decode and tile index caching.
        private bool EmitMultiPackedSectionInstances(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz, int S,
            List<byte> offsetList,
            List<uint> tileIndexList,
            List<byte> faceDirList)
        {
            // Validate preconditions.
            if (desc.Kind != 5) return false; // Not MultiPacked (caller will fallback / choose other path)
            if (desc.NonAirCount == 0 ||
                desc.OccupancyBits == null ||
                desc.PackedBitData == null ||
                desc.Palette == null) return false; // nothing to emit / cannot decode
            // If palette effectively single non‑air id with 1 bit index prefer the single packed path.
            if (desc.Palette.Count <= 2 && desc.BitsPerIndex == 1) return false;

            EnsureBoundaryMasks();   // Precomputed boundary position masks
            EnsureLiDecode();        // Linear index -> (lx,ly,lz) tables

            var occ = desc.OccupancyBits; // 64 * ulong => 4096 occupancy bits

            // World base coordinates for this section
            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            // Optional tight local bounds (0..15) supplied by finalization
            int lxMin = 0, lxMax = S - 1;
            int lyMin = 0, lyMax = S - 1;
            int lzMin = 0, lzMax = S - 1;
            if (desc.HasBounds)
            {
                lxMin = desc.MinLX; lxMax = desc.MaxLX;
                lyMin = desc.MinLY; lyMax = desc.MaxLY;
                lzMin = desc.MinLZ; lzMax = desc.MaxLZ;
            }

            // Neighbor section descriptor access (for cross-section occlusion suppression)
            int sxCount = data.sectionsX;
            int syCount = data.sectionsY;
            int szCount = data.sectionsZ;
            SectionPrerenderDesc[] allSecs = data.SectionDescs;

            bool hasLeft  = sx > 0;              ref SectionPrerenderDesc leftSec  = ref hasLeft  ? ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasRight = sx + 1 < sxCount;    ref SectionPrerenderDesc rightSec = ref hasRight ? ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasDown  = sy > 0;              ref SectionPrerenderDesc downSec  = ref hasDown  ? ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)]   : ref desc;
            bool hasUp    = sy + 1 < syCount;    ref SectionPrerenderDesc upSec    = ref hasUp    ? ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)]   : ref desc;
            bool hasBack  = sz > 0;              ref SectionPrerenderDesc backSec  = ref hasBack  ? ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)]   : ref desc;
            bool hasFront = sz + 1 < szCount;    ref SectionPrerenderDesc frontSec = ref hasFront ? ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)]   : ref desc;

            // Boundary face bitsets built during finalization (bit per boundary voxel that is occupied)
            ulong[] faceNegX = desc.FaceNegXBits; ulong[] facePosX = desc.FacePosXBits;
            ulong[] faceNegY = desc.FaceNegYBits; ulong[] facePosY = desc.FacePosYBits;
            ulong[] faceNegZ = desc.FaceNegZBits; ulong[] facePosZ = desc.FacePosZBits;

            // Working face masks (internal + boundary) for each direction
            Span<ulong> shift  = stackalloc ulong[64]; // temp shift buffer reused per direction
            Span<ulong> faceNX = stackalloc ulong[64]; // -X faces
            Span<ulong> facePX = stackalloc ulong[64]; // +X faces
            Span<ulong> faceNY = stackalloc ulong[64]; // -Y faces
            Span<ulong> facePY = stackalloc ulong[64]; // +Y faces
            Span<ulong> faceNZ = stackalloc ulong[64]; // -Z faces
            Span<ulong> facePZ = stackalloc ulong[64]; // +Z faces

            // strides in linear-index space for +X / +Y / +Z neighbors
            const int strideX = 16;
            const int strideY = 1;
            const int strideZ = 256; // 16 * 16

            // Internal faces: occupancy AND NOT(shifted occupancy) excluding boundary layer bits.
            BitsetShiftLeft (occ, strideX, shift); for (int i = 0; i < 64; i++) faceNX[i] = (occ[i] & ~_maskX0 [i]) & ~shift[i];
            BitsetShiftRight(occ, strideX, shift); for (int i = 0; i < 64; i++) facePX[i] = (occ[i] & ~_maskX15[i]) & ~shift[i];
            BitsetShiftLeft(occ, strideY, shift); for (int i = 0; i < 64; i++) faceNY[i] = (occ[i] & ~_maskY0 [i]) & ~shift[i];
            BitsetShiftRight(occ, strideY, shift); for (int i = 0; i < 64; i++) facePY[i] = (occ[i] & ~_maskY15[i]) & ~shift[i];
            BitsetShiftLeft(occ, strideZ, shift); for (int i = 0; i < 64; i++) faceNZ[i] = (occ[i] & ~_maskZ0 [i]) & ~shift[i];
            BitsetShiftRight(occ, strideZ, shift); for (int i = 0; i < 64; i++) facePZ[i] = (occ[i] & ~_maskZ15[i]) & ~shift[i];

            // Neighbor chunk boundary planes (bit per world boundary cell) used for world-edge occlusion
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;

            // --- Add boundary faces (only those visible w.r.t outside world / neighbor sections) ---

            // LEFT boundary (local x==0)
            if (lxMin == 0 && faceNegX != null)
            {
                int worldX = baseX;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = z * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((faceNegX[w] & (1UL << b)) == 0) continue; // no voxel
                        bool hidden = false;
                        if (worldX == 0)
                        {
                            // Chunk outer boundary -> consult neighbor plane bits
                            hidden = PlaneBit(planeNegX, (baseZ + z) * maxY + (baseY + y));
                        }
                        else if (hasLeft && NeighborBoundarySolid(ref leftSec, 0, 15, y, z))
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16) + 0) * 16 + y; // linear index of voxel at x=0
                            faceNX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // RIGHT boundary (local x==15)
            if (lxMax == S - 1 && facePosX != null)
            {
                int worldXRight = baseX + 15;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = z * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((facePosX[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (worldXRight == maxX - 1)
                        {
                            hidden = PlaneBit(planePosX, (baseZ + z) * maxY + (baseY + y));
                        }
                        else if (hasRight && NeighborBoundarySolid(ref rightSec, 1, 0, y, z))
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16 + 15) * 16) + y; // x=15
                            facePX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // BOTTOM boundary (y==0)
            if (lyMin == 0 && faceNegY != null)
            {
                int worldY = baseY;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int idx = x * 16 + z; int w = idx >> 6; int b = idx & 63;
                        if ((faceNegY[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (worldY == 0)
                        {
                            hidden = PlaneBit(planeNegY, (baseX + x) * maxZ + (baseZ + z));
                        }
                        else if (hasDown && NeighborBoundarySolid(ref downSec, 2, x, 15, z))
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 0; // y=0
                            faceNY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // TOP boundary (y==15)
            if (lyMax == S - 1 && facePosY != null)
            {
                int worldYTop = baseY + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int idx = x * 16 + z; int w = idx >> 6; int b = idx & 63;
                        if ((facePosY[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (worldYTop == maxY - 1)
                        {
                            hidden = PlaneBit(planePosY, (baseX + x) * maxZ + (baseZ + z));
                        }
                        else if (hasUp && NeighborBoundarySolid(ref upSec, 3, x, 0, z))
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((z * 16 + x) * 16) + 15; // y=15
                            facePY[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // BACK boundary (z==0)
            if (lzMin == 0 && faceNegZ != null)
            {
                int worldZ = baseZ;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = x * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((faceNegZ[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (worldZ == 0)
                        {
                            hidden = PlaneBit(planeNegZ, (baseX + x) * maxY + (baseY + y));
                        }
                        else if (hasBack && NeighborBoundarySolid(ref backSec, 4, x, y, 15))
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((0 * 16 + x) * 16) + y; // z=0
                            faceNZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // FRONT boundary (z==15)
            if (lzMax == S - 1 && facePosZ != null)
            {
                int worldZFront = baseZ + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int idx = x * 16 + y; int w = idx >> 6; int b = idx & 63;
                        if ((facePosZ[w] & (1UL << b)) == 0) continue;
                        bool hidden = false;
                        if (worldZFront == maxZ - 1)
                        {
                            hidden = PlaneBit(planePosZ, (baseX + x) * maxY + (baseY + y));
                        }
                        else if (hasFront && NeighborBoundarySolid(ref frontSec, 5, x, y, 0))
                        {
                            hidden = true;
                        }
                        if (!hidden)
                        {
                            int li = ((15 * 16 + x) * 16) + y; // z=15
                            facePZ[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // Tile index cache: key = (blockId << 3) | faceDir  (faceDir 0..5) -> tile atlas index.
            // 2048 entries supports blockIds up to 256 safely. Overflow uses bucket 0.
            uint[] tileCache = new uint[2048];
            for (int i = 0; i < tileCache.Length; i++) tileCache[i] = 0xFFFFFFFFu;

            // Decode block id from packed representation at local coords.
            uint GetTileIndex(ushort block, byte faceDir)
            {
                int key = (block << 3) | faceDir;
                if ((uint)key >= tileCache.Length) key = 0; // clamp / fallback
                uint cached = tileCache[key];
                if (cached != 0xFFFFFFFFu) return cached;

                var uvFace = atlas.GetBlockUVs(block, (Faces)faceDir);
                byte minTileX = 255, minTileY = 255;
                for (int i = 0; i < 4; i++)
                {
                    if (uvFace[i].x < minTileX) minTileX = uvFace[i].x;
                    if (uvFace[i].y < minTileY) minTileY = uvFace[i].y;
                }
                cached = (uint)(minTileY * atlas.tilesX + minTileX);
                tileCache[key] = cached;
                return cached;
            }

            // Local copy of descriptor.
            var localDesc = desc;

            // Emit all faces represented by bits inside a given directional mask.
            void EmitMask(Span<ulong> mask, byte faceDir)
            {
                for (int wi = 0; wi < 64; wi++)
                {
                    ulong word = mask[wi];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1; // clear lowest set bit
                        int li = (wi << 6) + bit; // voxel linear index

                        // Fast decode local coords from prepared tables
                        int ly = _lyFromLi[li];
                        int t = li >> 4;
                        int lx = t & 15;
                        int lz = t >> 4;

                        // Skip outside tight bounds
                        if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;

                        ushort block = DecodePacked(ref localDesc, lx, ly, lz);
                        if (block == 0) continue; // safety (should not happen because occupancy bit guaranteed solid)

                        // Emit instance
                        offsetList.Add((byte)(baseX + lx));
                        offsetList.Add((byte)(baseY + ly));
                        offsetList.Add((byte)(baseZ + lz));
                        tileIndexList.Add(GetTileIndex(block, faceDir));
                        faceDirList.Add(faceDir);
                    }
                }
            }

            // Emit in canonical direction order matching single packed path
            EmitMask(faceNX, 0); // LEFT (-X)
            EmitMask(facePX, 1); // RIGHT
            EmitMask(faceNY, 2); // BOTTOM (-Y)
            EmitMask(facePY, 3); // TOP
            EmitMask(faceNZ, 4); // BACK (-Z)
            EmitMask(facePZ, 5); // FRONT

            return true; // handled
        }
    }
}

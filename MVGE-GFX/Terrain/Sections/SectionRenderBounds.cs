using MVGE_GFX.Models;
using MVGE_INF.Models.Generation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using MVGE_INF.Loaders; // for TerrainLoader.IsOpaque

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {

        // Unified face descriptor used by both uniform emission and boundary reinsertion paths.
        // Axis: 0=X,1=Y,2=Z. Negative indicates -axis face. (Dx,Dy,Dz) point to neighbor section offset.
        private readonly struct FaceDescriptor
        {
            public readonly int FaceDir;
            public readonly sbyte Dx;
            public readonly sbyte Dy;
            public readonly sbyte Dz;
            public readonly int Axis;
            public readonly bool Negative;

            public FaceDescriptor(int faceDir, sbyte dx, sbyte dy, sbyte dz, int axis, bool negative)
            {
                FaceDir = faceDir; Dx = dx; Dy = dy; Dz = dz; Axis = axis; Negative = negative;
            }

            public int FixedLocal => Negative ? 0 : 15; // local coordinate (0 or 15) along Axis
        }

        // Order matches Faces enum: LEFT, RIGHT, BOTTOM, TOP, BACK, FRONT
        private static readonly FaceDescriptor[] _faces = new FaceDescriptor[]
        {
            new FaceDescriptor(0, -1,  0,  0, 0, true),   // -X
            new FaceDescriptor(1,  1,  0,  0, 0, false),  // +X
            new FaceDescriptor(2,  0, -1,  0, 1, true),   // -Y
            new FaceDescriptor(3,  0,  1,  0, 1, false),  // +Y
            new FaceDescriptor(4,  0,  0, -1, 2, true),   // -Z
            new FaceDescriptor(5,  0,  0,  1, 2, false),  // +Z
        };

        // Local packed (multi-id) decode (reused by MultiPacked + helpers)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort DecodePackedLocal(ref SectionPrerenderDesc d, int lx, int ly, int lz)
        {
            if (d.PackedBitData == null || d.Palette == null || d.BitsPerIndex <= 0) return 0;
            int li = ((lz * 16 + lx) << 4) + ly;
            int bpi = d.BitsPerIndex;
            long bitPos = (long)li * bpi;
            int word = (int)(bitPos >> 5);
            int bitOffset = (int)(bitPos & 31);
            uint value = d.PackedBitData[word] >> bitOffset;
            int rem = 32 - bitOffset;
            if (rem < bpi) value |= d.PackedBitData[word + 1] << rem;
            int mask = (1 << bpi) - 1;
            int pi = (int)(value & mask);
            if ((uint)pi >= (uint)d.Palette.Count) return 0;
            return d.Palette[pi];
        }

        // Unified neighbor voxel decode with occupancy flag (uniform / expanded / packed / multi-packed)
        // occupied == true when id != 0 and corresponding storage bit/entry is set.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort DecodeNeighborVoxel(ref SectionPrerenderDesc n, int lx, int ly, int lz, out bool occupied)
        {
            occupied = false;
            switch (n.Kind)
            {
                case 1: // Uniform
                {
                    ushort id = n.UniformBlockId;
                    occupied = id != 0;
                    return id;
                }
                case 3: // Expanded (dense array)
                {
                    if (n.ExpandedDense == null) return 0;
                    int li = ((lz * 16 + lx) * 16) + ly;
                    ushort id = n.ExpandedDense[li];
                    occupied = id != 0;
                    return id;
                }
                case 4: // Single packed (palette[1] holds the single non-air id).Test uses opaque OR transparent bits.
                {
                    if (n.Palette == null || n.Palette.Count <= 1) return 0;
                    int li = ((lz * 16 + lx) * 16) + ly;
                    bool bitSetOpaque = n.OpaqueBits != null && (n.OpaqueBits[li >> 6] & (1UL << (li & 63))) != 0UL;
                    bool bitSetTransparent = n.TransparentBits != null && (n.TransparentBits[li >> 6] & (1UL << (li & 63))) != 0UL;
                    bool occ = bitSetOpaque || bitSetTransparent;
                    if (!occ)
                    {
                        occupied = false;
                        return 0;
                    }
                    occupied = true;
                    return n.Palette[1];
                }
                case 5: // Multi-packed
                {
                    ushort id = DecodePackedLocal(ref n, lx, ly, lz);
                    occupied = id != 0;
                    return id;
                }
                default:
                    return 0;
            }
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
        // NOTE: occ now indicates only opaque voxel occupancy; transparent blocks are excluded upstream so internal mask
        // generation inherently ignores them without extra filtering here.
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
        // World plane bitsets and neighbor face bitsets are opaque-only: a set bit = opaque voxel present.
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
            // Existing explicit loops retained for clarity and as baseline path; generalized variant added below for selective & packed path usage.
            // Neighbor descriptors (bounded fetch)
            bool hasLeft  = sx > 0;                ref SectionPrerenderDesc leftSec  = ref hasLeft  ? ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasRight = sx + 1 < sxCount;      ref SectionPrerenderDesc rightSec = ref hasRight ? ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasDown  = sy > 0;                ref SectionPrerenderDesc downSec  = ref hasDown  ? ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)] : ref desc;
            bool hasUp    = sy + 1 < syCount;      ref SectionPrerenderDesc upSec    = ref hasUp    ? ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)] : ref desc;
            bool hasBack  = sz > 0;                ref SectionPrerenderDesc backSec  = ref hasBack  ? ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)] : ref desc;
            bool hasFront = sz + 1 < szCount;      ref SectionPrerenderDesc frontSec = ref hasFront ? ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)] : ref desc;

            // World boundary plane bitsets (holes suppress faces at world edge). Set bit == opaque neighbor voxel.
            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // LEFT boundary (x=0)
            if (lxMin == 0 && desc.FaceNegXBits != null)
            {
                int worldX = baseX;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int planeIndex = z * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        ulong maskBit = 1UL << b;
                        if ((desc.FaceNegXBits[w] & maskBit) == 0) continue; // boundary voxel not present (opaque)

                        bool hidden = false;
                        if (worldX == 0)
                        {
                            // World -X edge: consult world plane bitset (set bit means opaque neighbor outside chunk)
                            int planeBitIndex = (baseZ + z) * maxY + (baseY + y);
                            hidden = PlaneBit(planeNegX, planeBitIndex);
                        }
                        else if (hasLeft && NeighborBoundarySolid(ref leftSec, 0, 15, y, z))
                        {
                            hidden = true; // Occluded by neighbor +X boundary opaque voxel
                        }

                        if (!hidden)
                        {
                            int li = ((z * 16) + 0) * 16 + y; // local voxel linear index at x=0
                            faceNX[li >> 6] |= 1UL << (li & 63);
                        }
                    }
                }
            }

            // RIGHT boundary (x=15)
            if (lxMax == 15 && desc.FacePosXBits != null)
            {
                int worldXRight = baseX + 15;
                for (int z = lzMin; z <= lzMax; z++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int planeIndex = z * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        ulong maskBit = 1UL << b;
                        if ((desc.FacePosXBits[w] & maskBit) == 0) continue;

                        bool hidden = false;
                        if (worldXRight == maxX - 1)
                        {
                            int planeBitIndex = (baseZ + z) * maxY + (baseY + y);
                            hidden = PlaneBit(planePosX, planeBitIndex);
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

            // BOTTOM boundary (y=0)
            if (lyMin == 0 && desc.FaceNegYBits != null)
            {
                int worldY = baseY;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int planeIndex = x * 16 + z;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        ulong maskBit = 1UL << b;
                        if ((desc.FaceNegYBits[w] & maskBit) == 0) continue;

                        bool hidden = false;
                        if (worldY == 0)
                        {
                            int planeBitIndex = (baseX + x) * maxZ + (baseZ + z);
                            hidden = PlaneBit(planeNegY, planeBitIndex);
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

            // TOP boundary (y=15)
            if (lyMax == 15 && desc.FacePosYBits != null)
            {
                int worldYTop = baseY + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int z = lzMin; z <= lzMax; z++)
                    {
                        int planeIndex = x * 16 + z;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        ulong maskBit = 1UL << b;
                        if ((desc.FacePosYBits[w] & maskBit) == 0) continue;

                        bool hidden = false;
                        if (worldYTop == maxY - 1)
                        {
                            int planeBitIndex = (baseX + x) * maxZ + (baseZ + z);
                            hidden = PlaneBit(planePosY, planeBitIndex);
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

            // BACK boundary (z=0)
            if (lzMin == 0 && desc.FaceNegZBits != null)
            {
                int worldZ = baseZ;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int planeIndex = x * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        ulong maskBit = 1UL << b;
                        if ((desc.FaceNegZBits[w] & maskBit) == 0) continue;

                        bool hidden = false;
                        if (worldZ == 0)
                        {
                            int planeBitIndex = (baseX + x) * maxY + (baseY + y);
                            hidden = PlaneBit(planeNegZ, planeBitIndex);
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

            // FRONT boundary (z=15)
            if (lzMax == 15 && desc.FacePosZBits != null)
            {
                int worldZFront = baseZ + 15;
                for (int x = lxMin; x <= lxMax; x++)
                {
                    for (int y = lyMin; y <= lyMax; y++)
                    {
                        int planeIndex = x * 16 + y;
                        int w = planeIndex >> 6;
                        int b = planeIndex & 63;
                        ulong maskBit = 1UL << b;
                        if ((desc.FacePosZBits[w] & maskBit) == 0) continue;

                        bool hidden = false;
                        if (worldZFront == maxZ - 1)
                        {
                            int planeBitIndex = (baseX + x) * maxY + (baseY + y);
                            hidden = PlaneBit(planePosZ, planeBitIndex);
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
        }

        // Build internal transparent face masks then cull adjacency to opaque.
        private static void BuildTransparentInternalFaceMasks(
            ReadOnlySpan<ulong> transparentOcc,
            ReadOnlySpan<ulong> opaqueOcc,
            Span<ulong> faceNX, Span<ulong> facePX,
            Span<ulong> faceNY, Span<ulong> facePY,
            Span<ulong> faceNZ, Span<ulong> facePZ)
        {
            BuildInternalFaceMasks(transparentOcc, faceNX, facePX, faceNY, facePY, faceNZ, facePZ);
            Span<ulong> shift = stackalloc ulong[64];

            // For each direction, construct mask of faces whose neighbor is opaque, then subtract.
            // -X faces hidden if +X neighbor opaque
            BitsetShiftRight(opaqueOcc, STRIDE_X, shift);
            for (int i = 0; i < 64; i++) faceNX[i] &= ~shift[i];
            // +X faces hidden if -X neighbor opaque
            BitsetShiftLeft(opaqueOcc, STRIDE_X, shift);
            for (int i = 0; i < 64; i++) facePX[i] &= ~shift[i];
            // -Y
            BitsetShiftRight(opaqueOcc, STRIDE_Y, shift);
            for (int i = 0; i < 64; i++) faceNY[i] &= ~shift[i];
            // +Y
            BitsetShiftLeft(opaqueOcc, STRIDE_Y, shift);
            for (int i = 0; i < 64; i++) facePY[i] &= ~shift[i];
            // -Z
            BitsetShiftRight(opaqueOcc, STRIDE_Z, shift);
            for (int i = 0; i < 64; i++) faceNZ[i] &= ~shift[i];
            // +Z
            BitsetShiftLeft(opaqueOcc, STRIDE_Z, shift);
            for (int i = 0; i < 64; i++) facePZ[i] &= ~shift[i];
        }

        // Generalized boundary reinsertion (metadata-driven) with optional skip flags (used by packed selective path).
        // Preserves original face visibility semantics while reducing branch duplication.
        // World plane & neighbor face bit semantics: bit set == opaque voxel present; transparent blocks are treated as holes.
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
            // Delegate to explicit implementation when no skips to retain maximum clarity and avoid extra overhead.
            if (!skipDir[0] && !skipDir[1] && !skipDir[2] && !skipDir[3] && !skipDir[4] && !skipDir[5])
            {
                AddVisibleBoundaryFaces(ref desc, baseX, baseY, baseZ, lxMin, lxMax, lyMin, lyMax, lzMin, lzMax,
                                        allSecs, sx, sy, sz, sxCount, syCount, szCount,
                                        faceNX, facePX, faceNY, facePY, faceNZ, facePZ, data);
                return;
            }

            var planeNegX = data.NeighborPlaneNegX; var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; var planePosZ = data.NeighborPlanePosZ;
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

            // Neighbor refs
            bool hasLeft = sx > 0; ref SectionPrerenderDesc leftSec = ref hasLeft ? ref allSecs[SecIndex(sx - 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasRight = sx + 1 < sxCount; ref SectionPrerenderDesc rightSec = ref hasRight ? ref allSecs[SecIndex(sx + 1, sy, sz, syCount, szCount)] : ref desc;
            bool hasDown = sy > 0; ref SectionPrerenderDesc downSec = ref hasDown ? ref allSecs[SecIndex(sx, sy - 1, sz, syCount, szCount)] : ref desc;
            bool hasUp = sy + 1 < syCount; ref SectionPrerenderDesc upSec = ref hasUp ? ref allSecs[SecIndex(sx, sy + 1, sz, syCount, szCount)] : ref desc;
            bool hasBack = sz > 0; ref SectionPrerenderDesc backSec = ref hasBack ? ref allSecs[SecIndex(sx, sy, sz - 1, syCount, szCount)] : ref desc;
            bool hasFront = sz + 1 < szCount; ref SectionPrerenderDesc frontSec = ref hasFront ? ref allSecs[SecIndex(sx, sy, sz + 1, syCount, szCount)] : ref desc;

            for (int i = 0; i < _faces.Length; i++)
            {
                ref readonly var meta = ref _faces[i];
                if (skipDir[meta.FaceDir]) continue; // skip fully occluded face
                // Check bounds gating for this axis
                if (meta.Axis == 0)
                {
                    if (meta.Negative && lxMin != 0) continue; if (!meta.Negative && lxMax != 15) continue;
                }
                else if (meta.Axis == 1)
                {
                    if (meta.Negative && lyMin != 0) continue; if (!meta.Negative && lyMax != 15) continue;
                }
                else
                {
                    if (meta.Negative && lzMin != 0) continue; if (!meta.Negative && lzMax != 15) continue;
                }

                ulong[] faceBits = meta.FaceDir switch
                {
                    0 => desc.FaceNegXBits,
                    1 => desc.FacePosXBits,
                    2 => desc.FaceNegYBits,
                    3 => desc.FacePosYBits,
                    4 => desc.FaceNegZBits,
                    5 => desc.FacePosZBits,
                    _ => null
                };
                if (faceBits == null) continue; // null means no opaque voxels on that boundary

                // World boundary plane & neighbor selection
                ulong[] plane = null; bool hasNeighbor = false; SectionPrerenderDesc neighbor = default;
                switch (meta.FaceDir)
                {
                    case 0: plane = baseX == 0 ? planeNegX : null; hasNeighbor = hasLeft; neighbor = hasLeft ? leftSec : desc; break;
                    case 1: plane = (baseX + 15) == maxX - 1 ? planePosX : null; hasNeighbor = hasRight; neighbor = hasRight ? rightSec : desc; break;
                    case 2: plane = baseY == 0 ? planeNegY : null; hasNeighbor = hasDown; neighbor = hasDown ? downSec : desc; break;
                    case 3: plane = (baseY + 15) == maxY - 1 ? planePosY : null; hasNeighbor = hasUp; neighbor = hasUp ? upSec : desc; break;
                    case 4: plane = baseZ == 0 ? planeNegZ : null; hasNeighbor = hasBack; neighbor = hasBack ? backSec : desc; break;
                    case 5: plane = (baseZ + 15) == maxZ - 1 ? planePosZ : null; hasNeighbor = hasFront; neighbor = hasFront ? frontSec : desc; break;
                }

                // Iterate plane indices according to face orientation (faceBits store opaque voxel positions)
                if (meta.FaceDir == 0 || meta.FaceDir == 1) // X faces iterate z,y
                {
                    for (int z = lzMin; z <= lzMax; z++)
                        for (int y = lyMin; y <= lyMax; y++)
                        {
                            int idx = z * 16 + y; int w = idx >> 6; int b = idx & 63; if ((faceBits[w] & (1UL << b)) == 0) continue;
                            bool hidden = false;
                            if (plane != null)
                                hidden = PlaneBit(plane, (baseZ + z) * maxY + (baseY + y));
                            else if (hasNeighbor)
                                hidden = NeighborBoundarySolid(ref neighbor, meta.FaceDir == 0 ? 0 : 1, meta.FaceDir == 0 ? 15 : 0, y, z);
                            if (!hidden)
                            {
                                int li = ((z * 16 + (meta.FaceDir == 0 ? 0 : 15)) * 16) + y;
                                (meta.FaceDir == 0 ? faceNX : facePX)[li >> 6] |= 1UL << (li & 63);
                            }
                        }
                }
                else if (meta.FaceDir == 2 || meta.FaceDir == 3) // Y faces iterate x,z
                {
                    for (int x = lxMin; x <= lxMax; x++)
                        for (int z = lzMin; z <= lzMax; z++)
                        {
                            int idx = x * 16 + z; int w = idx >> 6; int b = idx & 63; if ((faceBits[w] & (1UL << b)) == 0) continue;
                            bool hidden = false;
                            if (plane != null)
                                hidden = PlaneBit(plane, (baseX + x) * maxZ + (baseZ + z));
                            else if (hasNeighbor)
                                hidden = NeighborBoundarySolid(ref neighbor, meta.FaceDir == 2 ? 2 : 3, x, meta.FaceDir == 2 ? 15 : 0, z);
                            if (!hidden)
                            {
                                int li = ((z * 16 + x) * 16) + (meta.FaceDir == 2 ? 0 : 15);
                                (meta.FaceDir == 2 ? faceNY : facePY)[li >> 6] |= 1UL << (li & 63);
                            }
                        }
                }
                else // Z faces iterate x,y
                {
                    for (int x = lxMin; x <= lxMax; x++)
                        for (int y = lyMin; y <= lyMax; y++)
                        {
                            int idx = x * 16 + y; int w = idx >> 6; int b = idx & 63; if ((faceBits[w] & (1UL << b)) == 0) continue;
                            bool hidden = false;
                            if (plane != null)
                                hidden = PlaneBit(plane, (baseX + x) * maxY + (baseY + y));
                            else if (hasNeighbor)
                                hidden = NeighborBoundarySolid(ref neighbor, meta.FaceDir == 4 ? 4 : 5, x, y, meta.FaceDir == 4 ? 15 : 0);
                            if (!hidden)
                            {
                                int li = (((meta.FaceDir == 4 ? 0 : 15) * 16 + x) * 16) + y;
                                (meta.FaceDir == 4 ? faceNZ : facePZ)[li >> 6] |= 1UL << (li & 63);
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

        // Fallback solid test against arbitrary neighbor descriptor (shared across paths).
        // Returns true only for opaque voxels (uniform transparent blocks excluded).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool NeighborVoxelSolid(ref SectionPrerenderDesc n, int lx, int ly, int lz)
        {
            if (n.Kind == 0 || n.OpaqueCount == 0) return false; // empty or no opaque voxels
            switch (n.Kind)
            {
                case 1: // Uniform
                    return TerrainLoader.IsOpaque(n.UniformBlockId);
                case 3: // DenseExpanded
                    if (n.ExpandedDense != null)
                    {
                        int liD = ((lz * 16 + lx) * 16) + ly;
                        ushort id = n.ExpandedDense[liD];
                        return id != 0 && TerrainLoader.IsOpaque(id);
                    }
                    return false;
                case 4: // Packed
                case 5: // MultiPacked
                    if (n.OpaqueBits != null)
                    {
                        int li = ((lz * 16 + lx) * 16) + ly;
                        return (n.OpaqueBits[li >> 6] & (1UL << (li & 63))) != 0UL;
                    }
                    return false;
                default:
                    return false;
            }
        }

        // Neighbor boundary probe using its precomputed face bitsets (with per-voxel fallback).
        // faceDir: 0..5 (-X,+X,-Y,+Y,-Z,+Z)  (bit set == opaque voxel)
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

        // Helper: treat neighbor as fully solid only if every voxel is opaque. Uniform transparent sections are NOT treated as solid.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool NeighborFullySolid(ref SectionPrerenderDesc n)
        {
            if (n.OpaqueCount != 4096) return false;
            if (n.Kind == 1) return TerrainLoader.IsOpaque(n.UniformBlockId);
            if (n.Kind == 4 || n.Kind == 5 || n.Kind == 3) return true; // packed, multi-packed, dense (opaque occupancy fully filled)
            return false;
        }

        // Neighbor mask popcount (occluded cells) for predicted capacity. Guard length to plane size (<=256 bits).
        // Masks operate over opaque-only bits.
        int MaskOcclusionCount(ulong[] mask)
        {
            if (mask == null) return 0;
            int occluded = 0; int neededWords = 4; // 256 bits -> 4 * 64
            for (int i = 0; i < mask.Length && i < neededWords; i++) occluded += BitOperations.PopCount(mask[i]);
            return occluded;
        }

        // Helper for world-edge plane quick skip if fully occluded in the SxS window. Plane bits represent opaque voxels only.
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
                    if ((plane[w] & (1UL << b)) == 0UL) return false; // found a hole (non-opaque)
                    idx++;
                }
            }
            return true;
        }

        // Count visible cells for world boundary face (used for capacity prediction). Plane bits represent opaque voxels only.
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
                    if ((plane[w] & (1UL << bit)) == 0UL) visible++; // hole -> visible (transparent or air)
                }
            }
            return visible;
        }
    }
}

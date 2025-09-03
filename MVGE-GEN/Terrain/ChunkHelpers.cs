using MVGE_INF.Generation.Models;
using MVGE_INF.Loaders;
using MVGE_INF.Models.Generation;
using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        // Static cache: block id -> base block type (built on first use)
        private static Dictionary<ushort, BaseBlockType> _blockIdToBaseType;
        private static readonly object _baseTypeInitLock = new();

        // Cheaply set per-face solidity flags using cached boundary plane bitsets
        private static bool PlaneIsFull(ulong[] plane, int wordCount, ulong fullWord, ulong lastMask)
        {
            if (plane == null || wordCount == 0) return false;
            // ensure plane length is sufficient
            if (plane.Length < wordCount) return false;
            // all full words must be 0xFFFFFFFFFFFFFFFF
            for (int i = 0; i < wordCount - 1; i++)
            {
                if (plane[i] != fullWord) return false;
            }
            // last word uses a partial mask
            if (plane[wordCount - 1] != lastMask) return false;
            return true;
        }
        private void SetFaceSolidFromPlanes()
        {
            // YZ plane (Neg/Pos X): size = dimY * dimZ
            int yzBits = dimY * dimZ;
            int yzWC = (yzBits + 63) >> 6;
            ulong fullWord = ~0UL;
            int remYZ = yzBits & 63;
            ulong lastYZ = remYZ == 0 ? fullWord : ((1UL << remYZ) - 1);
            FaceSolidNegX = PlaneIsFull(PlaneNegX, yzWC, fullWord, lastYZ);
            FaceSolidPosX = PlaneIsFull(PlanePosX, yzWC, fullWord, lastYZ);

            // XZ plane (Neg/Pos Y): size = dimX * dimZ
            int xzBits = dimX * dimZ;
            int xzWC = (xzBits + 63) >> 6;
            int remXZ = xzBits & 63;
            ulong lastXZ = remXZ == 0 ? fullWord : ((1UL << remXZ) - 1);
            FaceSolidNegY = PlaneIsFull(PlaneNegY, xzWC, fullWord, lastXZ);
            FaceSolidPosY = PlaneIsFull(PlanePosY, xzWC, fullWord, lastXZ);

            // XY plane (Neg/Pos Z): size = dimX * dimY
            int xyBits = dimX * dimY;
            int xyWC = (xyBits + 63) >> 6;
            int remXY = xyBits & 63;
            ulong lastXY = remXY == 0 ? fullWord : ((1UL << remXY) - 1);
            FaceSolidNegZ = PlaneIsFull(PlaneNegZ, xyWC, fullWord, lastXY);
            FaceSolidPosZ = PlaneIsFull(PlanePosZ, xyWC, fullWord, lastXY);
        }

        private void BuildAllBoundaryPlanesInitial()
        {
            EnsurePlaneArrays();
            Array.Clear(PlaneNegX);
            Array.Clear(PlanePosX);
            Array.Clear(PlaneNegY);
            Array.Clear(PlanePosY);
            Array.Clear(PlaneNegZ);
            Array.Clear(PlanePosZ);

            const int S = ChunkSection.SECTION_SIZE;

            // Helpers: set one bit in a plane
            static void SetPlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return;
                int w = index >> 6;
                int b = index & 63;
                plane[w] |= 1UL << b;
            }

            // --- -X / +X (YZ planes) ---
            if (sectionsX > 0)
            {
                int sxNeg = 0;
                int sxPos = sectionsX - 1;

                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        // Neg X: section local face at x=0 contributes to global plane x==0
                        var secNeg = sections[sxNeg, sy, sz];
                        if (secNeg != null)
                        {
                            // Uniform sections intentionally leave Face*Bits null but imply fully solid face
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localZ = 0; localZ < S; localZ++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlaneNegX, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegXBits != null)
                            {
                                var bits = secNeg.FaceNegXBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localZ = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlaneNegX, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }

                        }

                        // Pos X: section local face at x=15 contributes to global plane x==dimX-1
                        var secPos = sections[sxPos, sy, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localZ = 0; localZ < S; localZ++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlanePosX, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosXBits != null)
                            {
                                var bits = secPos.FacePosXBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localZ = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlanePosX, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // --- -Y / +Y (XZ planes) ---
            if (sectionsY > 0)
            {
                int syNeg = 0;
                int syPos = sectionsY - 1;

                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var secNeg = sections[sx, syNeg, sz];
                        if (secNeg != null)
                        {
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlaneNegY, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegYBits != null)
                            {
                                var bits = secNeg.FaceNegYBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localZ = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlaneNegY, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }

                        var secPos = sections[sx, syPos, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlanePosY, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosYBits != null)
                            {
                                var bits = secPos.FacePosYBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localZ = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlanePosY, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // --- -Z / +Z (XY planes) ---
            if (sectionsZ > 0)
            {
                int szNeg = 0;
                int szPos = sectionsZ - 1;

                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sy = 0; sy < sectionsY; sy++)
                    {
                        var secNeg = sections[sx, sy, szNeg];
                        if (secNeg != null)
                        {
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlaneNegZ, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegZBits != null)
                            {
                                var bits = secNeg.FaceNegZBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlaneNegZ, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }

                        var secPos = sections[sx, sy, szPos];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlanePosZ, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosZBits != null)
                            {
                                var bits = secPos.FacePosZBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlanePosZ, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Derive per-face solidity flags from planes
            SetFaceSolidFromPlanes();
        }

        private void CreateUniformSections(ushort blockId)
        {
            int S = ChunkSection.SECTION_SIZE;
            int voxelsPerSection = S * S * S;
            for (int sx = 0; sx < sectionsX; sx++)
                for (int sy = 0; sy < sectionsY; sy++)
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = new ChunkSection
                        {
                            IsAllAir = false,
                            Kind = ChunkSection.RepresentationKind.Uniform,
                            UniformBlockId = blockId,
                            NonAirCount = voxelsPerSection,
                            VoxelCount = voxelsPerSection,
                            CompletelyFull = true,
                            MetadataBuilt = true,
                            HasBounds = true,
                            MinLX = 0,
                            MinLY = 0,
                            MinLZ = 0,
                            MaxLX = 15,
                            MaxLY = 15,
                            MaxLZ = 15
                        };
                        sections[sx, sy, sz] = sec;
                    }
        }
    }
}

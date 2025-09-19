using MVGE_INF.Generation.Models;
using MVGE_INF.Loaders;
using MVGE_INF.Models.Generation;
using MVGE_INF.Models.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVGE_GEN.Utils;

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

            // Reset transparent id boundary maps (only allocate on demand below)
            TransparentPlaneNegX = null;
            TransparentPlanePosX = null;
            TransparentPlaneNegY = null;
            TransparentPlanePosY = null;
            TransparentPlaneNegZ = null;
            TransparentPlanePosZ = null;

            const int S = Section.SECTION_SIZE;

            // Helpers: set one bit in a plane
            static void SetPlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return;
                int w = index >> 6;
                int b = index & 63;
                plane[w] |= 1UL << b;
            }

            // Local helper to lazily ensure transparent boundary id arrays
            void EnsureTransparentArrays()
            {
                if (TransparentPlaneNegX == null) TransparentPlaneNegX = new ushort[dimY * dimZ];
                if (TransparentPlanePosX == null) TransparentPlanePosX = new ushort[dimY * dimZ];
                if (TransparentPlaneNegY == null) TransparentPlaneNegY = new ushort[dimX * dimZ];
                if (TransparentPlanePosY == null) TransparentPlanePosY = new ushort[dimX * dimZ];
                if (TransparentPlaneNegZ == null) TransparentPlaneNegZ = new ushort[dimX * dimY];
                if (TransparentPlanePosZ == null) TransparentPlanePosZ = new ushort[dimX * dimY];
            }

            // Helper to record a transparent id into a boundary map (0 retains existing value, last writer wins otherwise)
            static void RecordTransparent(ushort[] arr, int index, ushort id)
            {
                if (arr == null || id == 0) return;
                arr[index] = id; // store id directly
            }

            // Decode a voxel id at (lx,ly,lz) for any section representation
            static ushort GetSectionVoxelId(Section sec, int lx, int ly, int lz)
            {
                if (sec == null) return 0;
                switch (sec.Kind)
                {
                    case Section.RepresentationKind.Empty:
                        return 0;
                    case Section.RepresentationKind.Uniform:
                        return sec.UniformBlockId;
                    case Section.RepresentationKind.Expanded:
                        {
                            var dense = sec.ExpandedDense;
                            if (dense == null) return 0;
                            int li = ((lz * 16 + lx) * 16) + ly;
                            return dense[li];
                        }
                    case Section.RepresentationKind.Packed:
                    case Section.RepresentationKind.MultiPacked:
                        {
                            var bits = sec.BitData;
                            var pal = sec.Palette;
                            int bpi = sec.BitsPerIndex;
                            if (bits == null || pal == null || pal.Count == 0 || bpi <= 0) return 0;
                            int li = ((lz * 16 + lx) * 16) + ly;
                            long bitPos = (long)li * bpi;
                            int w = (int)(bitPos >> 5);
                            int off = (int)(bitPos & 31);
                            if ((uint)w >= (uint)bits.Length) return 0;
                            uint v = bits[w] >> off;
                            int rem = 32 - off;
                            if (rem < bpi && w + 1 < bits.Length) v |= bits[w + 1] << rem;
                            int mask = (1 << bpi) - 1;
                            int pi = (int)(v & (uint)mask);
                            if ((uint)pi >= (uint)pal.Count) return 0;
                            return pal[pi];
                        }
                    default:
                        return 0;
                }
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
                            // Uniform opaque sections (non-air and IsOpaque) intentionally leave Face*Bits null but imply fully solid face
                            if (secNeg.Kind == Section.RepresentationKind.Uniform && secNeg.UniformBlockId != Section.AIR && TerrainLoader.IsOpaque(secNeg.UniformBlockId))
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
                            else
                            {
                                // Fallback: per-voxel decode for boundary occupancy (-X) to ensure correctness for all kinds
                                for (int localZ = 0; localZ < S; localZ++)
                                {
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        ushort id = GetSectionVoxelId(secNeg, 0, localY, localZ);
                                        if (id != 0 && TerrainLoader.IsOpaque(id))
                                        {
                                            int globalZ = sz * S + localZ;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalZ * dimY + globalY;
                                            SetPlaneBit(PlaneNegX, globalIdx);
                                        }
                                    }
                                }
                            }

                            // Transparent boundary ids on -X face (always derived for any representation)
                            {
                                // iterate voxels at x=0 for transparent ids
                                bool uniformTransparent = secNeg.Kind == Section.RepresentationKind.Uniform && secNeg.UniformBlockId != Section.AIR && !TerrainLoader.IsOpaque(secNeg.UniformBlockId);
                                if (uniformTransparent)
                                {
                                    EnsureTransparentArrays();
                                    for (int localZ = 0; localZ < S; localZ++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            int globalZ = sz * S + localZ;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalZ * dimY + globalY;
                                            RecordTransparent(TransparentPlaneNegX, globalIdx, secNeg.UniformBlockId);
                                        }
                                }
                                else if (secNeg?.TransparentFaceNegXBits != null)
                                {
                                    EnsureTransparentArrays();
                                    var tBits = secNeg.TransparentFaceNegXBits;
                                    for (int wi = 0; wi < tBits.Length; wi++)
                                    {
                                        ulong word = tBits[wi];
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

                                            ushort id = GetSectionVoxelId(secNeg, 0, localY, localZ); // x fixed at 0
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                                RecordTransparent(TransparentPlaneNegX, globalIdx, id);
                                            word &= word - 1;
                                        }
                                    }
                                }
                                else
                                {
                                    // Fallback: per-voxel decode boundary transparency when no mask exists
                                    for (int localZ = 0; localZ < S; localZ++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            ushort id = GetSectionVoxelId(secNeg, 0, localY, localZ);
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                            {
                                                EnsureTransparentArrays();
                                                int globalZ = sz * S + localZ;
                                                int globalY = sy * S + localY;
                                                int globalIdx = globalZ * dimY + globalY;
                                                RecordTransparent(TransparentPlaneNegX, globalIdx, id);
                                            }
                                        }
                                }
                            }
                        }

                        // Pos X: section local face at x=15 contributes to global plane x==dimX-1
                        var secPos = sections[sxPos, sy, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == Section.RepresentationKind.Uniform && secPos.UniformBlockId != Section.AIR && TerrainLoader.IsOpaque(secPos.UniformBlockId))
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
                            else
                            {
                                // Fallback: per-voxel decode for boundary occupancy (+X)
                                for (int localZ = 0; localZ < S; localZ++)
                                {
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        ushort id = GetSectionVoxelId(secPos, 15, localY, localZ);
                                        if (id != 0 && TerrainLoader.IsOpaque(id))
                                        {
                                            int globalZ = sz * S + localZ;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalZ * dimY + globalY;
                                            SetPlaneBit(PlanePosX, globalIdx);
                                        }
                                    }
                                }
                            }

                            // Transparent boundary ids on +X face
                            {
                                bool uniformTransparentPos = secPos.Kind == Section.RepresentationKind.Uniform && secPos.UniformBlockId != Section.AIR && !TerrainLoader.IsOpaque(secPos.UniformBlockId);
                                if (uniformTransparentPos)
                                {
                                    EnsureTransparentArrays();
                                    for (int localZ = 0; localZ < S; localZ++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            int globalZ = sz * S + localZ;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalZ * dimY + globalY;
                                            RecordTransparent(TransparentPlanePosX, globalIdx, secPos.UniformBlockId);
                                        }
                                }
                                else if (secPos.TransparentFacePosXBits != null)
                                {
                                    EnsureTransparentArrays();
                                    var tBits = secPos.TransparentFacePosXBits;
                                    for (int wi = 0; wi < tBits.Length; wi++)
                                    {
                                        ulong word = tBits[wi];
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

                                            ushort id = GetSectionVoxelId(secPos, 15, localY, localZ); // x fixed at 15
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                                RecordTransparent(TransparentPlanePosX, globalIdx, id);
                                            word &= word - 1;
                                        }
                                    }
                                }
                                else
                                {
                                    for (int localZ = 0; localZ < S; localZ++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            ushort id = GetSectionVoxelId(secPos, 15, localY, localZ);
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                            {
                                                EnsureTransparentArrays();
                                                int globalZ = sz * S + localZ;
                                                int globalY = sy * S + localY;
                                                int globalIdx = globalZ * dimY + globalY;
                                                RecordTransparent(TransparentPlanePosX, globalIdx, id);
                                            }
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
                            if (secNeg.Kind == Section.RepresentationKind.Uniform && secNeg.UniformBlockId != Section.AIR && TerrainLoader.IsOpaque(secNeg.UniformBlockId))
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
                            else
                            {
                                // Fallback: per-voxel decode for boundary occupancy (-Y)
                                for (int localX = 0; localX < S; localX++)
                                {
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        ushort id = GetSectionVoxelId(secNeg, localX, 0, localZ);
                                        if (id != 0 && TerrainLoader.IsOpaque(id))
                                        {
                                            int globalX = sx * S + localX;
                                            int globalZ = sz * S + localZ;
                                            int globalIdx = globalX * dimZ + globalZ;
                                            SetPlaneBit(PlaneNegY, globalIdx);
                                        }
                                    }
                                }
                            }

                            // Transparent -Y
                            {
                                bool uniformTransparentNegY = secNeg.Kind == Section.RepresentationKind.Uniform && secNeg.UniformBlockId != Section.AIR && !TerrainLoader.IsOpaque(secNeg.UniformBlockId);
                                if (uniformTransparentNegY)
                                {
                                    EnsureTransparentArrays();
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localZ = 0; localZ < S; localZ++)
                                        {
                                            int globalX = sx * S + localX;
                                            int globalZ = sz * S + localZ;
                                            int globalIdx = globalX * dimZ + globalZ;
                                            RecordTransparent(TransparentPlaneNegY, globalIdx, secNeg.UniformBlockId);
                                        }
                                }
                                else if (secNeg.TransparentFaceNegYBits != null)
                                {
                                    EnsureTransparentArrays();
                                    var tBits = secNeg.TransparentFaceNegYBits;
                                    for (int wi = 0; wi < tBits.Length; wi++)
                                    {
                                        ulong word = tBits[wi];
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

                                            ushort id = GetSectionVoxelId(secNeg, localX, 0, localZ); // y fixed at 0
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                                RecordTransparent(TransparentPlaneNegY, globalIdx, id);
                                            word &= word - 1;
                                        }
                                    }
                                }
                                else
                                {
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localZ = 0; localZ < S; localZ++)
                                        {
                                            ushort id = GetSectionVoxelId(secNeg, localX, 0, localZ);
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                            {
                                                EnsureTransparentArrays();
                                                int globalX = sx * S + localX;
                                                int globalZ = sz * S + localZ;
                                                int globalIdx = globalX * dimZ + globalZ;
                                                RecordTransparent(TransparentPlaneNegY, globalIdx, id);
                                            }
                                        }
                                }
                            }
                        }

                        var secPos = sections[sx, syPos, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == Section.RepresentationKind.Uniform && secPos.UniformBlockId != Section.AIR && TerrainLoader.IsOpaque(secPos.UniformBlockId))
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
                            else
                            {
                                // Fallback: per-voxel decode for boundary occupancy (+Y)
                                for (int localX = 0; localX < S; localX++)
                                {
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        ushort id = GetSectionVoxelId(secPos, localX, 15, localZ);
                                        if (id != 0 && TerrainLoader.IsOpaque(id))
                                        {
                                            int globalX = sx * S + localX;
                                            int globalZ = sz * S + localZ;
                                            int globalIdx = globalX * dimZ + globalZ;
                                            SetPlaneBit(PlanePosY, globalIdx);
                                        }
                                    }
                                }
                            }

                            // Transparent +Y
                            {
                                bool uniformTransparentPosY = secPos.Kind == Section.RepresentationKind.Uniform && secPos.UniformBlockId != Section.AIR && !TerrainLoader.IsOpaque(secPos.UniformBlockId);
                                if (uniformTransparentPosY)
                                {
                                    EnsureTransparentArrays();
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localZ = 0; localZ < S; localZ++)
                                        {
                                            int globalX = sx * S + localX;
                                            int globalZ = sz * S + localZ;
                                            int globalIdx = globalX * dimZ + globalZ;
                                            RecordTransparent(TransparentPlanePosY, globalIdx, secPos.UniformBlockId);
                                        }
                                }
                                else if (secPos.TransparentFacePosYBits != null)
                                {
                                    EnsureTransparentArrays();
                                    var tBits = secPos.TransparentFacePosYBits;
                                    for (int wi = 0; wi < tBits.Length; wi++)
                                    {
                                        ulong word = tBits[wi];
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

                                            ushort id = GetSectionVoxelId(secPos, localX, 15, localZ); // y fixed at 15
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                                RecordTransparent(TransparentPlanePosY, globalIdx, id);
                                            word &= word - 1;
                                        }
                                    }
                                }
                                else
                                {
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localZ = 0; localZ < S; localZ++)
                                        {
                                            ushort id = GetSectionVoxelId(secPos, localX, 15, localZ);
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                            {
                                                EnsureTransparentArrays();
                                                int globalX = sx * S + localX;
                                                int globalZ = sz * S + localZ;
                                                int globalIdx = globalX * dimZ + globalZ;
                                                RecordTransparent(TransparentPlanePosY, globalIdx, id);
                                            }
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
                            if (secNeg.Kind == Section.RepresentationKind.Uniform && secNeg.UniformBlockId != Section.AIR && TerrainLoader.IsOpaque(secNeg.UniformBlockId))
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
                            else
                            {
                                // Fallback: per-voxel decode for boundary occupancy (-Z)
                                for (int localX = 0; localX < S; localX++)
                                {
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        ushort id = GetSectionVoxelId(secNeg, localX, localY, 0);
                                        if (id != 0 && TerrainLoader.IsOpaque(id))
                                        {
                                            int globalX = sx * S + localX;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalX * dimY + globalY;
                                            SetPlaneBit(PlaneNegZ, globalIdx);
                                        }
                                    }
                                }
                            }

                            // Transparent -Z
                            {
                                bool uniformTransparentNegZ = secNeg.Kind == Section.RepresentationKind.Uniform && secNeg.UniformBlockId != Section.AIR && !TerrainLoader.IsOpaque(secNeg.UniformBlockId);
                                if (uniformTransparentNegZ)
                                {
                                    EnsureTransparentArrays();
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            int globalX = sx * S + localX;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalX * dimY + globalY;
                                            RecordTransparent(TransparentPlaneNegZ, globalIdx, secNeg.UniformBlockId);
                                        }
                                }
                                else if (secNeg.TransparentFaceNegZBits != null)
                                {
                                    EnsureTransparentArrays();
                                    var tBits = secNeg.TransparentFaceNegZBits;
                                    for (int wi = 0; wi < tBits.Length; wi++)
                                    {
                                        ulong word = tBits[wi];
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

                                            ushort id = GetSectionVoxelId(secNeg, localX, localY, 0); // z fixed at 0
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                                RecordTransparent(TransparentPlaneNegZ, globalIdx, id);
                                            word &= word - 1;
                                        }
                                    }
                                }
                                else
                                {
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            ushort id = GetSectionVoxelId(secNeg, localX, localY, 0);
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                            {
                                                EnsureTransparentArrays();
                                                int globalX = sx * S + localX;
                                                int globalY = sy * S + localY;
                                                int globalIdx = globalX * dimY + globalY;
                                                RecordTransparent(TransparentPlaneNegZ, globalIdx, id);
                                            }
                                        }
                                }
                            }
                        }

                        var secPos = sections[sx, sy, szPos];
                        if (secPos != null)
                        {
                            if (secPos.Kind == Section.RepresentationKind.Uniform && secPos.UniformBlockId != Section.AIR && TerrainLoader.IsOpaque(secPos.UniformBlockId))
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
                            else
                            {
                                // Fallback: per-voxel decode for boundary occupancy (+Z)
                                for (int localX = 0; localX < S; localX++)
                                {
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        ushort id = GetSectionVoxelId(secPos, localX, localY, 15);
                                        if (id != 0 && TerrainLoader.IsOpaque(id))
                                        {
                                            int globalX = sx * S + localX;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalX * dimY + globalY;
                                            SetPlaneBit(PlanePosZ, globalIdx);
                                        }
                                    }
                                }
                            }

                            // Transparent +Z
                            {
                                bool uniformTransparentPosZ = secPos.Kind == Section.RepresentationKind.Uniform && secPos.UniformBlockId != Section.AIR && !TerrainLoader.IsOpaque(secPos.UniformBlockId);
                                if (uniformTransparentPosZ)
                                {
                                    EnsureTransparentArrays();
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            int globalX = sx * S + localX;
                                            int globalY = sy * S + localY;
                                            int globalIdx = globalX * dimY + globalY;
                                            RecordTransparent(TransparentPlanePosZ, globalIdx, secPos.UniformBlockId);
                                        }
                                }
                                else if (secPos.TransparentFacePosZBits != null)
                                {
                                    EnsureTransparentArrays();
                                    var tBits = secPos.TransparentFacePosZBits;
                                    for (int wi = 0; wi < tBits.Length; wi++)
                                    {
                                        ulong word = tBits[wi];
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

                                            ushort id = GetSectionVoxelId(secPos, localX, localY, 15); // z fixed at 15
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                                RecordTransparent(TransparentPlanePosZ, globalIdx, id);
                                            word &= word - 1;
                                        }
                                    }
                                }
                                else
                                {
                                    for (int localX = 0; localX < S; localX++)
                                        for (int localY = 0; localY < S; localY++)
                                        {
                                            ushort id = GetSectionVoxelId(secPos, localX, localY, 15);
                                            if (id != 0 && !TerrainLoader.IsOpaque(id))
                                            {
                                                EnsureTransparentArrays();
                                                int globalX = sx * S + localX;
                                                int globalY = sy * S + localY;
                                                int globalIdx = globalX * dimY + globalY;
                                                RecordTransparent(TransparentPlanePosZ, globalIdx, id);
                                            }
                                        }
                                }
                            }
                        }
                    }
                }
            }

            // Derive per-face solidity flags from planes (opaque only tracking)
            SetFaceSolidFromPlanes();
        }

        private void CreateUniformSections(ushort blockId)
        {
            int S = Section.SECTION_SIZE;
            int voxelsPerSection = S * S * S;
            bool isTransparentUniform = blockId != Section.AIR && !TerrainLoader.IsOpaque(blockId); // water / glass etc.
            for (int sx = 0; sx < sectionsX; sx++)
                for (int sy = 0; sy < sectionsY; sy++)
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = new Section
                        {
                            IsAllAir = false,
                            Kind = Section.RepresentationKind.Uniform,
                            UniformBlockId = blockId,
                            OpaqueVoxelCount = isTransparentUniform ? 0 : voxelsPerSection,
                            VoxelCount = voxelsPerSection,
                            CompletelyFull = !isTransparentUniform, // only opaque uniform flagged full for fast paths
                            MetadataBuilt = true,
                            HasBounds = true,
                            MinLX = 0,
                            MinLY = 0,
                            MinLZ = 0,
                            MaxLX = 15,
                            MaxLY = 15,
                            MaxLZ = 15
                        };
                        if (isTransparentUniform)
                        {
                            // Uniform transparent override (e.g. water): record transparent occupancy eagerly (all 4096 bits set)
                            sec.TransparentCount = voxelsPerSection;
                            sec.HasTransparent = true;
                            sec.TransparentBits = new ulong[64];
                            for (int i = 0; i < 64; i++) sec.TransparentBits[i] = ulong.MaxValue;
                            sec.InternalExposure = 0; // no opaque exposure
                            // Build transparent boundary face masks immediately so prerender has them without further metadata rebuild.
                            SectionUtils.BuildTransparentFaceMasks(sec, sec.TransparentBits);
                        }
                        sections[sx, sy, sz] = sec;
                    }
        }
    }
}

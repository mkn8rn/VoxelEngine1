using MVGE_GFX.Models;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        // Unified masked two-pass (handles both bounding-box and full-volume cases)
        private void GenerateFacesListMaskedTwoPass()
        {
            int strideX = maxZ * maxY;
            int strideZ = maxY;

            // ---- PASS 1: Bounding box of non-empty ----
            int minX = maxX, minY = maxY, minZ = maxZ;
            int maxXb = -1, maxYb = -1, maxZb = -1;
            bool any = false;

            for (int x = 0; x < maxX; x++)
            {
                int xBase = x * strideX;
                for (int z = 0; z < maxZ; z++)
                {
                    int zBase = xBase + z * maxY;
                    for (int y = 0; y < maxY; y++)
                    {
                        int li = zBase + y;
                        if (flatBlocks[li] == emptyBlock) continue;
                        any = true;
                        if (x < minX) minX = x; if (x > maxXb) maxXb = x;
                        if (y < minY) minY = y; if (y > maxYb) maxYb = y;
                        if (z < minZ) minZ = z; if (z > maxZb) maxZb = z;
                    }
                }
            }

            if (!any)
            {
                chunkVertsList = new List<byte>(0);
                chunkUVsList = new List<byte>(0);
                chunkIndicesList = new List<uint>(0);
                indexFormat = IndexFormat.UInt;
                return;
            }

            bool fullVolume =
                minX == 0 && minY == 0 && minZ == 0 &&
                maxXb == maxX - 1 && maxYb == maxY - 1 && maxZb == maxZ - 1;

            int spanX = maxXb - minX + 1;
            int spanY = maxYb - minY + 1;
            int spanZ = maxZb - minZ + 1;

            byte[] masks;
            if (fullVolume)
            {
                masks = new byte[flatBlocks.Length]; // index == flat linear index
            }
            else
            {
                int regionVolume = spanX * spanY * spanZ;
                masks = new byte[regionVolume];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int MaskIndex(int x, int y, int z)
            {
                if (fullVolume)
                {
                    return (x * maxZ + z) * maxY + y; // original flat index
                }
                return ((x - minX) * spanZ + (z - minZ)) * spanY + (y - minY);
            }

            // Visibility (same logic)
            bool leftVisible = !(faceNegX && nNegXPosX);
            bool rightVisible = !(facePosX && nPosXNegX);
            bool bottomVisible = !(faceNegY && nNegYPosY);
            bool topVisible = !(facePosY && nPosYNegY);
            bool backVisible = !(faceNegZ && nNegZPosZ);
            bool frontVisible = !(facePosZ && nPosZNegZ);

            // Neighbor plane bitsets only if region touches that boundary
            ulong[] nLeft = null, nRight = null, nBottom = null, nTop = null, nBack = null, nFront = null;
            int yzBits = maxY * maxZ; int yzWC = (yzBits + 63) >> 6;
            int xzBits = maxX * maxZ; int xzWC = (xzBits + 63) >> 6;
            int xyBits = maxX * maxY; int xyWC = (xyBits + 63) >> 6;

            // Use cached neighbor planes only if region touches boundary.
            if (leftVisible && minX == 0) nLeft = prerenderData.NeighborPlaneNegX;
            if (rightVisible && maxXb == maxX - 1) nRight = prerenderData.NeighborPlanePosX;
            if (bottomVisible && minY == 0) nBottom = prerenderData.NeighborPlaneNegY;
            if (topVisible && maxYb == maxY - 1) nTop = prerenderData.NeighborPlanePosY;
            if (backVisible && minZ == 0) nBack = prerenderData.NeighborPlaneNegZ;
            if (frontVisible && maxZb == maxZ - 1) nFront = prerenderData.NeighborPlanePosZ;

            int totalFaces = 0;

            // ---- PASS 2: Build masks & face count ----
            for (int x = minX; x <= maxXb; x++)
            {
                int xBase = x * strideX;
                bool atMinX = x == 0;
                bool atMaxX = x == maxX - 1;
                for (int z = minZ; z <= maxZb; z++)
                {
                    int zBase = xBase + z * maxY;
                    bool atMinZ = z == 0;
                    bool atMaxZ = z == maxZ - 1;
                    int yzBaseOffset = z * maxY;
                    int xzBaseOffset = x * maxZ + z;
                    for (int y = minY; y <= maxYb; y++)
                    {
                        int li = zBase + y;
                        ushort block = flatBlocks[li];
                        if (block == emptyBlock) continue;

                        bool atMinY = y == 0;
                        bool atMaxY = y == maxY - 1;
                        byte mask = 0;

                        if (leftVisible && (atMinX ? (nLeft == null ? true : (yzBaseOffset + y >= yzBits || ((nLeft[(yzBaseOffset + y) >> 6] & (1UL << ((yzBaseOffset + y) & 63))) == 0UL))) : flatBlocks[li - strideX] == emptyBlock)) mask |= FACE_LEFT;
                        if (rightVisible && (atMaxX ? (nRight == null ? true : (yzBaseOffset + y >= yzBits || ((nRight[(yzBaseOffset + y) >> 6] & (1UL << ((yzBaseOffset + y) & 63))) == 0UL))) : flatBlocks[li + strideX] == emptyBlock)) mask |= FACE_RIGHT;
                        if (topVisible && (atMaxY ? (nTop == null ? true : (xzBaseOffset >= xzBits || ((nTop[xzBaseOffset >> 6] & (1UL << (xzBaseOffset & 63))) == 0UL))) : flatBlocks[li + 1] == emptyBlock)) mask |= FACE_TOP;
                        if (bottomVisible && (atMinY ? (nBottom == null ? true : (xzBaseOffset >= xzBits || ((nBottom[xzBaseOffset >> 6] & (1UL << (xzBaseOffset & 63))) == 0UL))) : flatBlocks[li - 1] == emptyBlock)) mask |= FACE_BOTTOM;
                        if (frontVisible && (atMaxZ ? (nFront == null ? true : (x * maxY + y >= xyBits || ((nFront[(x * maxY + y) >> 6] & (1UL << ((x * maxY + y) & 63))) == 0UL))) : flatBlocks[li + strideZ] == emptyBlock)) mask |= FACE_FRONT;
                        if (backVisible && (atMinZ ? (nBack == null ? true : (x * maxY + y >= xyBits || ((nBack[(x * maxY + y) >> 6] & (1UL << ((x * maxY + y) & 63))) == 0UL))) : flatBlocks[li - strideZ] == emptyBlock)) mask |= FACE_BACK;

                        if (mask == 0) continue;
                        int mi = MaskIndex(x, y, z);
                        masks[mi] = mask;
                        totalFaces += FacePopCount[mask];
                    }
                }
            }

            if (totalFaces == 0)
            {
                chunkVertsList = new List<byte>(0);
                chunkUVsList = new List<byte>(0);
                chunkIndicesList = new List<uint>(0);
                indexFormat = IndexFormat.UInt;
                return;
            }

            int totalVerts = totalFaces * 4;
            bool useUShortIndices = totalVerts <= 65535;
            indexFormat = useUShortIndices ? IndexFormat.UShort : IndexFormat.UInt;

            chunkVertsList = new List<byte>(totalVerts * 3);
            chunkUVsList = new List<byte>(totalVerts * 2);
            if (useUShortIndices)
                chunkIndicesUShortList = new List<ushort>(totalFaces * 6);
            else
                chunkIndicesList = new List<uint>(totalFaces * 6);

            // ---- EMIT PASS ----
            int currentVertexBase = 0;
            if (fullVolume)
            {
                // Use original flat indexing directly (slightly faster)
                for (int x = 0; x < maxX; x++)
                {
                    int xBase = x * strideX;
                    for (int z = 0; z < maxZ; z++)
                    {
                        int zBase = xBase + z * maxY;
                        for (int y = 0; y < maxY; y++)
                        {
                            int li = zBase + y;
                            byte mask = masks[li];
                            if (mask == 0) continue;
                            ushort block = flatBlocks[li];
                            var bp = new ByteVector3 { x = (byte)x, y = (byte)y, z = (byte)z };
                            if ((mask & FACE_LEFT) != 0) IntegrateFaceListEmit(block, Faces.LEFT, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_RIGHT) != 0) IntegrateFaceListEmit(block, Faces.RIGHT, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_TOP) != 0) IntegrateFaceListEmit(block, Faces.TOP, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_BOTTOM) != 0) IntegrateFaceListEmit(block, Faces.BOTTOM, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_FRONT) != 0) IntegrateFaceListEmit(block, Faces.FRONT, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_BACK) != 0) IntegrateFaceListEmit(block, Faces.BACK, bp, ref currentVertexBase, useUShortIndices);
                        }
                    }
                }
            }
            else
            {
                for (int x = minX; x <= maxXb; x++)
                {
                    for (int z = minZ; z <= maxZb; z++)
                    {
                        for (int y = minY; y <= maxYb; y++)
                        {
                            byte mask = masks[MaskIndex(x, y, z)];
                            if (mask == 0) continue;
                            ushort block = flatBlocks[(x * maxZ + z) * maxY + y];
                            var bp = new ByteVector3 { x = (byte)x, y = (byte)y, z = (byte)z };
                            if ((mask & FACE_LEFT) != 0) IntegrateFaceListEmit(block, Faces.LEFT, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_RIGHT) != 0) IntegrateFaceListEmit(block, Faces.RIGHT, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_TOP) != 0) IntegrateFaceListEmit(block, Faces.TOP, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_BOTTOM) != 0) IntegrateFaceListEmit(block, Faces.BOTTOM, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_FRONT) != 0) IntegrateFaceListEmit(block, Faces.FRONT, bp, ref currentVertexBase, useUShortIndices);
                            if ((mask & FACE_BACK) != 0) IntegrateFaceListEmit(block, Faces.BACK, bp, ref currentVertexBase, useUShortIndices);
                        }
                    }
                }
            }
        }
    }
}

using MVGE_GFX.Models;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVGE_GFX.Utils
{
    internal static class RenderPooledFacesUtils
    {
        internal struct PooledFaceBuildResult
        {
            public bool FullyOccluded; // preserved for compatibility (always false here because caller pre-checks)
            public bool UseUShort;
            public bool HasSingleOpaque;
            public byte[] VertBuffer;
            public byte[] UVBuffer;
            public uint[] IndicesUIntBuffer;
            public ushort[] IndicesUShortBuffer;
            public int VertBytesUsed;
            public int UVBytesUsed;
            public int IndicesUsed;
        }

        // UV cache: key = (blockId << 3) | face
        private static readonly ConcurrentDictionary<int, byte[]> uvByteCache = new();

        private static byte[] GetOrCreateUv(BlockTextureAtlas atlas, ushort block, Faces face)
        {
            int key = (block << 3) | (int)face;
            return uvByteCache.GetOrAdd(key, _ =>
            {
                var list = atlas.GetBlockUVs(block, face);
                var arr = new byte[8];
                for (int j = 0; j < list.Count && j < 4; j++)
                {
                    arr[j * 2] = list[j].x;
                    arr[j * 2 + 1] = list[j].y;
                }
                return arr;
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LocalIndex(int x, int y, int z, int maxY, int maxZ) => (x * maxZ + z) * maxY + y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFace(
            ushort block,
            Faces face,
            byte bx,
            byte by,
            byte bz,
            ref int faceIndex,
            ushort emptyBlock,
            bool hasSingleOpaque,
            ushort singleSolidId,
            byte[] singleSolidUVConcat,
            BlockTextureAtlas atlas,
            byte[] vertBuffer,
            byte[] uvBuffer,
            bool useUShort,
            ushort[] indicesUShortBuffer,
            uint[] indicesUIntBuffer)
        {
            if (hasSingleOpaque) block = singleSolidId;
            int baseVertexIndex = faceIndex * 4;
            int vertexByteOffset = baseVertexIndex * 3;
            int uvByteOffset = baseVertexIndex * 2;
            int indexOffset = faceIndex * 6;

            var verts = RawFaceData.rawVertexData[face];
            for (int i = 0; i < 4; i++)
            {
                vertBuffer[vertexByteOffset + i * 3 + 0] = (byte)(verts[i].x + bx);
                vertBuffer[vertexByteOffset + i * 3 + 1] = (byte)(verts[i].y + by);
                vertBuffer[vertexByteOffset + i * 3 + 2] = (byte)(verts[i].z + bz);
            }

            if (block != emptyBlock)
            {
                if (singleSolidUVConcat != null)
                {
                    int off = ((int)face) * 8;
                    // copy 8 bytes
                    uvBuffer[uvByteOffset + 0] = singleSolidUVConcat[off + 0];
                    uvBuffer[uvByteOffset + 1] = singleSolidUVConcat[off + 1];
                    uvBuffer[uvByteOffset + 2] = singleSolidUVConcat[off + 2];
                    uvBuffer[uvByteOffset + 3] = singleSolidUVConcat[off + 3];
                    uvBuffer[uvByteOffset + 4] = singleSolidUVConcat[off + 4];
                    uvBuffer[uvByteOffset + 5] = singleSolidUVConcat[off + 5];
                    uvBuffer[uvByteOffset + 6] = singleSolidUVConcat[off + 6];
                    uvBuffer[uvByteOffset + 7] = singleSolidUVConcat[off + 7];
                }
                else
                {
                    byte[] uvBytes = GetOrCreateUv(atlas, block, face);
                    uvBuffer[uvByteOffset + 0] = uvBytes[0];
                    uvBuffer[uvByteOffset + 1] = uvBytes[1];
                    uvBuffer[uvByteOffset + 2] = uvBytes[2];
                    uvBuffer[uvByteOffset + 3] = uvBytes[3];
                    uvBuffer[uvByteOffset + 4] = uvBytes[4];
                    uvBuffer[uvByteOffset + 5] = uvBytes[5];
                    uvBuffer[uvByteOffset + 6] = uvBytes[6];
                    uvBuffer[uvByteOffset + 7] = uvBytes[7];
                }
            }
            else
            {
                for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = 0;
            }

            if (useUShort)
            {
                indicesUShortBuffer[indexOffset + 0] = (ushort)(baseVertexIndex + 0);
                indicesUShortBuffer[indexOffset + 1] = (ushort)(baseVertexIndex + 1);
                indicesUShortBuffer[indexOffset + 2] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[indexOffset + 3] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[indexOffset + 4] = (ushort)(baseVertexIndex + 3);
                indicesUShortBuffer[indexOffset + 5] = (ushort)(baseVertexIndex + 0);
            }
            else
            {
                indicesUIntBuffer[indexOffset + 0] = (uint)(baseVertexIndex + 0);
                indicesUIntBuffer[indexOffset + 1] = (uint)(baseVertexIndex + 1);
                indicesUIntBuffer[indexOffset + 2] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[indexOffset + 3] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[indexOffset + 4] = (uint)(baseVertexIndex + 3);
                indicesUIntBuffer[indexOffset + 5] = (uint)(baseVertexIndex + 0);
            }

            faceIndex++;
        }

        internal static PooledFaceBuildResult BuildPooledFaces(
            Vector3 chunkWorldPosition,
            int maxX,
            int maxY,
            int maxZ,
            ushort emptyBlock,
            Func<int, int, int, ushort> getWorldBlock,
            Func<int, int, int, ushort> getLocalBlock,
            BlockTextureAtlas atlas)
        {
            int yzPlaneBits = maxY * maxZ;
            int yzWC = (yzPlaneBits + 63) >> 6;
            int xzPlaneBits = maxX * maxZ;
            int xzWC = (xzPlaneBits + 63) >> 6;
            int xyPlaneBits = maxX * maxY;
            int xyWC = (xyPlaneBits + 63) >> 6;

            int voxelCount = maxX * maxY * maxZ;
            // Pool working arrays
            ushort[] localBlocks = ArrayPool<ushort>.Shared.Rent(voxelCount);
            ulong[] xSlabs = ArrayPool<ulong>.Shared.Rent(maxX * yzWC);
            ulong[] ySlabs = ArrayPool<ulong>.Shared.Rent(maxY * xzWC);
            ulong[] zSlabs = ArrayPool<ulong>.Shared.Rent(maxZ * xyWC);
            bool[] xSlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxX);
            bool[] ySlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxY);
            bool[] zSlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxZ);
            ulong[] neighborLeft = ArrayPool<ulong>.Shared.Rent(yzWC);
            ulong[] neighborRight = ArrayPool<ulong>.Shared.Rent(yzWC);
            ulong[] neighborBottom = ArrayPool<ulong>.Shared.Rent(xzWC);
            ulong[] neighborTop = ArrayPool<ulong>.Shared.Rent(xzWC);
            ulong[] neighborBack = ArrayPool<ulong>.Shared.Rent(xyWC);
            ulong[] neighborFront = ArrayPool<ulong>.Shared.Rent(xyWC);

            // Zero arrays (ArrayPool does not guarantee cleared)
            Array.Clear(xSlabs, 0, xSlabs.Length);
            Array.Clear(ySlabs, 0, ySlabs.Length);
            Array.Clear(zSlabs, 0, zSlabs.Length);
            Array.Clear(xSlabNonEmpty, 0, xSlabNonEmpty.Length);
            Array.Clear(ySlabNonEmpty, 0, ySlabNonEmpty.Length);
            Array.Clear(zSlabNonEmpty, 0, zSlabNonEmpty.Length);
            Array.Clear(neighborLeft, 0, neighborLeft.Length);
            Array.Clear(neighborRight, 0, neighborRight.Length);
            Array.Clear(neighborBottom, 0, neighborBottom.Length);
            Array.Clear(neighborTop, 0, neighborTop.Length);
            Array.Clear(neighborBack, 0, neighborBack.Length);
            Array.Clear(neighborFront, 0, neighborFront.Length);

            bool haveSolid = false; bool detectSingleSolid = true; ushort singleSolidId = 0;

            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;

            // Identical pair arrays (rent only if needed)
            bool[] identicalPairX = null;
            bool[] identicalPairY = null;
            bool[] identicalPairZ = null;

            try
            {
                for (int z = 0; z < maxZ; z++)
                    for (int y = 0; y < maxY; y++)
                    {
                        int yzIndex = z * maxY + y; int w = yzIndex >> 6; int b = yzIndex & 63;
                        if (getWorldBlock(baseWX - 1, baseWY + y, baseWZ + z) != emptyBlock) neighborLeft[w] |= 1UL << b;
                        if (getWorldBlock(baseWX + maxX, baseWY + y, baseWZ + z) != emptyBlock) neighborRight[w] |= 1UL << b;
                    }
                for (int x = 0; x < maxX; x++)
                    for (int z = 0; z < maxZ; z++)
                    {
                        int xzIndex = x * maxZ + z; int w = xzIndex >> 6; int b = xzIndex & 63;
                        if (getWorldBlock(baseWX + x, baseWY - 1, baseWZ + z) != emptyBlock) neighborBottom[w] |= 1UL << b;
                        if (getWorldBlock(baseWX + x, baseWY + maxY, baseWZ + z) != emptyBlock) neighborTop[w] |= 1UL << b;
                    }
                for (int x = 0; x < maxX; x++)
                    for (int y = 0; y < maxY; y++)
                    {
                        int xyIndex = x * maxY + y; int w = xyIndex >> 6; int b = xyIndex & 63;
                        if (getWorldBlock(baseWX + x, baseWY + y, baseWZ - 1) != emptyBlock) neighborBack[w] |= 1UL << b;
                        if (getWorldBlock(baseWX + x, baseWY + y, baseWZ + maxZ) != emptyBlock) neighborFront[w] |= 1UL << b;
                    }

                for (int x = 0; x < maxX; x++)
                {
                    int xSlabOffset = x * yzWC;
                    ulong slabAccumX = 0UL;
                    for (int z = 0; z < maxZ; z++)
                    {
                        for (int y = 0; y < maxY; y++)
                        {
                            int li = LocalIndex(x, y, z, maxY, maxZ);
                            ushort bId = getLocalBlock(x, y, z);
                            localBlocks[li] = bId;
                            if (bId != emptyBlock)
                            {
                                int yzIndex = z * maxY + y; int wy = yzIndex >> 6; int by = yzIndex & 63;
                                ulong bit = 1UL << by;
                                xSlabs[xSlabOffset + wy] |= bit;
                                slabAccumX |= bit;

                                int xzIndex = x * maxZ + z; int wxz = xzIndex >> 6; int bxz = xzIndex & 63;
                                ySlabs[y * xzWC + wxz] |= 1UL << bxz;

                                int xyIndex = x * maxY + y; int wxy = xyIndex >> 6; int bxy = xyIndex & 63;
                                zSlabs[z * xyWC + wxy] |= 1UL << bxy;

                                if (detectSingleSolid)
                                {
                                    if (!haveSolid) { haveSolid = true; singleSolidId = bId; }
                                    else if (bId != singleSolidId) { detectSingleSolid = false; }
                                }
                            }
                        }
                    }
                    if (slabAccumX != 0) xSlabNonEmpty[x] = true;
                }
                bool singleSolidType = detectSingleSolid;
                for (int y = 0; y < maxY; y++)
                {
                    int off = y * xzWC; ulong acc = 0UL; for (int w = 0; w < xzWC; w++) acc |= ySlabs[off + w]; ySlabNonEmpty[y] = acc != 0;
                }
                for (int z = 0; z < maxZ; z++)
                {
                    int off = z * xyWC; ulong acc = 0UL; for (int w = 0; w < xyWC; w++) acc |= zSlabs[off + w]; zSlabNonEmpty[z] = acc != 0;
                }
                bool hasSingleOpaque = haveSolid && singleSolidType;

                if (maxX > 1) identicalPairX = ArrayPool<bool>.Shared.Rent(maxX - 1);
                if (maxY > 1) identicalPairY = ArrayPool<bool>.Shared.Rent(maxY - 1);
                if (maxZ > 1) identicalPairZ = ArrayPool<bool>.Shared.Rent(maxZ - 1);

                if (identicalPairX != null)
                {
                    Array.Clear(identicalPairX, 0, identicalPairX.Length);
                    for (int x = 0; x < maxX - 1; x++)
                    {
                        if (!xSlabNonEmpty[x] || !xSlabNonEmpty[x + 1]) continue;
                        int aOff = x * yzWC; int bOff = (x + 1) * yzWC; bool eq = true;
                        for (int w = 0; w < yzWC; w++) { if (xSlabs[aOff + w] != xSlabs[bOff + w]) { eq = false; break; } }
                        identicalPairX[x] = eq;
                    }
                }
                if (identicalPairY != null)
                {
                    Array.Clear(identicalPairY, 0, identicalPairY.Length);
                    for (int y = 0; y < maxY - 1; y++)
                    {
                        if (!ySlabNonEmpty[y] || !ySlabNonEmpty[y + 1]) continue;
                        int aOff = y * xzWC; int bOff = (y + 1) * xzWC; bool eq = true;
                        for (int w = 0; w < xzWC; w++) { if (ySlabs[aOff + w] != ySlabs[bOff + w]) { eq = false; break; } }
                        identicalPairY[y] = eq;
                    }
                }
                if (identicalPairZ != null)
                {
                    Array.Clear(identicalPairZ, 0, identicalPairZ.Length);
                    for (int z = 0; z < maxZ - 1; z++)
                    {
                        if (!zSlabNonEmpty[z] || !zSlabNonEmpty[z + 1]) continue;
                        int aOff = z * xyWC; int bOff = (z + 1) * xyWC; bool eq = true;
                        for (int w = 0; w < xyWC; w++) { if (zSlabs[aOff + w] != zSlabs[bOff + w]) { eq = false; break; } }
                        identicalPairZ[z] = eq;
                    }
                }

                int totalFaces = 0;
                for (int x = 0; x < maxX; x++)
                {
                    if (!xSlabNonEmpty[x]) continue;
                    bool skipLeft = x > 0 && identicalPairX != null && identicalPairX[x - 1];
                    bool skipRight = x < maxX - 1 && identicalPairX != null && identicalPairX[x];
                    if (skipLeft && skipRight) continue;
                    int curOff = x * yzWC; int prevOff = (x - 1) * yzWC; int nextOff = (x + 1) * yzWC;
                    for (int w = 0; w < yzWC; w++)
                    {
                        ulong cur = xSlabs[curOff + w]; if (cur == 0) continue;
                        if (!skipLeft)
                        {
                            ulong leftBits = (x == 0) ? (cur & ~neighborLeft[w]) : (cur & ~xSlabs[prevOff + w]);
                            if (leftBits != 0) totalFaces += BitOperations.PopCount(leftBits);
                        }
                        if (!skipRight)
                        {
                            ulong rightBits = (x == maxX - 1) ? (cur & ~neighborRight[w]) : (cur & ~xSlabs[nextOff + w]);
                            if (rightBits != 0) totalFaces += BitOperations.PopCount(rightBits);
                        }
                    }
                }
                for (int y = 0; y < maxY; y++)
                {
                    if (!ySlabNonEmpty[y]) continue;
                    bool skipBottom = y > 0 && identicalPairY != null && identicalPairY[y - 1];
                    bool skipTop = y < maxY - 1 && identicalPairY != null && identicalPairY[y];
                    if (skipBottom && skipTop) continue;
                    int curOff = y * xzWC; int prevOff = (y - 1) * xzWC; int nextOff = (y + 1) * xzWC;
                    for (int w = 0; w < xzWC; w++)
                    {
                        ulong cur = ySlabs[curOff + w]; if (cur == 0) continue;
                        if (!skipBottom)
                        {
                            ulong bottomBits = (y == 0) ? (cur & ~neighborBottom[w]) : (cur & ~ySlabs[prevOff + w]);
                            if (bottomBits != 0) totalFaces += BitOperations.PopCount(bottomBits);
                        }
                        if (!skipTop)
                        {
                            ulong topBits = (y == maxY - 1) ? (cur & ~neighborTop[w]) : (cur & ~ySlabs[nextOff + w]);
                            if (topBits != 0) totalFaces += BitOperations.PopCount(topBits);
                        }
                    }
                }
                for (int z = 0; z < maxZ; z++)
                {
                    if (!zSlabNonEmpty[z]) continue;
                    bool skipBack = z > 0 && identicalPairZ != null && identicalPairZ[z - 1];
                    bool skipFront = z < maxZ - 1 && identicalPairZ != null && identicalPairZ[z];
                    if (skipBack && skipFront) continue;
                    int curOff = z * xyWC; int prevOff = (z - 1) * xyWC; int nextOff = (z + 1) * xyWC;
                    for (int w = 0; w < xyWC; w++)
                    {
                        ulong cur = zSlabs[curOff + w]; if (cur == 0) continue;
                        if (!skipBack)
                        {
                            ulong backBits = (z == 0) ? (cur & ~neighborBack[w]) : (cur & ~zSlabs[prevOff + w]);
                            if (backBits != 0) totalFaces += BitOperations.PopCount(backBits);
                        }
                        if (!skipFront)
                        {
                            ulong frontBits = (z == maxZ - 1) ? (cur & ~neighborFront[w]) : (cur & ~zSlabs[nextOff + w]);
                            if (frontBits != 0) totalFaces += BitOperations.PopCount(frontBits);
                        }
                    }
                }

                int totalVerts = totalFaces * 4;
                bool useUShort = totalVerts <= 65535;
                byte[] vertBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
                byte[] uvBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
                uint[] indicesUIntBuffer = null; ushort[] indicesUShortBuffer = null;
                if (useUShort) indicesUShortBuffer = ArrayPool<ushort>.Shared.Rent(totalFaces * 6); else indicesUIntBuffer = ArrayPool<uint>.Shared.Rent(totalFaces * 6);

                byte[] singleSolidUVConcat = null;
                if (hasSingleOpaque)
                {
                    singleSolidUVConcat = new byte[48];
                    for (int f = 0; f < 6; f++)
                    {
                        var uvBytes = GetOrCreateUv(atlas, singleSolidId, (Faces)f);
                        System.Buffer.BlockCopy(uvBytes, 0, singleSolidUVConcat, f * 8, 8);
                    }
                }

                int faceIndex = 0;
                // Emit faces
                for (int x = 0; x < maxX; x++)
                {
                    if (!xSlabNonEmpty[x]) continue;
                    bool skipLeft = x > 0 && identicalPairX != null && identicalPairX[x - 1];
                    bool skipRight = x < maxX - 1 && identicalPairX != null && identicalPairX[x];
                    if (skipLeft && skipRight) continue;
                    int curOff = x * yzWC; int prevOff = (x - 1) * yzWC; int nextOff = (x + 1) * yzWC;
                    for (int w = 0; w < yzWC; w++)
                    {
                        ulong cur = xSlabs[curOff + w]; if (cur == 0) continue;
                        if (!skipLeft)
                        {
                            ulong leftBits = (x == 0) ? (cur & ~neighborLeft[w]) : (cur & ~xSlabs[prevOff + w]);
                            ulong bits = leftBits;
                            while (bits != 0)
                            {
                                int t = BitOperations.TrailingZeroCount(bits);
                                int yzIndex = (w << 6) + t; if (yzIndex >= yzPlaneBits) break;
                                int z = yzIndex / maxY; int y = yzIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                                WriteFace(localBlocks[li], Faces.LEFT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, hasSingleOpaque, singleSolidId, singleSolidUVConcat, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                bits &= bits - 1;
                            }
                        }
                        if (!skipRight)
                        {
                            ulong rightBits = (x == maxX - 1) ? (cur & ~neighborRight[w]) : (cur & ~xSlabs[nextOff + w]);
                            ulong bits = rightBits;
                            while (bits != 0)
                            {
                                int t = BitOperations.TrailingZeroCount(bits);
                                int yzIndex = (w << 6) + t; if (yzIndex >= yzPlaneBits) break;
                                int z = yzIndex / maxY; int y = yzIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                                WriteFace(localBlocks[li], Faces.RIGHT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, hasSingleOpaque, singleSolidId, singleSolidUVConcat, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                bits &= bits - 1;
                            }
                        }
                    }
                }
                for (int y = 0; y < maxY; y++)
                {
                    if (!ySlabNonEmpty[y]) continue;
                    bool skipBottom = y > 0 && identicalPairY != null && identicalPairY[y - 1];
                    bool skipTop = y < maxY - 1 && identicalPairY != null && identicalPairY[y];
                    if (skipBottom && skipTop) continue;
                    int curOff = y * xzWC; int prevOff = (y - 1) * xzWC; int nextOff = (y + 1) * xzWC;
                    for (int w = 0; w < xzWC; w++)
                    {
                        ulong cur = ySlabs[curOff + w]; if (cur == 0) continue;
                        if (!skipBottom)
                        {
                            ulong bottomBits = (y == 0) ? (cur & ~neighborBottom[w]) : (cur & ~ySlabs[prevOff + w]);
                            ulong bits = bottomBits;
                            while (bits != 0)
                            {
                                int t = BitOperations.TrailingZeroCount(bits);
                                int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                                int x = xzIndex / maxZ; int z = xzIndex % maxZ; int li = LocalIndex(x, y, z, maxY, maxZ);
                                WriteFace(localBlocks[li], Faces.BOTTOM, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, hasSingleOpaque, singleSolidId, singleSolidUVConcat, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                bits &= bits - 1;
                            }
                        }
                        if (!skipTop)
                        {
                            ulong topBits = (y == maxY - 1) ? (cur & ~neighborTop[w]) : (cur & ~ySlabs[nextOff + w]);
                            ulong bits = topBits;
                            while (bits != 0)
                            {
                                int t = BitOperations.TrailingZeroCount(bits);
                                int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                                int x = xzIndex / maxZ; int z = xzIndex % maxZ; int li = LocalIndex(x, y, z, maxY, maxZ);
                                WriteFace(localBlocks[li], Faces.TOP, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, hasSingleOpaque, singleSolidId, singleSolidUVConcat, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                bits &= bits - 1;
                            }
                        }
                    }
                }
                for (int z = 0; z < maxZ; z++)
                {
                    if (!zSlabNonEmpty[z]) continue;
                    bool skipBack = z > 0 && identicalPairZ != null && identicalPairZ[z - 1];
                    bool skipFront = z < maxZ - 1 && identicalPairZ != null && identicalPairZ[z];
                    if (skipBack && skipFront) continue;
                    int curOff = z * xyWC; int prevOff = (z - 1) * xyWC; int nextOff = (z + 1) * xyWC;
                    for (int w = 0; w < xyWC; w++)
                    {
                        ulong cur = zSlabs[curOff + w]; if (cur == 0) continue;
                        if (!skipBack)
                        {
                            ulong backBits = (z == 0) ? (cur & ~neighborBack[w]) : (cur & ~zSlabs[prevOff + w]);
                            ulong bits = backBits;
                            while (bits != 0)
                            {
                                int t = BitOperations.TrailingZeroCount(bits);
                                int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                int x = xyIndex / maxY; int y = xyIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                                WriteFace(localBlocks[li], Faces.BACK, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, hasSingleOpaque, singleSolidId, singleSolidUVConcat, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                bits &= bits - 1;
                            }
                        }
                        if (!skipFront)
                        {
                            ulong frontBits = (z == maxZ - 1) ? (cur & ~neighborFront[w]) : (cur & ~zSlabs[nextOff + w]);
                            ulong bits = frontBits;
                            while (bits != 0)
                            {
                                int t = BitOperations.TrailingZeroCount(bits);
                                int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                int x = xyIndex / maxY; int y = xyIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                                WriteFace(localBlocks[li], Faces.FRONT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, hasSingleOpaque, singleSolidId, singleSolidUVConcat, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                bits &= bits - 1;
                            }
                        }
                    }
                }

                return new PooledFaceBuildResult
                {
                    FullyOccluded = false,
                    UseUShort = useUShort,
                    HasSingleOpaque = hasSingleOpaque,
                    VertBuffer = vertBuffer,
                    UVBuffer = uvBuffer,
                    IndicesUIntBuffer = indicesUIntBuffer,
                    IndicesUShortBuffer = indicesUShortBuffer,
                    VertBytesUsed = totalVerts * 3,
                    UVBytesUsed = totalVerts * 2,
                    IndicesUsed = totalFaces * 6
                };
            }
            finally
            {
                // Return identical pair arrays (if rented)
                if (identicalPairX != null) ArrayPool<bool>.Shared.Return(identicalPairX, false);
                if (identicalPairY != null) ArrayPool<bool>.Shared.Return(identicalPairY, false);
                if (identicalPairZ != null) ArrayPool<bool>.Shared.Return(identicalPairZ, false);
                // Return pooled working arrays
                ArrayPool<ushort>.Shared.Return(localBlocks, false);
                ArrayPool<ulong>.Shared.Return(xSlabs, false);
                ArrayPool<ulong>.Shared.Return(ySlabs, false);
                ArrayPool<ulong>.Shared.Return(zSlabs, false);
                ArrayPool<bool>.Shared.Return(xSlabNonEmpty, false);
                ArrayPool<bool>.Shared.Return(ySlabNonEmpty, false);
                ArrayPool<bool>.Shared.Return(zSlabNonEmpty, false);
                ArrayPool<ulong>.Shared.Return(neighborLeft, false);
                ArrayPool<ulong>.Shared.Return(neighborRight, false);
                ArrayPool<ulong>.Shared.Return(neighborBottom, false);
                ArrayPool<ulong>.Shared.Return(neighborTop, false);
                ArrayPool<ulong>.Shared.Return(neighborBack, false);
                ArrayPool<ulong>.Shared.Return(neighborFront, false);
            }
        }
    }
}

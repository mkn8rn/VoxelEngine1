using System;
using System.Buffers;
using System.Numerics;
using MVGE_GFX.Models;
using MVGE_INF.Models.Terrain;

namespace MVGE_GFX.Terrain
{
    public partial class DenseChunkRender
    {
        private static bool AllBitsSet(ulong[] arr, int bitCount)
        {
            int wc = (bitCount + 63) >> 6; int rem = bitCount & 63; ulong lastMask = rem == 0 ? ulong.MaxValue : (1UL << rem) - 1UL;
            for (int i = 0; i < wc; i++)
            {
                ulong expected = (i == wc - 1) ? lastMask : ulong.MaxValue;
                if ((arr[i] & expected) != expected) return false;
            }
            return true;
        }

        private static bool SlabsEqual(ulong[] slabs, int offA, int offB, int wordCount)
        {
            int i = 0;
            int vecCount = Vector<ulong>.Count;
            for (; i <= wordCount - vecCount; i += vecCount)
            {
                var va = new Vector<ulong>(slabs, offA + i);
                var vb = new Vector<ulong>(slabs, offB + i);
                var diff = va ^ vb;
                if (!Vector<ulong>.Zero.Equals(diff)) return false;
            }
            for (; i < wordCount; i++)
            {
                if (slabs[offA + i] != slabs[offB + i]) return false;
            }
            return true;
        }

        private void PrefetchNeighborPlane(GetBlockFastDelegate fastGetter, Func<int, int, int, ushort> slowGetter, ulong[] target, int baseWX, int baseWY, int baseWZ, int dimA, int dimB, int wordCount, char plane)
        {
            if (plane == 'X')
            {
                for (int z = 0; z < dimB; z++)
                {
                    for (int y = 0; y < dimA; y++)
                    {
                        int yzIndex = z * dimA + y; int w = yzIndex >> 6; int b = yzIndex & 63;
                        ushort val = 0; bool ok = fastGetter != null && fastGetter(baseWX, baseWY + y, baseWZ + z, out val);
                        if (!ok) val = slowGetter(baseWX, baseWY + y, baseWZ + z);
                        if (val != emptyBlock) target[w] |= 1UL << b;
                    }
                }
            }
            else if (plane == 'Y')
            {
                for (int z = 0; z < dimB; z++)
                {
                    for (int x = 0; x < dimA; x++)
                    {
                        int xzIndex = x * dimB + z; int w = xzIndex >> 6; int b = xzIndex & 63;
                        ushort val = 0; bool ok = fastGetter != null && fastGetter(baseWX + x, baseWY, baseWZ + z, out val);
                        if (!ok) val = slowGetter(baseWX + x, baseWY, baseWZ + z);
                        if (val != emptyBlock) target[w] |= 1UL << b;
                    }
                }
            }
            else
            {
                for (int x = 0; x < dimA; x++)
                {
                    for (int y = 0; y < dimB; y++)
                    {
                        int xyIndex = x * dimB + y; int w = xyIndex >> 6; int b = xyIndex & 63;
                        ushort val = 0; bool ok = fastGetter != null && fastGetter(baseWX + x, baseWY + y, baseWZ, out val);
                        if (!ok) val = slowGetter(baseWX + x, baseWY + y, baseWZ);
                        if (val != emptyBlock) target[w] |= 1UL << b;
                    }
                }
            }
        }

        // Fully occluded early exit (all our faces + neighbor opposing faces solid)
        private bool IsFullyOccludedByFlags() =>
            faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
            nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ;

        // BuildResult factory for empty returns
        private static BuildResult EmptyBuildResult(bool hasSingleOpaque) => new BuildResult
        {
            UseUShort = true,
            HasSingleOpaque = hasSingleOpaque,
            VertBuffer = EMPTY_BYTES,
            UVBuffer = EMPTY_BYTES,
            IndicesUShortBuffer = EMPTY_USHORTS,
            IndicesUIntBuffer = null,
            VertBytesUsed = 0,
            UVBytesUsed = 0,
            IndicesUsed = 0
        };

        // Flag-based full occlusion (all our faces + neighbor opposing faces solid)
        private bool TryFlagBasedFullOcclusionEarlyOut(bool hasSingleOpaque, out BuildResult result)
        {
            if (IsFullyOccludedByFlags())
            {
                result = EmptyBuildResult(hasSingleOpaque);
                return true;
            }
            result = default;
            return false;
        }

        private static void ClearUlongsStatic(ulong[] arr, int len)
        {
            for (int i = 0; i < len; i++) arr[i] = 0UL;
        }

        // Simplified fully solid neighbor occlusion test (retained to satisfy existing call sites).
        // If chunk is entirely solid and all neighbor touching planes are also fully solid, we early out.
        private bool TryFullySolidNeighborOcclusion(long solidCount, int voxelCount,
                                                     int yzWC, int xzWC, int xyWC,
                                                     int yzPlaneBits, int xzPlaneBits, int xyPlaneBits,
                                                     int baseWX, int baseWY, int baseWZ,
                                                     ref ulong[] neighborLeft, ref ulong[] neighborRight,
                                                     ref ulong[] neighborBottom, ref ulong[] neighborTop,
                                                     ref ulong[] neighborBack, ref ulong[] neighborFront,
                                                     ref bool loadedLeft, ref bool loadedRight,
                                                     ref bool loadedBottom, ref bool loadedTop,
                                                     ref bool loadedBack, ref bool loadedFront,
                                                     bool hasSingleOpaque,
                                                     out BuildResult result)
        {
            result = default;
            if (solidCount != voxelCount) return false; // only consider fully solid volume

            neighborLeft = ArrayPool<ulong>.Shared.Rent(yzWC); ClearUlongsStatic(neighborLeft, yzWC); PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, neighborLeft, baseWX - 1, baseWY, baseWZ, maxY, maxZ, yzWC, plane: 'X'); loadedLeft = true;
            neighborRight = ArrayPool<ulong>.Shared.Rent(yzWC); ClearUlongsStatic(neighborRight, yzWC); PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, neighborRight, baseWX + maxX, baseWY, baseWZ, maxY, maxZ, yzWC, plane: 'X'); loadedRight = true;
            neighborBack = ArrayPool<ulong>.Shared.Rent(xyWC); ClearUlongsStatic(neighborBack, xyWC); PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, neighborBack, baseWX, baseWY, baseWZ - 1, maxX, maxY, xyWC, plane: 'Z'); loadedBack = true;
            neighborFront = ArrayPool<ulong>.Shared.Rent(xyWC); ClearUlongsStatic(neighborFront, xyWC); PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, neighborFront, baseWX, baseWY, baseWZ + maxZ, maxX, maxY, xyWC, plane: 'Z'); loadedFront = true;
            neighborBottom = ArrayPool<ulong>.Shared.Rent(xzWC); ClearUlongsStatic(neighborBottom, xzWC); PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, neighborBottom, baseWX, baseWY - 1, baseWZ, maxX, maxZ, xzWC, plane: 'Y'); loadedBottom = true;
            neighborTop = ArrayPool<ulong>.Shared.Rent(xzWC); ClearUlongsStatic(neighborTop, xzWC); PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, neighborTop, baseWX, baseWY + maxY, baseWZ, maxX, maxZ, xzWC, plane: 'Y'); loadedTop = true;

            if (AllBitsSet(neighborLeft, yzPlaneBits) && AllBitsSet(neighborRight, yzPlaneBits) &&
                AllBitsSet(neighborBottom, xzPlaneBits) && AllBitsSet(neighborTop, xzPlaneBits) &&
                AllBitsSet(neighborBack, xyPlaneBits) && AllBitsSet(neighborFront, xyPlaneBits))
            {
                result = EmptyBuildResult(hasSingleOpaque);
                return true;
            }
            return false;
        }

        // ----- UNIFORM SINGLE-SOLID FAST PATH -----
        private BuildResult BuildUniformSingleSolidFastPath()
        {
            if (IsFullyOccludedByFlags())
            {
                return new BuildResult { UseUShort = true, HasSingleOpaque = true, VertBuffer = EMPTY_BYTES, UVBuffer = EMPTY_BYTES, IndicesUShortBuffer = EMPTY_USHORTS, IndicesUIntBuffer = null, VertBytesUsed = 0, UVBytesUsed = 0, IndicesUsed = 0 };
            }
            bool emitLeft = !(faceNegX && nNegXPosX);
            bool emitRight = !(facePosX && nPosXNegX);
            bool emitBottom = !(faceNegY && nNegYPosY);
            bool emitTop = !(facePosY && nPosYNegY);
            bool emitBack = !(faceNegZ && nNegZPosZ);
            bool emitFront = !(facePosZ && nPosZNegZ);

            int faces = 0;
            if (emitLeft) faces += maxY * maxZ;
            if (emitRight) faces += maxY * maxZ;
            if (emitBottom) faces += maxX * maxZ;
            if (emitTop) faces += maxX * maxZ;
            if (emitBack) faces += maxX * maxY;
            if (emitFront) faces += maxX * maxY;
            if (faces == 0)
            {
                return new BuildResult { UseUShort = true, HasSingleOpaque = true, VertBuffer = EMPTY_BYTES, UVBuffer = EMPTY_BYTES, IndicesUShortBuffer = EMPTY_USHORTS, IndicesUIntBuffer = null, VertBytesUsed = 0, UVBytesUsed = 0, IndicesUsed = 0 };
            }
            int totalVerts = faces * 4;
            bool useUShort = totalVerts <= 65535;
            byte[] vertBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
            byte[] uvBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
            ushort[] indicesUShortBuffer = useUShort ? ArrayPool<ushort>.Shared.Rent(faces * 6) : null;
            uint[] indicesUIntBuffer = useUShort ? null : ArrayPool<uint>.Shared.Rent(faces * 6);
            byte[] uvConcat = GetSingleSolidUVConcat(forceSingleSolidBlockId) ?? new byte[48];
            int faceIndex = 0;

            if (emitLeft)
            {
                int x = 0;
                for (int z = 0; z < maxZ; z++)
                    for (int y = 0; y < maxY; y++)
                        WriteFaceSingle(Faces.LEFT, (byte)x, (byte)y, (byte)z, ref faceIndex, uvConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            }
            if (emitRight)
            {
                int x = maxX - 1;
                for (int z = 0; z < maxZ; z++)
                    for (int y = 0; y < maxY; y++)
                        WriteFaceSingle(Faces.RIGHT, (byte)x, (byte)y, (byte)z, ref faceIndex, uvConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            }
            if (emitBottom)
            {
                int y = 0;
                for (int x = 0; x < maxX; x++)
                    for (int z = 0; z < maxZ; z++)
                        WriteFaceSingle(Faces.BOTTOM, (byte)x, (byte)y, (byte)z, ref faceIndex, uvConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            }
            if (emitTop)
            {
                int y = maxY - 1;
                for (int x = 0; x < maxX; x++)
                    for (int z = 0; z < maxZ; z++)
                        WriteFaceSingle(Faces.TOP, (byte)x, (byte)y, (byte)z, ref faceIndex, uvConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            }
            if (emitBack)
            {
                int z = 0;
                for (int x = 0; x < maxX; x++)
                    for (int y = 0; y < maxY; y++)
                        WriteFaceSingle(Faces.BACK, (byte)x, (byte)y, (byte)z, ref faceIndex, uvConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            }
            if (emitFront)
            {
                int z = maxZ - 1;
                for (int x = 0; x < maxX; x++)
                    for (int y = 0; y < maxY; y++)
                        WriteFaceSingle(Faces.FRONT, (byte)x, (byte)y, (byte)z, ref faceIndex, uvConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            }

            return new BuildResult
            {
                UseUShort = useUShort,
                HasSingleOpaque = true,
                VertBuffer = vertBuffer,
                UVBuffer = uvBuffer,
                IndicesUIntBuffer = indicesUIntBuffer,
                IndicesUShortBuffer = indicesUShortBuffer,
                VertBytesUsed = totalVerts * 3,
                UVBytesUsed = totalVerts * 2,
                IndicesUsed = faces * 6
            };
        }
    }
}

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

        // Delegates removed: neighbor planes now pre-cached in constructor via ChunkPrerenderData
        private bool IsFullyOccludedByFlags() =>
            faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
            nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ;

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

        // Simplified fully solid neighbor occlusion test now uses pre-cached neighbor planes.
        private bool TryFullySolidNeighborOcclusion(long solidCount, int voxelCount,
                                                     int yzPlaneBits, int xzPlaneBits, int xyPlaneBits,
                                                     bool hasSingleOpaque,
                                                     out BuildResult result)
        {
            result = default;
            if (solidCount != voxelCount) return false;
            if (neighborPlaneNegX == null || neighborPlanePosX == null || neighborPlaneNegY == null || neighborPlanePosY == null || neighborPlaneNegZ == null || neighborPlanePosZ == null)
                return false; // cannot evaluate without all planes
            if (AllBitsSet(neighborPlaneNegX, yzPlaneBits) && AllBitsSet(neighborPlanePosX, yzPlaneBits) &&
                AllBitsSet(neighborPlaneNegY, xzPlaneBits) && AllBitsSet(neighborPlanePosY, xzPlaneBits) &&
                AllBitsSet(neighborPlaneNegZ, xyPlaneBits) && AllBitsSet(neighborPlanePosZ, xyPlaneBits))
            {
                result = EmptyBuildResult(hasSingleOpaque);
                return true;
            }
            return false;
        }

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

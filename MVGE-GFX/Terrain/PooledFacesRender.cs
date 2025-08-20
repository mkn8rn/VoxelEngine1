using MVGE_GFX.Models;
using MVGE_INF.Models.Terrain;
using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVGE_GFX.Terrain
{
    internal sealed class PooledFacesRender
    {
        internal struct BuildResult
        {
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

        private readonly Vector3 chunkWorldPosition;
        private readonly int maxX, maxY, maxZ;
        private readonly ushort emptyBlock;
        private readonly Func<int, int, int, ushort> getWorldBlock; // legacy path
        private readonly GetBlockFastDelegate getWorldBlockFast; // renamed for consistency
        private readonly Func<int, int, int, ushort> getLocalBlock; // fallback delegate path
        private readonly BlockTextureAtlas atlas;
        // Optional pre-flattened local blocks (contiguous, x-major, then z, then y)
        private readonly ushort[] preFlattenedLocalBlocks; // null if not supplied

        // Flat UV lookup: [blockId * 6 + face] * 8 bytes (4 verts * 2 bytes each)
        private static byte[] uvLut; // null until initialized
        private static int uvLutBlockCount;
        private static readonly object uvInitLock = new();

        // Heuristic threshold
        private const float IDENTICAL_SLAB_FILL_THRESHOLD = 0.70f; // 70%

        public PooledFacesRender(Vector3 chunkWorldPosition,
                                 int maxX, int maxY, int maxZ,
                                 ushort emptyBlock,
                                 Func<int, int, int, ushort> worldGetter,
                                 GetBlockFastDelegate fastGetter,
                                 Func<int, int, int, ushort> localGetter,
                                 BlockTextureAtlas atlas,
                                 ushort[] preFlattenedLocalBlocks = null)
        {
            this.chunkWorldPosition = chunkWorldPosition;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            this.emptyBlock = emptyBlock;
            getWorldBlock = worldGetter;
            getWorldBlockFast = fastGetter;
            getLocalBlock = localGetter;
            this.atlas = atlas;
            this.preFlattenedLocalBlocks = preFlattenedLocalBlocks; // may be null
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LocalIndex(int x, int y, int z, int maxY, int maxZ) => (x * maxZ + z) * maxY + y;

        private static void EnsureUvLut(BlockTextureAtlas atlas)
        {
            if (uvLut != null) return;
            lock (uvInitLock)
            {
                if (uvLut != null) return;
                // Determine block count from atlas static dictionary
                uvLutBlockCount = BlockTextureAtlas.blockTypeUVCoordinates.Count;
                if (uvLutBlockCount == 0)
                {
                    uvLutBlockCount = 1; // safeguard
                }
                uvLut = new byte[uvLutBlockCount * 6 * 8];
                for (ushort b = 0; b < uvLutBlockCount; b++)
                {
                    for (int f = 0; f < 6; f++)
                    {
                        var list = atlas.GetBlockUVs(b, (Faces)f); // returns 4 entries
                        int baseOffset = ((b * 6) + f) * 8;
                        for (int i = 0; i < 4; i++)
                        {
                            uvLut[baseOffset + i * 2] = list[i].x;
                            uvLut[baseOffset + i * 2 + 1] = list[i].y;
                        }
                    }
                }
            }
        }

        // Specialized face writers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFaceMulti(
            ushort block,
            Faces face,
            byte bx,
            byte by,
            byte bz,
            ref int faceIndex,
            ushort emptyBlock,
            BlockTextureAtlas atlas,
            byte[] vertBuffer,
            byte[] uvBuffer,
            bool useUShort,
            ushort[] indicesUShortBuffer,
            uint[] indicesUIntBuffer)
        {
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
                // Direct span copy from LUT
                int lutOffset = ((block * 6) + (int)face) * 8;
                for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = uvLut[lutOffset + i];
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFaceSingle(
            Faces face,
            byte bx,
            byte by,
            byte bz,
            ref int faceIndex,
            byte[] singleSolidUVConcat,
            byte[] vertBuffer,
            byte[] uvBuffer,
            bool useUShort,
            ushort[] indicesUShortBuffer,
            uint[] indicesUIntBuffer)
        {
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
            int off = ((int)face) * 8;
            for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = singleSolidUVConcat[off + i];

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

        public BuildResult Build()
        {
            // Ensure UV LUT ready
            EnsureUvLut(atlas);

            int yzPlaneBits = maxY * maxZ;
            int yzWC = (yzPlaneBits + 63) >> 6;
            int xzPlaneBits = maxX * maxZ; int xzWC = (xzPlaneBits + 63) >> 6;
            int xyPlaneBits = maxX * maxY; int xyWC = (xyPlaneBits + 63) >> 6;
            int voxelCount = maxX * maxY * maxZ;

            // If we already have a flattened array supplied, use it directly (no per-voxel delegate calls)
            bool suppliedLocalBlocks = preFlattenedLocalBlocks != null;
            ushort[] localBlocks = suppliedLocalBlocks ? preFlattenedLocalBlocks : ArrayPool<ushort>.Shared.Rent(voxelCount);

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

            static void ClearUlongs(ulong[] arr, int len) { for (int i = 0; i < len; i++) arr[i] = 0UL; }
            static void ClearBools(bool[] arr, int len) { for (int i = 0; i < len; i++) arr[i] = false; }
            ClearUlongs(xSlabs, maxX * yzWC);
            ClearUlongs(ySlabs, maxY * xzWC);
            ClearUlongs(zSlabs, maxZ * xyWC);
            ClearBools(xSlabNonEmpty, maxX);
            ClearBools(ySlabNonEmpty, maxY);
            ClearBools(zSlabNonEmpty, maxZ);
            ClearUlongs(neighborLeft, yzWC);
            ClearUlongs(neighborRight, yzWC);
            ClearUlongs(neighborBottom, xzWC);
            ClearUlongs(neighborTop, xzWC);
            ClearUlongs(neighborBack, xyWC);
            ClearUlongs(neighborFront, xyWC);

            bool haveSolid = false; bool detectSingleSolid = true; ushort singleSolidId = 0;
            bool[] identicalPairX = null, identicalPairY = null, identicalPairZ = null;
            int baseWX = (int)chunkWorldPosition.X; int baseWY = (int)chunkWorldPosition.Y; int baseWZ = (int)chunkWorldPosition.Z;

            long solidCount = 0; // track total solid voxels for density heuristic

            var worldGetter = getWorldBlock;
            var fastGetter = getWorldBlockFast;

            int xNonEmptyCount = 0, yNonEmptyCount = 0, zNonEmptyCount = 0;

            try
            {
                // Prefetch neighbor boundary occupancy planes
                PrefetchNeighborPlane(fastGetter, worldGetter, neighborLeft, baseWX - 1, baseWY, baseWZ, maxY, maxZ, yzWC, plane: 'X');
                PrefetchNeighborPlane(fastGetter, worldGetter, neighborRight, baseWX + maxX, baseWY, baseWZ, maxY, maxZ, yzWC, plane: 'X');
                PrefetchNeighborPlane(fastGetter, worldGetter, neighborBack, baseWX, baseWY, baseWZ - 1, maxX, maxY, xyWC, plane: 'Z');
                PrefetchNeighborPlane(fastGetter, worldGetter, neighborFront, baseWX, baseWY, baseWZ + maxZ, maxX, maxY, xyWC, plane: 'Z');
                PrefetchNeighborPlane(fastGetter, worldGetter, neighborBottom, baseWX, baseWY - 1, baseWZ, maxX, maxZ, xzWC, plane: 'Y');
                PrefetchNeighborPlane(fastGetter, worldGetter, neighborTop, baseWX, baseWY + maxY, baseWZ, maxX, maxZ, xzWC, plane: 'Y');

                // Optimized voxel scan with incremental linear index
                for (int x = 0; x < maxX; x++)
                {
                    int xSlabOffset = x * yzWC;
                    ulong slabAccumX = 0UL;
                    for (int z = 0; z < maxZ; z++)
                    {
                        int baseYZIndexZ = z * maxY;
                        int liBase = (x * maxZ + z) * maxY; // start index for (x,z,0)
                        int li = liBase;
                        for (int y = 0; y < maxY; y++, li++)
                        {
                            int yzIndex = baseYZIndexZ + y; int wyz = yzIndex >> 6; int byz = yzIndex & 63;
                            ushort bId;
                            if (suppliedLocalBlocks)
                            {
                                bId = localBlocks[li];
                            }
                            else
                            {
                                bId = getLocalBlock(x, y, z); // fallback delegate only when no pre-flattened array
                                localBlocks[li] = bId; // store for later multi-face pass
                            }
                            if (bId != emptyBlock)
                            {
                                solidCount++;
                                ulong bit = 1UL << byz;
                                xSlabs[xSlabOffset + wyz] |= bit; slabAccumX |= bit;
                                int xzIndex2 = x * maxZ + z; int wxz2 = xzIndex2 >> 6; int bxz2 = xzIndex2 & 63; ySlabs[y * xzWC + wxz2] |= 1UL << bxz2;
                                int xyIndex2 = x * maxY + y; int wxy2 = xyIndex2 >> 6; int bxy2 = xyIndex2 & 63; zSlabs[z * xyWC + wxy2] |= 1UL << bxy2;
                                if (!ySlabNonEmpty[y]) { ySlabNonEmpty[y] = true; yNonEmptyCount++; }
                                if (!zSlabNonEmpty[z]) { zSlabNonEmpty[z] = true; zNonEmptyCount++; }
                                if (detectSingleSolid)
                                {
                                    if (!haveSolid) { haveSolid = true; singleSolidId = bId; }
                                    else if (bId != singleSolidId) { detectSingleSolid = false; }
                                }
                            }
                        }
                    }
                    if (slabAccumX != 0) { xSlabNonEmpty[x] = true; xNonEmptyCount++; }
                }
                bool singleSolidType = detectSingleSolid;
                bool hasSingleOpaque = haveSolid && singleSolidType;

                // Early full-occlusion cull inside pooled builder: chunk full AND all six neighbor planes full
                if (solidCount == voxelCount &&
                    AllBitsSet(neighborLeft, yzPlaneBits) &&
                    AllBitsSet(neighborRight, yzPlaneBits) &&
                    AllBitsSet(neighborBottom, xzPlaneBits) &&
                    AllBitsSet(neighborTop, xzPlaneBits) &&
                    AllBitsSet(neighborBack, xyPlaneBits) &&
                    AllBitsSet(neighborFront, xyPlaneBits))
                {
                    // Rent minimal 1-byte buffers so caller's pooling return logic still works uniformly.
                    byte[] vb = ArrayPool<byte>.Shared.Rent(1);
                    byte[] ub = ArrayPool<byte>.Shared.Rent(1);
                    ushort[] ib = ArrayPool<ushort>.Shared.Rent(1);
                    return new BuildResult
                    {
                        UseUShort = true,
                        HasSingleOpaque = hasSingleOpaque,
                        VertBuffer = vb,
                        UVBuffer = ub,
                        IndicesUShortBuffer = ib,
                        IndicesUIntBuffer = null,
                        VertBytesUsed = 0,
                        UVBytesUsed = 0,
                        IndicesUsed = 0
                    };
                }

                float fillRatio = solidCount == 0 ? 0f : (float)solidCount / voxelCount;
                bool enableIdenticalDetection = fillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD;
                float xAxisFillRatio = (float)xNonEmptyCount / Math.Max(1, maxX);
                float yAxisFillRatio = (float)yNonEmptyCount / Math.Max(1, maxY);
                float zAxisFillRatio = (float)zNonEmptyCount / Math.Max(1, maxZ);
                bool enableX = enableIdenticalDetection && xAxisFillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD && maxX > 1;
                bool enableY = enableIdenticalDetection && yAxisFillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD && maxY > 1;
                bool enableZ = enableIdenticalDetection && zAxisFillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD && maxZ > 1;

                if (enableX) identicalPairX = ArrayPool<bool>.Shared.Rent(maxX - 1);
                if (enableY) identicalPairY = ArrayPool<bool>.Shared.Rent(maxY - 1);
                if (enableZ) identicalPairZ = ArrayPool<bool>.Shared.Rent(maxZ - 1);

                if (identicalPairX != null)
                {
                    Array.Clear(identicalPairX);
                    for (int x = 0; x < maxX - 1; x++)
                    {
                        if (!xSlabNonEmpty[x] || !xSlabNonEmpty[x + 1]) continue;
                        int aOff = x * yzWC; int bOff = (x + 1) * yzWC; bool eq = true;
                        if (yzWC >= 1 && xSlabs[aOff] != xSlabs[bOff]) eq = false;
                        else if (yzWC >= 2 && xSlabs[aOff + 1] != xSlabs[bOff + 1]) eq = false;
                        if (eq)
                        {
                            for (int w = 2; w < yzWC; w++) { if (xSlabs[aOff + w] != xSlabs[bOff + w]) { eq = false; break; } }
                        }
                        identicalPairX[x] = eq;
                    }
                }
                if (identicalPairY != null)
                {
                    Array.Clear(identicalPairY);
                    for (int y = 0; y < maxY - 1; y++)
                    {
                        if (!ySlabNonEmpty[y] || !ySlabNonEmpty[y + 1]) continue;
                        int aOff = y * xzWC; int bOff = (y + 1) * xzWC; bool eq = true;
                        if (xzWC >= 1 && ySlabs[aOff] != ySlabs[bOff]) eq = false;
                        else if (xzWC >= 2 && ySlabs[aOff + 1] != ySlabs[bOff + 1]) eq = false;
                        if (eq)
                        {
                            for (int w = 2; w < xzWC; w++) { if (ySlabs[aOff + w] != ySlabs[bOff + w]) { eq = false; break; } }
                        }
                        identicalPairY[y] = eq;
                    }
                }
                if (identicalPairZ != null)
                {
                    Array.Clear(identicalPairZ);
                    for (int z = 0; z < maxZ - 1; z++)
                    {
                        if (!zSlabNonEmpty[z] || !zSlabNonEmpty[z + 1]) continue;
                        int aOff = z * xyWC; int bOff = (z + 1) * xyWC; bool eq = true;
                        if (xyWC >= 1 && zSlabs[aOff] != zSlabs[bOff]) eq = false;
                        else if (xyWC >= 2 && zSlabs[aOff + 1] != zSlabs[bOff + 1]) eq = false;
                        if (eq)
                        {
                            for (int w = 2; w < xyWC; w++) { if (zSlabs[aOff + w] != zSlabs[bOff + w]) { eq = false; break; } }
                        }
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
                byte[] vertBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 3)); // rent at least 1 so we can always return
                byte[] uvBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 2));
                uint[] indicesUIntBuffer = null; ushort[] indicesUShortBuffer = null;
                if (useUShort) indicesUShortBuffer = ArrayPool<ushort>.Shared.Rent(Math.Max(1, totalFaces * 6)); else indicesUIntBuffer = ArrayPool<uint>.Shared.Rent(Math.Max(1, totalFaces * 6));

                byte[] singleSolidUVConcat = null;
                if (hasSingleOpaque)
                {
                    singleSolidUVConcat = new byte[48];
                    for (int f = 0; f < 6; f++)
                    {
                        int lutOffset = ((singleSolidId * 6) + f) * 8;
                        for (int i = 0; i < 8; i++) singleSolidUVConcat[f * 8 + i] = uvLut[lutOffset + i];
                    }
                }

                int faceIndex = 0;
                if (hasSingleOpaque)
                {
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
                                    int z = yzIndex / maxY; int y = yzIndex % maxY;
                                    WriteFaceSingle(Faces.LEFT, (byte)x, (byte)y, (byte)z, ref faceIndex, singleSolidUVConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
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
                                    int z = yzIndex / maxY; int y = yzIndex % maxY;
                                    WriteFaceSingle(Faces.RIGHT, (byte)x, (byte)y, (byte)z, ref faceIndex, singleSolidUVConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
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
                                ulong bits = bottomBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                                    int x2 = xzIndex / maxZ; int z2 = xzIndex % maxZ;
                                    WriteFaceSingle(Faces.BOTTOM, (byte)x2, (byte)y, (byte)z2, ref faceIndex, singleSolidUVConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                    bits &= bits - 1;
                                }
                            }
                            if (!skipTop)
                            {
                                ulong topBits = (y == maxY - 1) ? (cur & ~neighborTop[w]) : (cur & ~ySlabs[nextOff + w]);
                                ulong bits = topBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                                    int x2 = xzIndex / maxZ; int z2 = xzIndex % maxZ;
                                    WriteFaceSingle(Faces.TOP, (byte)x2, (byte)y, (byte)z2, ref faceIndex, singleSolidUVConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
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
                                ulong bits = backBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                    int x2 = xyIndex / maxY; int y2 = xyIndex % maxY;
                                    WriteFaceSingle(Faces.BACK, (byte)x2, (byte)y2, (byte)z, ref faceIndex, singleSolidUVConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                    bits &= bits - 1;
                                }
                            }
                            if (!skipFront)
                            {
                                ulong frontBits = (z == maxZ - 1) ? (cur & ~neighborFront[w]) : (cur & ~zSlabs[nextOff + w]);
                                ulong bits = frontBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                    int x2 = xyIndex / maxY; int y2 = xyIndex % maxY;
                                    WriteFaceSingle(Faces.FRONT, (byte)x2, (byte)y2, (byte)z, ref faceIndex, singleSolidUVConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                    bits &= bits - 1;
                                }
                            }
                        }
                    }
                }
                else
                {
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
                                    WriteFaceMulti(localBlocks[li], Faces.LEFT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
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
                                    WriteFaceMulti(localBlocks[li], Faces.RIGHT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
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
                                ulong bits = bottomBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                                    int x2 = xzIndex / maxZ; int z2 = xzIndex % maxZ; int li = LocalIndex(x2, y, z2, maxY, maxZ);
                                    WriteFaceMulti(localBlocks[li], Faces.BOTTOM, (byte)x2, (byte)y, (byte)z2, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                    bits &= bits - 1;
                                }
                            }
                            if (!skipTop)
                            {
                                ulong topBits = (y == maxY - 1) ? (cur & ~neighborTop[w]) : (cur & ~ySlabs[nextOff + w]);
                                ulong bits = topBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                                    int x2 = xzIndex / maxZ; int z2 = xzIndex % maxZ; int li = LocalIndex(x2, y, z2, maxY, maxZ);
                                    WriteFaceMulti(localBlocks[li], Faces.TOP, (byte)x2, (byte)y, (byte)z2, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
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
                                ulong bits = backBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                    int x2 = xyIndex / maxY; int y2 = xyIndex % maxY; int li = LocalIndex(x2, y2, z, maxY, maxZ);
                                    WriteFaceMulti(localBlocks[li], Faces.BACK, (byte)x2, (byte)y2, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                    bits &= bits - 1;
                                }
                            }
                            if (!skipFront)
                            {
                                ulong frontBits = (z == maxZ - 1) ? (cur & ~neighborFront[w]) : (cur & ~zSlabs[nextOff + w]);
                                ulong bits = frontBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                    int x2 = xyIndex / maxY; int y2 = xyIndex % maxY; int li = LocalIndex(x2, y2, z, maxY, maxZ);
                                    WriteFaceMulti(localBlocks[li], Faces.FRONT, (byte)x2, (byte)y2, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                    bits &= bits - 1;
                                }
                            }
                        }
                    }
                }

                return new BuildResult
                {
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
                if (identicalPairX != null) ArrayPool<bool>.Shared.Return(identicalPairX, false);
                if (identicalPairY != null) ArrayPool<bool>.Shared.Return(identicalPairY, false);
                if (identicalPairZ != null) ArrayPool<bool>.Shared.Return(identicalPairZ, false);
                if (!suppliedLocalBlocks) ArrayPool<ushort>.Shared.Return(localBlocks, false);
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

        private static bool AllBitsSet(ulong[] arr, int bitCount)
        {
            int wc = (bitCount + 63) >> 6;
            int rem = bitCount & 63;
            ulong lastMask = rem == 0 ? ulong.MaxValue : (1UL << rem) - 1UL;
            for (int i = 0; i < wc; i++)
            {
                ulong expected = (i == wc - 1) ? lastMask : ulong.MaxValue;
                if ((arr[i] & expected) != expected) return false;
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
            else // 'Z'
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
    }
}

using MVGE_GFX.Models;
using MVGE_INF.Models.Terrain;
using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector2 = OpenTK.Mathematics.Vector2;
using MVGE_GFX.Textures;

namespace MVGE_GFX.Terrain
{
    public partial class DenseChunkRender
    {
        // Static empty arrays to avoid renting tiny buffers for empty/fully-occluded chunks
        private static readonly byte[] EMPTY_BYTES = Array.Empty<byte>();
        private static readonly ushort[] EMPTY_USHORTS = Array.Empty<ushort>();
        private static readonly uint[] EMPTY_UINTS = Array.Empty<uint>();

        private readonly Vector3 chunkWorldPosition;
        private readonly int maxX, maxY, maxZ;
        private readonly ushort emptyBlock;
        private readonly Func<int, int, int, ushort> getWorldBlock; // legacy path
        private readonly GetBlockFastDelegate getWorldBlockFast; // renamed for consistency
        private readonly Func<int, int, int, ushort> getLocalBlock; // fallback delegate path
        private readonly BlockTextureAtlas atlas;
        // Optional pre-flattened local blocks (contiguous, x-major, then z, then y)
        private readonly ushort[] preFlattenedLocalBlocks; // null if not supplied

        // Incoming face solidity flags (current chunk faces)
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        // Neighbor opposing face solidity flags
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;

        // Uniform single-solid fast path flags (set when chunk known to be one block id everywhere)
        private readonly bool forceSingleSolid;
        private readonly ushort forceSingleSolidBlockId;

        // Dense LUT fields (legacy). Left in place for fallback but not used in sparse mode.
        private static byte[] uvLut; // null in sparse mode
        private static int uvLutBlockCount;
        private static readonly object uvInitLock = new();

        // Sparse LUT maps blockId -> 48-byte array (6 faces * 8 bytes)
        private static Dictionary<ushort, byte[]> uvLutSparse; // null until built

        // Cache for single solid UV concat (references same 48-byte arrays in sparse mode)
        private static Dictionary<ushort, byte[]> singleSolidUvCacheSparse; // for sparse
        private static byte[][] singleSolidUvCache; // legacy dense cache

        // Heuristic threshold
        private const float IDENTICAL_SLAB_FILL_THRESHOLD = 0.70f; // 70%

        // ----- Thread local plane bitset pool (yz/xz/xy plane sized) -----
        // These are small compared to full slab arrays; pooling per thread reduces contention.
        [ThreadStatic] private static Dictionary<int, Stack<ulong[]>> tlPlanePools;
        private static ulong[] TLPlaneRent(int len)
        {
            var pools = tlPlanePools;
            if (pools != null && pools.TryGetValue(len, out var stack) && stack.Count > 0)
            {
                var arr = stack.Pop();
                Array.Clear(arr, 0, arr.Length);
                return arr;
            }
            return new ulong[len];
        }
        private static void TLPlaneReturn(ulong[] arr)
        {
            if (arr == null) return;
            var pools = tlPlanePools;
            if (pools == null)
            {
                pools = new Dictionary<int, Stack<ulong[]>>(4);
                tlPlanePools = pools;
            }
            if (!pools.TryGetValue(arr.Length, out var stack))
            {
                stack = new Stack<ulong[]>(4);
                pools[arr.Length] = stack;
            }
            stack.Push(arr);
        }

        public DenseChunkRender(Vector3 chunkWorldPosition,
                                 int maxX, int maxY, int maxZ,
                                 ushort emptyBlock,
                                 Func<int, int, int, ushort> worldGetter,
                                 GetBlockFastDelegate fastGetter,
                                 Func<int, int, int, ushort> localGetter,
                                 BlockTextureAtlas atlas,
                                 ushort[] preFlattenedLocalBlocks = null,
                                 bool faceNegX = false,
                                 bool facePosX = false,
                                 bool faceNegY = false,
                                 bool facePosY = false,
                                 bool faceNegZ = false,
                                 bool facePosZ = false,
                                 bool nNegXPosX = false,
                                 bool nPosXNegX = false,
                                 bool nNegYPosY = false,
                                 bool nPosYNegY = false,
                                 bool nNegZPosZ = false,
                                 bool nPosZNegZ = false,
                                 bool forceSingleSolid = false,
                                 ushort forceSingleSolidBlockId = 0)
        {
            this.chunkWorldPosition = chunkWorldPosition;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            this.emptyBlock = emptyBlock;
            getWorldBlock = worldGetter;
            getWorldBlockFast = fastGetter;
            getLocalBlock = localGetter;
            this.atlas = atlas;
            this.preFlattenedLocalBlocks = preFlattenedLocalBlocks; // may be null
            this.faceNegX = faceNegX; this.facePosX = facePosX; this.faceNegY = faceNegY; this.facePosY = facePosY; this.faceNegZ = faceNegZ; this.facePosZ = facePosZ;
            this.nNegXPosX = nNegXPosX; this.nPosXNegX = nPosXNegX; this.nNegYPosY = nNegYPosY; this.nPosYNegY = nPosYNegY; this.nNegZPosZ = nNegZPosZ; this.nPosZNegZ = nPosZNegZ;
            this.forceSingleSolid = forceSingleSolid; this.forceSingleSolidBlockId = forceSingleSolidBlockId;
        }

        internal BuildResult Build()
        {
            // Ensure UV LUT ready
            EnsureUvLut(atlas);

            // ----- UNIFORM SINGLE-SOLID FAST PATH (enhanced with plane merging) -----
            if (forceSingleSolid)
            {
                return BuildUniformSingleSolidFastPath();
            }

            // ----- GENERAL / EXISTING PATH -----
            int yzPlaneBits = maxY * maxZ;
            int yzWC = (yzPlaneBits + 63) >> 6;
            int xzPlaneBits = maxX * maxZ; int xzWC = (xzPlaneBits + 63) >> 6;
            int xyPlaneBits = maxX * maxY; int xyWC = (xyPlaneBits + 63) >> 6;
            int voxelCount = maxX * maxY * maxZ;

            // If we already have a flattened array supplied, use it directly (no per-voxel delegate calls)
            bool suppliedLocalBlocks = preFlattenedLocalBlocks != null;
            ushort[] localBlocks = suppliedLocalBlocks ? preFlattenedLocalBlocks : ArrayPool<ushort>.Shared.Rent(voxelCount);

            // Occupancy slabs (always needed)
            ulong[] xSlabs = ArrayPool<ulong>.Shared.Rent(maxX * yzWC);
            ulong[] ySlabs = ArrayPool<ulong>.Shared.Rent(maxY * xzWC);
            ulong[] zSlabs = ArrayPool<ulong>.Shared.Rent(maxZ * xyWC);
            bool[] xSlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxX);
            bool[] ySlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxY);
            bool[] zSlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxZ);

            // Replace manual clears with Array.Clear for potential vectorized zeroing
            Array.Clear(xSlabs, 0, maxX * yzWC);
            Array.Clear(ySlabs, 0, maxY * xzWC);
            Array.Clear(zSlabs, 0, maxZ * xyWC);
            Array.Clear(xSlabNonEmpty, 0, maxX);
            Array.Clear(ySlabNonEmpty, 0, maxY);
            Array.Clear(zSlabNonEmpty, 0, maxZ);

            // Neighbor planes (lazy). We allocate & populate only when required by a boundary face.
            ulong[] neighborLeft = null, neighborRight = null, neighborBottom = null, neighborTop = null, neighborBack = null, neighborFront = null;
            bool loadedLeft = false, loadedRight = false, loadedBottom = false, loadedTop = false, loadedBack = false, loadedFront = false;
            // Track if neighbor planes came from thread-local pool (vs ArrayPool in another partial path)
            bool leftFromTL = false, rightFromTL = false, bottomFromTL = false, topFromTL = false, backFromTL = false, frontFromTL = false;

            bool haveSolid = false; bool detectSingleSolid = true; ushort singleSolidId = 0;
            long solidCount = 0; int xNonEmptyCount = 0, yNonEmptyCount = 0, zNonEmptyCount = 0;
            int baseWX = (int)chunkWorldPosition.X; int baseWY = (int)chunkWorldPosition.Y; int baseWZ = (int)chunkWorldPosition.Z;

            try
            {
                // Optimized voxel scan with incremental linear index. Build slabs & detect boundaries.
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
                bool hasSingleOpaque = haveSolid && detectSingleSolid;

                // Early empty
                if (solidCount == 0)
                {
                    return EmptyBuildResult(false);
                }

                // Flag-based full occlusion (all our faces + neighbor opposing faces solid)
                if (TryFlagBasedFullOcclusionEarlyOut(hasSingleOpaque, out var flagOccluded))
                {
                    return flagOccluded;
                }

                // Fully solid neighbor occlusion test
                if (TryFullySolidNeighborOcclusion(solidCount, voxelCount, yzWC, xzWC, xyWC, yzPlaneBits, xzPlaneBits, xyPlaneBits, baseWX, baseWY, baseWZ,
                                                   ref neighborLeft, ref neighborRight, ref neighborBottom, ref neighborTop, ref neighborBack, ref neighborFront,
                                                   ref loadedLeft, ref loadedRight, ref loadedBottom, ref loadedTop, ref loadedBack, ref loadedFront,
                                                   hasSingleOpaque, out var fullySolidOccluded))
                {
                    return fullySolidOccluded;
                }

                // Helpers to lazily load needed neighbor plane for boundary tests (explicit reorder to avoid allocation when cur==0)
                ulong[] GetNeighbor(ref ulong[] arr, ref bool loaded, ref bool fromTL, int wc, int dimA, int dimB, char plane, int ox, int oy, int oz)
                {
                    if (loaded) return arr;
                    arr = TLPlaneRent(wc); // thread-local rent
                    fromTL = true;
                    PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, arr, ox, oy, oz, dimA, dimB, wc, plane);
                    loaded = true;
                    return arr;
                }

                bool[] identicalPairX = null, identicalPairY = null, identicalPairZ = null; // Retain structure for consistency but detection below
                float fillRatio = (float)solidCount / voxelCount;
                bool enableIdenticalDetection = fillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD;
                float xAxisFillRatio = (float)xNonEmptyCount / Math.Max(1, maxX);
                float yAxisFillRatio = (float)yNonEmptyCount / Math.Max(1, maxY);
                float zAxisFillRatio = (float)zNonEmptyCount / Math.Max(1, maxZ);
                bool enableX = enableIdenticalDetection && xAxisFillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD && maxX > 1;
                bool enableY = enableIdenticalDetection && yAxisFillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD && maxY > 1;
                bool enableZ = enableIdenticalDetection && zAxisFillRatio >= IDENTICAL_SLAB_FILL_THRESHOLD && maxZ > 1;

                if (enableX) { identicalPairX = ArrayPool<bool>.Shared.Rent(maxX - 1); Array.Clear(identicalPairX, 0, maxX - 1); }
                if (enableY) { identicalPairY = ArrayPool<bool>.Shared.Rent(maxY - 1); Array.Clear(identicalPairY, 0, maxY - 1); }
                if (enableZ) { identicalPairZ = ArrayPool<bool>.Shared.Rent(maxZ - 1); Array.Clear(identicalPairZ, 0, maxZ - 1); }

                if (identicalPairX != null)
                {
                    for (int x = 0; x < maxX - 1; x++)
                    {
                        if (!xSlabNonEmpty[x] || !xSlabNonEmpty[x + 1]) continue;
                        identicalPairX[x] = SlabsEqual(xSlabs, x * yzWC, (x + 1) * yzWC, yzWC);
                    }
                }
                if (identicalPairY != null)
                {
                    for (int y = 0; y < maxY - 1; y++)
                    {
                        if (!ySlabNonEmpty[y] || !ySlabNonEmpty[y + 1]) continue;
                        identicalPairY[y] = SlabsEqual(ySlabs, y * xzWC, (y + 1) * xzWC, xzWC);
                    }
                }
                if (identicalPairZ != null)
                {
                    for (int z = 0; z < maxZ - 1; z++)
                    {
                        if (!zSlabNonEmpty[z] || !zSlabNonEmpty[z + 1]) continue;
                        identicalPairZ[z] = SlabsEqual(zSlabs, z * xyWC, (z + 1) * xyWC, xyWC);
                    }
                }

                // Directional occlusion flags to skip boundary neighbor work
                bool occludeLeft = faceNegX && nNegXPosX;
                bool occludeRight = facePosX && nPosXNegX;
                bool occludeBottom = faceNegY && nNegYPosY;
                bool occludeTop = facePosY && nPosYNegY;
                bool occludeBack = faceNegZ && nNegZPosZ;
                bool occludeFront = facePosZ && nPosZNegZ;

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
                        ulong cur = xSlabs[curOff + w]; if (cur == 0) continue; // skip neighbor load entirely when empty
                        if (!skipLeft && !(x == 0 && occludeLeft))
                        {
                            ulong leftBits;
                            if (x == 0)
                            {
                                var nl = GetNeighbor(ref neighborLeft, ref loadedLeft, ref leftFromTL, yzWC, maxY, maxZ, 'X', baseWX - 1, baseWY, baseWZ);
                                leftBits = cur & ~nl[w];
                            }
                            else
                            {
                                leftBits = cur & ~xSlabs[prevOff + w];
                            }
                            if (leftBits != 0) totalFaces += BitOperations.PopCount(leftBits);
                        }
                        if (!skipRight && !(x == maxX - 1 && occludeRight))
                        {
                            ulong rightBits;
                            if (x == maxX - 1)
                            {
                                var nr = GetNeighbor(ref neighborRight, ref loadedRight, ref rightFromTL, yzWC, maxY, maxZ, 'X', baseWX + maxX, baseWY, baseWZ);
                                rightBits = cur & ~nr[w];
                            }
                            else
                            {
                                rightBits = cur & ~xSlabs[nextOff + w];
                            }
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
                        if (!skipBottom && !(y == 0 && occludeBottom))
                        {
                            ulong bottomBits;
                            if (y == 0)
                            {
                                var nb = GetNeighbor(ref neighborBottom, ref loadedBottom, ref bottomFromTL, xzWC, maxX, maxZ, 'Y', baseWX, baseWY - 1, baseWZ);
                                bottomBits = cur & ~nb[w];
                            }
                            else
                            {
                                bottomBits = cur & ~ySlabs[prevOff + w];
                            }
                            if (bottomBits != 0) totalFaces += BitOperations.PopCount(bottomBits);
                        }
                        if (!skipTop && !(y == maxY - 1 && occludeTop))
                        {
                            ulong topBits;
                            if (y == maxY - 1)
                            {
                                var nt = GetNeighbor(ref neighborTop, ref loadedTop, ref topFromTL, xzWC, maxX, maxZ, 'Y', baseWX, baseWY + maxY, baseWZ);
                                topBits = cur & ~nt[w];
                            }
                            else
                            {
                                topBits = cur & ~ySlabs[nextOff + w];
                            }
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
                        if (!skipBack && !(z == 0 && occludeBack))
                        {
                            ulong backBits;
                            if (z == 0)
                            {
                                var nbk = GetNeighbor(ref neighborBack, ref loadedBack, ref backFromTL, xyWC, maxX, maxY, 'Z', baseWX, baseWY, baseWZ - 1);
                                backBits = cur & ~nbk[w];
                            }
                            else
                            {
                                backBits = cur & ~zSlabs[prevOff + w];
                            }
                            if (backBits != 0) totalFaces += BitOperations.PopCount(backBits);
                        }
                        if (!skipFront && !(z == maxZ - 1 && occludeFront))
                        {
                            ulong frontBits;
                            if (z == maxZ - 1)
                            {
                                var nf = GetNeighbor(ref neighborFront, ref loadedFront, ref frontFromTL, xyWC, maxX, maxY, 'Z', baseWX, baseWY, baseWZ + maxZ);
                                frontBits = cur & ~nf[w];
                            }
                            else
                            {
                                frontBits = cur & ~zSlabs[nextOff + w];
                            }
                            if (frontBits != 0) totalFaces += BitOperations.PopCount(frontBits);
                        }
                    }
                }

                int totalVerts = totalFaces * 4;
                bool useUShort = totalVerts <= 65535;
                byte[] vertBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 3));
                byte[] uvBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 2));
                uint[] indicesUIntBuffer = null; ushort[] indicesUShortBuffer = null;
                if (useUShort) indicesUShortBuffer = ArrayPool<ushort>.Shared.Rent(Math.Max(1, totalFaces * 6)); else indicesUIntBuffer = ArrayPool<uint>.Shared.Rent(Math.Max(1, totalFaces * 6));

                byte[] singleSolidUVConcat = hasSingleOpaque ? GetSingleSolidUVConcat(singleSolidId) : null;
                int faceIndex = 0;

                // EMIT PASS
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
                            if (!skipLeft && !(x == 0 && occludeLeft))
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
                            if (!skipRight && !(x == maxX - 1 && occludeRight))
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
                            if (!skipBottom && !(y == 0 && occludeBottom))
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
                            if (!skipTop && !(y == maxY - 1 && occludeTop))
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
                            if (!skipBack && !(z == 0 && occludeBack))
                            {
                                ulong backBits = (z == 0) ? (cur & ~neighborBack[w]) : (cur & ~zSlabs[prevOff + w]);
                                ulong bits = backBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                    int x2 = xyIndex / maxY; int y2 = xyIndex % maxY; // int li = LocalIndex(x2, y2, z, maxY, maxZ); block id not needed for uniform UVs
                                    WriteFaceSingle(Faces.BACK, (byte)x2, (byte)y2, (byte)z, ref faceIndex, singleSolidUVConcat, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                                    bits &= bits - 1;
                                }
                            }
                            if (!skipFront && !(z == maxZ - 1 && occludeFront))
                            {
                                ulong frontBits = (z == maxZ - 1) ? (cur & ~neighborFront[w]) : (cur & ~zSlabs[nextOff + w]);
                                ulong bits = frontBits; while (bits != 0)
                                {
                                    int t = BitOperations.TrailingZeroCount(bits);
                                    int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                                    int x2 = xyIndex / maxY; int y2 = xyIndex % maxY; // int li = LocalIndex(x2, y2, z, maxY, maxZ);
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
                            if (!skipLeft && !(x == 0 && occludeLeft))
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
                            if (!skipRight && !(x == maxX - 1 && occludeRight))
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
                            if (!skipBottom && !(y == 0 && occludeBottom))
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
                            if (!skipTop && !(y == maxY - 1 && occludeTop))
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
                            if (!skipBack && !(z == 0 && occludeBack))
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
                            if (!skipFront && !(z == maxZ - 1 && occludeFront))
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
                // Return pooled resources (only those allocated)
                if (!suppliedLocalBlocks) ArrayPool<ushort>.Shared.Return(localBlocks, false);
                ArrayPool<ulong>.Shared.Return(xSlabs, false);
                ArrayPool<ulong>.Shared.Return(ySlabs, false);
                ArrayPool<ulong>.Shared.Return(zSlabs, false);
                ArrayPool<bool>.Shared.Return(xSlabNonEmpty, false);
                ArrayPool<bool>.Shared.Return(ySlabNonEmpty, false);
                ArrayPool<bool>.Shared.Return(zSlabNonEmpty, false);
                // Return neighbor planes to correct pools (thread-local vs shared) if they were loaded here (not in fully-solid fast path)
                if (neighborLeft != null) { if (leftFromTL) TLPlaneReturn(neighborLeft); else ArrayPool<ulong>.Shared.Return(neighborLeft, false); }
                if (neighborRight != null) { if (rightFromTL) TLPlaneReturn(neighborRight); else ArrayPool<ulong>.Shared.Return(neighborRight, false); }
                if (neighborBottom != null) { if (bottomFromTL) TLPlaneReturn(neighborBottom); else ArrayPool<ulong>.Shared.Return(neighborBottom, false); }
                if (neighborTop != null) { if (topFromTL) TLPlaneReturn(neighborTop); else ArrayPool<ulong>.Shared.Return(neighborTop, false); }
                if (neighborBack != null) { if (backFromTL) TLPlaneReturn(neighborBack); else ArrayPool<ulong>.Shared.Return(neighborBack, false); }
                if (neighborFront != null) { if (frontFromTL) TLPlaneReturn(neighborFront); else ArrayPool<ulong>.Shared.Return(neighborFront, false); }
            }
        }
    }
}

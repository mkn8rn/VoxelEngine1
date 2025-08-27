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

        // Heuristic threshold (kept for identical slab merging)
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal BuildResult Build()
        {
            // Ensure UV LUT ready
            EnsureUvLut(atlas);

            // UNIFORM SINGLE-SOLID FAST PATH
            if (forceSingleSolid)
            {
                return BuildUniformSingleSolidFastPath();
            }

            // GENERAL PATH (optimized for dense chunks: many solids, few exposed faces)
            int yzPlaneBits = maxY * maxZ;
            int yzWC = (yzPlaneBits + 63) >> 6;
            int xzPlaneBits = maxX * maxZ; int xzWC = (xzPlaneBits + 63) >> 6;
            int xyPlaneBits = maxX * maxY; int xyWC = (xyPlaneBits + 63) >> 6;
            int voxelCount = maxX * maxY * maxZ;

            bool suppliedLocalBlocks = preFlattenedLocalBlocks != null;
            // For dense path we avoid allocating a full copy when not supplied: we'll fetch block IDs on-demand for visible faces.
            ushort[] localBlocks = suppliedLocalBlocks ? preFlattenedLocalBlocks : null;

            ulong[] xSlabs = ArrayPool<ulong>.Shared.Rent(maxX * yzWC);
            ulong[] ySlabs = ArrayPool<ulong>.Shared.Rent(maxY * xzWC);
            ulong[] zSlabs = ArrayPool<ulong>.Shared.Rent(maxZ * xyWC);
            bool[] xSlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxX);
            bool[] ySlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxY);
            bool[] zSlabNonEmpty = ArrayPool<bool>.Shared.Rent(maxZ);
            Array.Clear(xSlabs, 0, maxX * yzWC);
            Array.Clear(ySlabs, 0, maxY * xzWC);
            Array.Clear(zSlabs, 0, maxZ * xyWC);
            Array.Clear(xSlabNonEmpty, 0, maxX);
            Array.Clear(ySlabNonEmpty, 0, maxY);
            Array.Clear(zSlabNonEmpty, 0, maxZ);

            ulong[] neighborLeft = null, neighborRight = null, neighborBottom = null, neighborTop = null, neighborBack = null, neighborFront = null;
            bool loadedLeft = false, loadedRight = false, loadedBottom = false, loadedTop = false, loadedBack = false, loadedFront = false;
            bool leftFromTL = false, rightFromTL = false, bottomFromTL = false, topFromTL = false, backFromTL = false, frontFromTL = false;

            long solidCount = 0; int xNonEmptyCount = 0, yNonEmptyCount = 0, zNonEmptyCount = 0;
            int baseWX = (int)chunkWorldPosition.X; int baseWY = (int)chunkWorldPosition.Y; int baseWZ = (int)chunkWorldPosition.Z;

            try
            {
                // Pass 1: occupancy slabs only (no block id storage when not supplied)
                if (suppliedLocalBlocks)
                {
                    // Fast path with direct array access (no delegate call, no per-voxel branch)
                    for (int x = 0; x < maxX; x++)
                    {
                        int xSlabOffset = x * yzWC;
                        ulong slabAccumX = 0UL;
                        for (int z = 0; z < maxZ; z++)
                        {
                            int baseYZIndexZ = z * maxY;
                            int liBase = (x * maxZ + z) * maxY;
                            int xzIndex2 = x * maxZ + z; // invariant for y loop
                            int wxz2 = xzIndex2 >> 6;
                            ulong bxzMask = 1UL << (xzIndex2 & 63);
                            for (int y = 0; y < maxY; y++)
                            {
                                int li = liBase + y;
                                ushort bId = localBlocks[li];
                                if (bId == emptyBlock) continue;
                                solidCount++;
                                int yzIndex = baseYZIndexZ + y; int wyz = yzIndex >> 6; int byz = yzIndex & 63;
                                ulong bit = 1UL << byz;
                                xSlabs[xSlabOffset + wyz] |= bit; slabAccumX |= bit;
                                // y slab (XZ plane): index (x,z) constant per y iteration
                                ySlabs[y * xzWC + wxz2] |= bxzMask;
                                // z slab (XY plane)
                                int xyIndex2 = x * maxY + y; int wxy2 = xyIndex2 >> 6; int bxy2 = xyIndex2 & 63; zSlabs[z * xyWC + wxy2] |= 1UL << bxy2;
                                if (!ySlabNonEmpty[y]) { ySlabNonEmpty[y] = true; yNonEmptyCount++; }
                                if (!zSlabNonEmpty[z]) { zSlabNonEmpty[z] = true; zNonEmptyCount++; }
                            }
                        }
                        if (slabAccumX != 0) { xSlabNonEmpty[x] = true; xNonEmptyCount++; }
                    }
                }
                else
                {
                    // Fallback path using delegate fetch per voxel
                    for (int x = 0; x < maxX; x++)
                    {
                        int xSlabOffset = x * yzWC;
                        ulong slabAccumX = 0UL;
                        for (int z = 0; z < maxZ; z++)
                        {
                            int baseYZIndexZ = z * maxY;
                            int xzIndex2 = x * maxZ + z; int wxz2 = xzIndex2 >> 6; ulong bxzMask = 1UL << (xzIndex2 & 63);
                            for (int y = 0; y < maxY; y++)
                            {
                                ushort bId = getLocalBlock(x, y, z);
                                if (bId == emptyBlock) continue;
                                solidCount++;
                                int yzIndex = baseYZIndexZ + y; int wyz = yzIndex >> 6; int byz = yzIndex & 63; ulong bit = 1UL << byz;
                                xSlabs[xSlabOffset + wyz] |= bit; slabAccumX |= bit;
                                ySlabs[y * xzWC + wxz2] |= bxzMask;
                                int xyIndex2 = x * maxY + y; int wxy2 = xyIndex2 >> 6; int bxy2 = xyIndex2 & 63; zSlabs[z * xyWC + wxy2] |= 1UL << bxy2;
                                if (!ySlabNonEmpty[y]) { ySlabNonEmpty[y] = true; yNonEmptyCount++; }
                                if (!zSlabNonEmpty[z]) { zSlabNonEmpty[z] = true; zNonEmptyCount++; }
                            }
                        }
                        if (slabAccumX != 0) { xSlabNonEmpty[x] = true; xNonEmptyCount++; }
                    }
                }

                // Early empty
                if (solidCount == 0)
                {
                    return EmptyBuildResult(false);
                }

                if (TryFlagBasedFullOcclusionEarlyOut(false, out var flagOccluded))
                {
                    return flagOccluded;
                }

                if (TryFullySolidNeighborOcclusion(solidCount, voxelCount, yzWC, xzWC, xyWC, yzPlaneBits, xzPlaneBits, xyPlaneBits, baseWX, baseWY, baseWZ,
                                                   ref neighborLeft, ref neighborRight, ref neighborBottom, ref neighborTop, ref neighborBack, ref neighborFront,
                                                   ref loadedLeft, ref loadedRight, ref loadedBottom, ref loadedTop, ref loadedBack, ref loadedFront,
                                                   false, out var fullySolidOccluded))
                {
                    return fullySolidOccluded;
                }

                // Word-level face mask derivation arrays
                ulong[] leftFaceWords = ArrayPool<ulong>.Shared.Rent(maxX * yzWC);
                ulong[] rightFaceWords = ArrayPool<ulong>.Shared.Rent(maxX * yzWC);
                ulong[] bottomFaceWords = ArrayPool<ulong>.Shared.Rent(maxY * xzWC);
                ulong[] topFaceWords = ArrayPool<ulong>.Shared.Rent(maxY * xzWC);
                ulong[] backFaceWords = ArrayPool<ulong>.Shared.Rent(maxZ * xyWC);
                ulong[] frontFaceWords = ArrayPool<ulong>.Shared.Rent(maxZ * xyWC);
                Array.Clear(leftFaceWords, 0, leftFaceWords.Length);
                Array.Clear(rightFaceWords, 0, rightFaceWords.Length);
                Array.Clear(bottomFaceWords, 0, bottomFaceWords.Length);
                Array.Clear(topFaceWords, 0, topFaceWords.Length);
                Array.Clear(backFaceWords, 0, backFaceWords.Length);
                Array.Clear(frontFaceWords, 0, frontFaceWords.Length);

                int totalFaces = 0;

                // Helpers to lazily fetch neighbor plane
                ulong[] GetNeighborPlane(ref ulong[] arr, ref bool loaded, ref bool fromTL, int wc, int dimA, int dimB, char plane, int ox, int oy, int oz)
                {
                    if (loaded) return arr;
                    arr = TLPlaneRent(wc); fromTL = true; PrefetchNeighborPlane(getWorldBlockFast, getWorldBlock, arr, ox, oy, oz, dimA, dimB, wc, plane); loaded = true; return arr;
                }

                bool occludeLeft = faceNegX && nNegXPosX;
                bool occludeRight = facePosX && nPosXNegX;
                bool occludeBottom = faceNegY && nNegYPosY;
                bool occludeTop = facePosY && nPosYNegY;
                bool occludeBack = faceNegZ && nNegZPosZ;
                bool occludeFront = facePosZ && nPosZNegZ;

                // X faces
                for (int x = 0; x < maxX; x++)
                {
                    if (!xSlabNonEmpty[x]) continue;
                    int curOff = x * yzWC; int prevOff = (x - 1) * yzWC; int nextOff = (x + 1) * yzWC;
                    var leftPlane = x == 0 ? (occludeLeft ? null : GetNeighborPlane(ref neighborLeft, ref loadedLeft, ref leftFromTL, yzWC, maxY, maxZ, 'X', baseWX - 1, baseWY, baseWZ)) : null;
                    var rightPlane = x == maxX - 1 ? (occludeRight ? null : GetNeighborPlane(ref neighborRight, ref loadedRight, ref rightFromTL, yzWC, maxY, maxZ, 'X', baseWX + maxX, baseWY, baseWZ)) : null;
                    for (int w = 0; w < yzWC; w++)
                    {
                        ulong cur = xSlabs[curOff + w]; if (cur == 0) continue;
                        // LEFT
                        if (!(x == 0 && occludeLeft))
                        {
                            ulong neighbor = x == 0 ? (leftPlane != null ? leftPlane[w] : ulong.MaxValue) : xSlabs[prevOff + w];
                            ulong faces = cur & ~neighbor; if (faces != 0) { leftFaceWords[curOff + w] = faces; totalFaces += BitOperations.PopCount(faces); }
                        }
                        // RIGHT
                        if (!(x == maxX - 1 && occludeRight))
                        {
                            ulong neighbor = x == maxX - 1 ? (rightPlane != null ? rightPlane[w] : ulong.MaxValue) : xSlabs[nextOff + w];
                            ulong faces = cur & ~neighbor; if (faces != 0) { rightFaceWords[curOff + w] = faces; totalFaces += BitOperations.PopCount(faces); }
                        }
                    }
                }
                // Y faces
                for (int y = 0; y < maxY; y++)
                {
                    if (!ySlabNonEmpty[y]) continue;
                    int curOff = y * xzWC; int prevOff = (y - 1) * xzWC; int nextOff = (y + 1) * xzWC;
                    var bottomPlane = y == 0 ? (occludeBottom ? null : GetNeighborPlane(ref neighborBottom, ref loadedBottom, ref bottomFromTL, xzWC, maxX, maxZ, 'Y', baseWX, baseWY - 1, baseWZ)) : null;
                    var topPlane = y == maxY - 1 ? (occludeTop ? null : GetNeighborPlane(ref neighborTop, ref loadedTop, ref topFromTL, xzWC, maxX, maxZ, 'Y', baseWX, baseWY + maxY, baseWZ)) : null;
                    for (int w = 0; w < xzWC; w++)
                    {
                        ulong cur = ySlabs[curOff + w]; if (cur == 0) continue;
                        if (!(y == 0 && occludeBottom))
                        {
                            ulong neighbor = y == 0 ? (bottomPlane != null ? bottomPlane[w] : ulong.MaxValue) : ySlabs[prevOff + w];
                            ulong faces = cur & ~neighbor; if (faces != 0) { bottomFaceWords[curOff + w] = faces; totalFaces += BitOperations.PopCount(faces); }
                        }
                        if (!(y == maxY - 1 && occludeTop))
                        {
                            ulong neighbor = y == maxY - 1 ? (topPlane != null ? topPlane[w] : ulong.MaxValue) : ySlabs[nextOff + w];
                            ulong faces = cur & ~neighbor; if (faces != 0) { topFaceWords[curOff + w] = faces; totalFaces += BitOperations.PopCount(faces); }
                        }
                    }
                }
                // Z faces
                for (int z = 0; z < maxZ; z++)
                {
                    if (!zSlabNonEmpty[z]) continue;
                    int curOff = z * xyWC; int prevOff = (z - 1) * xyWC; int nextOff = (z + 1) * xyWC;
                    var backPlane = z == 0 ? (occludeBack ? null : GetNeighborPlane(ref neighborBack, ref loadedBack, ref backFromTL, xyWC, maxX, maxY, 'Z', baseWX, baseWY, baseWZ - 1)) : null;
                    var frontPlane = z == maxZ - 1 ? (occludeFront ? null : GetNeighborPlane(ref neighborFront, ref loadedFront, ref frontFromTL, xyWC, maxX, maxY, 'Z', baseWX, baseWY, baseWZ + maxZ)) : null;
                    for (int w = 0; w < xyWC; w++)
                    {
                        ulong cur = zSlabs[curOff + w]; if (cur == 0) continue;
                        if (!(z == 0 && occludeBack))
                        {
                            ulong neighbor = z == 0 ? (backPlane != null ? backPlane[w] : ulong.MaxValue) : zSlabs[prevOff + w];
                            ulong faces = cur & ~neighbor; if (faces != 0) { backFaceWords[curOff + w] = faces; totalFaces += BitOperations.PopCount(faces); }
                        }
                        if (!(z == maxZ - 1 && occludeFront))
                        {
                            ulong neighbor = z == maxZ - 1 ? (frontPlane != null ? frontPlane[w] : ulong.MaxValue) : zSlabs[nextOff + w];
                            ulong faces = cur & ~neighbor; if (faces != 0) { frontFaceWords[curOff + w] = faces; totalFaces += BitOperations.PopCount(faces); }
                        }
                    }
                }

                int totalVerts = totalFaces * 4;
                bool useUShort = totalVerts <= 65535;
                byte[] vertBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 3));
                byte[] uvBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 2));
                ushort[] indicesUShortBuffer = useUShort ? ArrayPool<ushort>.Shared.Rent(Math.Max(1, totalFaces * 6)) : null;
                uint[] indicesUIntBuffer = useUShort ? null : ArrayPool<uint>.Shared.Rent(Math.Max(1, totalFaces * 6));

                int faceIndex = 0;
                // Emit faces by scanning face word arrays (only exposed bits)
                for (int x = 0; x < maxX; x++)
                {
                    int curOff = x * yzWC; if (!xSlabNonEmpty[x]) continue;
                    for (int w = 0; w < yzWC; w++)
                    {
                        ulong left = leftFaceWords[curOff + w];
                        while (left != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(left);
                            int yzIndex = (w << 6) + bit; if (yzIndex >= yzPlaneBits) break;
                            int z = yzIndex / maxY; int y = yzIndex % maxY; int li = (x * maxZ + z) * maxY + y;
                            ushort id = suppliedLocalBlocks ? localBlocks[li] : getLocalBlock(x, y, z);
                            WriteFaceMulti(id, Faces.LEFT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                            left &= left - 1;
                        }
                        ulong right = rightFaceWords[curOff + w];
                        while (right != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(right);
                            int yzIndex = (w << 6) + bit; if (yzIndex >= yzPlaneBits) break;
                            int z = yzIndex / maxY; int y = yzIndex % maxY; int li = (x * maxZ + z) * maxY + y;
                            ushort id = suppliedLocalBlocks ? localBlocks[li] : getLocalBlock(x, y, z);
                            WriteFaceMulti(id, Faces.RIGHT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                            right &= right - 1;
                        }
                    }
                }
                for (int y = 0; y < maxY; y++)
                {
                    int curOff = y * xzWC; if (!ySlabNonEmpty[y]) continue;
                    for (int w = 0; w < xzWC; w++)
                    {
                        ulong bottom = bottomFaceWords[curOff + w];
                        while (bottom != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(bottom);
                            int xzIndex = (w << 6) + bit; if (xzIndex >= xzPlaneBits) break;
                            int x = xzIndex / maxZ; int z = xzIndex % maxZ; int li = (x * maxZ + z) * maxY + y;
                            ushort id = suppliedLocalBlocks ? localBlocks[li] : getLocalBlock(x, y, z);
                            WriteFaceMulti(id, Faces.BOTTOM, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                            bottom &= bottom - 1;
                        }
                        ulong top = topFaceWords[curOff + w];
                        while (top != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(top);
                            int xzIndex = (w << 6) + bit; if (xzIndex >= xzPlaneBits) break;
                            int x = xzIndex / maxZ; int z = xzIndex % maxZ; int li = (x * maxZ + z) * maxY + y;
                            ushort id = suppliedLocalBlocks ? localBlocks[li] : getLocalBlock(x, y, z);
                            WriteFaceMulti(id, Faces.TOP, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                            top &= top - 1;
                        }
                    }
                }
                for (int z = 0; z < maxZ; z++)
                {
                    int curOff = z * xyWC; if (!zSlabNonEmpty[z]) continue;
                    for (int w = 0; w < xyWC; w++)
                    {
                        ulong back = backFaceWords[curOff + w];
                        while (back != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(back);
                            int xyIndex = (w << 6) + bit; if (xyIndex >= xyPlaneBits) break;
                            int x = xyIndex / maxY; int y = xyIndex % maxY; int li = (x * maxZ + z) * maxY + y;
                            ushort id = suppliedLocalBlocks ? localBlocks[li] : getLocalBlock(x, y, z);
                            WriteFaceMulti(id, Faces.BACK, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                            back &= back - 1;
                        }
                        ulong front = frontFaceWords[curOff + w];
                        while (front != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(front);
                            int xyIndex = (w << 6) + bit; if (xyIndex >= xyPlaneBits) break;
                            int x = xyIndex / maxY; int y = xyIndex % maxY; int li = (x * maxZ + z) * maxY + y;
                            ushort id = suppliedLocalBlocks ? localBlocks[li] : getLocalBlock(x, y, z);
                            WriteFaceMulti(id, Faces.FRONT, (byte)x, (byte)y, (byte)z, ref faceIndex, emptyBlock, atlas, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
                            front &= front - 1;
                        }
                    }
                }

                // Return face word arrays
                ArrayPool<ulong>.Shared.Return(leftFaceWords, false);
                ArrayPool<ulong>.Shared.Return(rightFaceWords, false);
                ArrayPool<ulong>.Shared.Return(bottomFaceWords, false);
                ArrayPool<ulong>.Shared.Return(topFaceWords, false);
                ArrayPool<ulong>.Shared.Return(backFaceWords, false);
                ArrayPool<ulong>.Shared.Return(frontFaceWords, false);

                return new BuildResult
                {
                    UseUShort = useUShort,
                    HasSingleOpaque = false,
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
                ArrayPool<ulong>.Shared.Return(xSlabs, false);
                ArrayPool<ulong>.Shared.Return(ySlabs, false);
                ArrayPool<ulong>.Shared.Return(zSlabs, false);
                ArrayPool<bool>.Shared.Return(xSlabNonEmpty, false);
                ArrayPool<bool>.Shared.Return(ySlabNonEmpty, false);
                ArrayPool<bool>.Shared.Return(zSlabNonEmpty, false);
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

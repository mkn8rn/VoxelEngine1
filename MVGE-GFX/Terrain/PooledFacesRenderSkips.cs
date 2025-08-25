using System;
using System.Buffers;
using System.Numerics;
using MVGE_GFX.Models;
using MVGE_INF.Models.Terrain;

namespace MVGE_GFX.Terrain
{
    public partial class PooledFacesRender
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

        // Determine plane emission (if plane and opposing neighbor plane both solid we skip entirely)
        // Returns false if nothing is visible (all planes skipped)
        private bool DetermineSingleSolidPlaneEmission(out bool emitLeft, out bool emitRight,
                                                       out bool emitBottom, out bool emitTop,
                                                       out bool emitBack, out bool emitFront)
        {
            emitLeft = !(faceNegX && nNegXPosX);
            emitRight = !(facePosX && nPosXNegX);
            emitBottom = !(faceNegY && nNegYPosY);
            emitTop = !(facePosY && nPosYNegY);
            emitBack = !(faceNegZ && nNegZPosZ);
            emitFront = !(facePosZ && nPosZNegZ);
            // Early if nothing visible
            return (emitLeft || emitRight || emitBottom || emitTop || emitBack || emitFront);
        }

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

        // Need all six neighbor planes and all set to consider occluded
        // Returns true if fully solid chunk is also fully occluded by neighbors (and supplies empty result)
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
            if (solidCount != voxelCount) return false;

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

        private static void ClearUlongsStatic(ulong[] arr, int len)
        {
            for (int i = 0; i < len; i++) arr[i] = 0UL;
        }

        // Extracted uniform single-solid fast path (lines 340-586 original Build)
        private BuildResult BuildUniformSingleSolidFastPath()
        {
            // Fully occluded early exit (all our faces + neighbor opposing faces solid)
            if (IsFullyOccludedByFlags())
            {
                return EmptyBuildResult(true);
            }

            // Determine plane emission (if plane and opposing neighbor plane both solid we skip entirely)
            bool emitLeft, emitRight, emitBottom, emitTop, emitBack, emitFront;
            if (!DetermineSingleSolidPlaneEmission(out emitLeft, out emitRight, out emitBottom, out emitTop, out emitBack, out emitFront))
            {
                return EmptyBuildResult(true);
            }

            // Collect merged quads per plane (face, origin, size)
            var merged = new List<(Faces face, byte x, byte y, byte z, byte h, byte w)>(32);

            // UV concat for the block
            byte[] uvConcat = GetSingleSolidUVConcat(forceSingleSolidBlockId) ?? new byte[48];

            // For each plane we build visibility mask from neighbor emptiness.
            // NOTE: We intentionally stretch texture over merged quad (no tiling) for performance.
            // Left / Right planes (Y x Z grid)
            if (emitLeft)
            {
                int rows = maxY, cols = maxZ;
                Span<bool> mask = stackalloc bool[256]; // supports up to 16x16; for larger sizes fallback to heap
                if (rows * cols > 256) mask = new bool[rows * cols];
                for (int y = 0; y < maxY; y++)
                    for (int z = 0; z < maxZ; z++)
                        mask[y * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X - 1, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z + z) == emptyBlock;
                var rects = new List<(int r,int c,int h,int w)>();
                // Quick full plane check
                if (rects.Capacity == 0) { }
                bool full = true;
                for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full)
                {
                    merged.Add((Faces.LEFT, 0, 0, 0, (byte)rows, (byte)cols));
                }
                else
                {
                    GreedyRectangles(mask, rows, cols, rects);
                    foreach (var r in rects)
                        merged.Add((Faces.LEFT, 0, (byte)r.r, (byte)r.c, (byte)r.h, (byte)r.w));
                }
            }
            if (emitRight)
            {
                int rows = maxY, cols = maxZ;
                Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int y = 0; y < maxY; y++)
                    for (int z = 0; z < maxZ; z++)
                        mask[y * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X + maxX, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z + z) == emptyBlock;
                var rects = new List<(int r,int c,int h,int w)>();
                bool full = true;
                for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full)
                {
                    merged.Add((Faces.RIGHT, (byte)(maxX - 1), 0, 0, (byte)rows, (byte)cols));
                }
                else
                {
                    GreedyRectangles(mask, rows, cols, rects);
                    foreach (var r in rects)
                        merged.Add((Faces.RIGHT, (byte)(maxX - 1), (byte)r.r, (byte)r.c, (byte)r.h, (byte)r.w));
                }
            }
            // Bottom / Top planes (X x Z grid)
            if (emitBottom)
            {
                int rows = maxX, cols = maxZ;
                Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++)
                    for (int z = 0; z < maxZ; z++)
                        mask[x * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y - 1, (int)chunkWorldPosition.Z + z) == emptyBlock;
                var rects = new List<(int r,int c,int h,int w)>();
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full)
                    merged.Add((Faces.BOTTOM, 0, 0, 0, (byte)rows, (byte)cols));
                else
                {
                    GreedyRectangles(mask, rows, cols, rects);
                    foreach (var r in rects)
                        merged.Add((Faces.BOTTOM, (byte)r.r, 0, (byte)r.c, (byte)r.h, (byte)r.w));
                }
            }
            if (emitTop)
            {
                int rows = maxX, cols = maxZ;
                Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++)
                    for (int z = 0; z < maxZ; z++)
                        mask[x * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y + maxY, (int)chunkWorldPosition.Z + z) == emptyBlock;
                var rects = new List<(int r,int c,int h,int w)>();
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full)
                    merged.Add((Faces.TOP, 0, (byte)(maxY - 1), 0, (byte)rows, (byte)cols));
                else
                {
                    GreedyRectangles(mask, rows, cols, rects);
                    foreach (var r in rects)
                        merged.Add((Faces.TOP, (byte)r.r, (byte)(maxY - 1), (byte)r.c, (byte)r.h, (byte)r.w));
                }
            }
            // Back / Front planes (X x Y grid)
            if (emitBack)
            {
                int rows = maxX, cols = maxY;
                Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++)
                    for (int y = 0; y < maxY; y++)
                        mask[x * maxY + y] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z - 1) == emptyBlock;
                var rects = new List<(int r,int c,int h,int w)>();
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full)
                    merged.Add((Faces.BACK, 0, 0, 0, (byte)rows, (byte)cols));
                else
                {
                    GreedyRectangles(mask, rows, cols, rects);
                    foreach (var r in rects)
                        merged.Add((Faces.BACK, (byte)r.r, (byte)r.c, 0, (byte)r.h, (byte)r.w));
                }
            }
            if (emitFront)
            {
                int rows = maxX, cols = maxY;
                Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++)
                    for (int y = 0; y < maxY; y++)
                        mask[x * maxY + y] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z + maxZ) == emptyBlock;
                var rects = new List<(int r,int c,int h,int w)>();
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full)
                    merged.Add((Faces.FRONT, 0, 0, (byte)(maxZ - 1), (byte)rows, (byte)cols));
                else
                {
                    GreedyRectangles(mask, rows, cols, rects);
                    foreach (var r in rects)
                        merged.Add((Faces.FRONT, (byte)r.r, (byte)r.c, (byte)(maxZ - 1), (byte)r.h, (byte)r.w));
                }
            }

            int faceCount = merged.Count;
            int totalVerts = faceCount * 4;
            bool useUShort = totalVerts <= 65535;
            byte[] vertBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 3));
            byte[] uvBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, totalVerts * 2));
            ushort[] indicesUShortBuffer = useUShort ? ArrayPool<ushort>.Shared.Rent(Math.Max(1, faceCount * 6)) : null;
            uint[] indicesUIntBuffer = useUShort ? null : ArrayPool<uint>.Shared.Rent(Math.Max(1, faceCount * 6));

            // Emit merged quads
            int faceIndex = 0;
            foreach (var mf in merged)
            {
                // Build stretched quad vertices per face orientation
                int baseVertexIndex = faceIndex * 4;
                int vertexByteOffset = baseVertexIndex * 3;
                int uvByteOffset = baseVertexIndex * 2;
                int indexOffset = faceIndex * 6;

                // Helper local to write indices
                void WriteIdx()
                {
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
                }

                byte x = mf.x, y = mf.y, z = mf.z, h = mf.h, w = mf.w;
                switch (mf.face)
                {
                    case Faces.LEFT:
                    case Faces.RIGHT:
                    {
                        // h-> along Y, w-> along Z
                        byte xConst = x;
                        // Vertex order matches existing single face winding
                        // (0) (xConst, y, z)
                        // (1) (xConst, y + h, z)
                        // (2) (xConst, y + h, z + w)
                        // (3) (xConst, y, z + w)
                        vertBuffer[vertexByteOffset + 0] = xConst; vertBuffer[vertexByteOffset + 1] = y; vertBuffer[vertexByteOffset + 2] = z;
                        vertBuffer[vertexByteOffset + 3] = xConst; vertBuffer[vertexByteOffset + 4] = (byte)(y + h); vertBuffer[vertexByteOffset + 5] = z;
                        vertBuffer[vertexByteOffset + 6] = xConst; vertBuffer[vertexByteOffset + 7] = (byte)(y + h); vertBuffer[vertexByteOffset + 8] = (byte)(z + w);
                        vertBuffer[vertexByteOffset + 9] = xConst; vertBuffer[vertexByteOffset +10] = y; vertBuffer[vertexByteOffset +11] = (byte)(z + w);
                        break;
                    }
                    case Faces.BOTTOM:
                    case Faces.TOP:
                    {
                        // h-> along X, w-> along Z (we stored r as X, w as width along Z)
                        byte yConst = y;
                        vertBuffer[vertexByteOffset + 0] = x; vertBuffer[vertexByteOffset + 1] = yConst; vertBuffer[vertexByteOffset + 2] = z;
                        vertBuffer[vertexByteOffset + 3] = (byte)(x + h); vertBuffer[vertexByteOffset + 4] = yConst; vertBuffer[vertexByteOffset + 5] = z;
                        vertBuffer[vertexByteOffset + 6] = (byte)(x + h); vertBuffer[vertexByteOffset + 7] = yConst; vertBuffer[vertexByteOffset + 8] = (byte)(z + w);
                        vertBuffer[vertexByteOffset + 9] = x; vertBuffer[vertexByteOffset +10] = yConst; vertBuffer[vertexByteOffset +11] = (byte)(z + w);
                        break;
                    }
                    case Faces.BACK:
                    case Faces.FRONT:
                    {
                        // h-> along X, w-> along Y (r as X, w as height along Y)
                        byte zConst = z;
                        vertBuffer[vertexByteOffset + 0] = x; vertBuffer[vertexByteOffset + 1] = y; vertBuffer[vertexByteOffset + 2] = zConst;
                        vertBuffer[vertexByteOffset + 3] = (byte)(x + h); vertBuffer[vertexByteOffset + 4] = y; vertBuffer[vertexByteOffset + 5] = zConst;
                        vertBuffer[vertexByteOffset + 6] = (byte)(x + h); vertBuffer[vertexByteOffset + 7] = (byte)(y + w); vertBuffer[vertexByteOffset + 8] = zConst;
                        vertBuffer[vertexByteOffset + 9] = x; vertBuffer[vertexByteOffset +10] = (byte)(y + w); vertBuffer[vertexByteOffset +11] = zConst;
                        break;
                    }
                }
                // UVs (same 4 corners stretched)
                int uvOff = ((int)mf.face) * 8;
                for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = uvConcat[uvOff + i];
                WriteIdx();
                faceIndex++;
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
                IndicesUsed = faceCount * 6
            };
        }
    }
}

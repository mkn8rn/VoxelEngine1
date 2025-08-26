using System;
using System.Buffers;
using MVGE_GFX.Models;
using MVGE_INF.Models.Terrain;
using OpenTK.Mathematics;
using MVGE_GFX.Textures;

namespace MVGE_GFX.Terrain
{
    // Sparse-focused renderer: O(nonEmpty) face discovery with optional microcell (8x8x8) partitioning
    internal sealed partial class SparseChunkRender
    {
        internal struct BuildResult
        {
            public bool UseUShort;
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
        private readonly ushort[] flatBlocks; // x-major, z, y
        private readonly Func<int,int,int,ushort> getWorldBlock;
        private readonly BlockTextureAtlas atlas;

        // Neighbor face solidity flags (still useful to skip some boundary world calls)
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;

        public SparseChunkRender(
            Vector3 chunkWorldPosition,
            int maxX, int maxY, int maxZ,
            ushort emptyBlock,
            ushort[] flatBlocks,
            Func<int,int,int,ushort> worldGetter,
            BlockTextureAtlas atlas,
            bool faceNegX, bool facePosX,
            bool faceNegY, bool facePosY,
            bool faceNegZ, bool facePosZ,
            bool nNegXPosX, bool nPosXNegX,
            bool nNegYPosY, bool nPosYNegY,
            bool nNegZPosZ, bool nPosZNegZ)
        {
            this.chunkWorldPosition = chunkWorldPosition;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            this.emptyBlock = emptyBlock;
            this.flatBlocks = flatBlocks;
            getWorldBlock = worldGetter;
            this.atlas = atlas;
            this.faceNegX = faceNegX; this.facePosX = facePosX; this.faceNegY = faceNegY; this.facePosY = facePosY; this.faceNegZ = faceNegZ; this.facePosZ = facePosZ;
            this.nNegXPosX = nNegXPosX; this.nPosXNegX = nPosXNegX; this.nNegYPosY = nNegYPosY; this.nPosYNegY = nPosYNegY; this.nNegZPosZ = nNegZPosZ; this.nPosZNegZ = nPosZNegZ;
        }

        private static readonly byte FACE_LEFT   = 1 << 0;
        private static readonly byte FACE_RIGHT  = 1 << 1;
        private static readonly byte FACE_TOP    = 1 << 2;
        private static readonly byte FACE_BOTTOM = 1 << 3;
        private static readonly byte FACE_FRONT  = 1 << 4;
        private static readonly byte FACE_BACK   = 1 << 5;

        private const int MICRO_CELL_SIZE = 8;              // 8x8x8 = 512 voxels
        private const int MICRO_CELL_BITS_PER_CELL = 512;   // occupancy bits per microcell
        private const int MICRO_CELL_WORDS = MICRO_CELL_BITS_PER_CELL / 64; // 8 ulongs

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void Decode(int li, int maxY, int maxZ, out int x, out int y, out int z)
        {
            y = li % maxY;
            int t = li / maxY;
            z = t % maxZ;
            x = t / maxZ;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool BitTest(ulong[] bits, int idx)
        {
            int w = idx >> 6;
            int b = idx & 63;
            return (bits[w] & (1UL << b)) != 0UL;
        }

        public BuildResult Build()
        {
            int voxelCount = maxX * maxY * maxZ;
            if (voxelCount == 0)
            {
                return new BuildResult
                {
                    UseUShort = true,
                    VertBuffer = Array.Empty<byte>(),
                    UVBuffer = Array.Empty<byte>(),
                    IndicesUShortBuffer = Array.Empty<ushort>(),
                    IndicesUIntBuffer = null,
                    VertBytesUsed = 0,
                    UVBytesUsed = 0,
                    IndicesUsed = 0
                };
            }

            // Global occupancy bitset (still used for inter-cell neighbor tests)
            int globalWordCount = (voxelCount + 63) >> 6;
            ulong[] globalOccupancy = ArrayPool<ulong>.Shared.Rent(globalWordCount);
            Array.Clear(globalOccupancy, 0, globalWordCount);

            // ---- Microcell grid layout ----
            int cellsX = (maxX + MICRO_CELL_SIZE - 1) / MICRO_CELL_SIZE;
            int cellsY = (maxY + MICRO_CELL_SIZE - 1) / MICRO_CELL_SIZE;
            int cellsZ = (maxZ + MICRO_CELL_SIZE - 1) / MICRO_CELL_SIZE;
            int microCellCount = cellsX * cellsY * cellsZ;

            // Per-cell counts (first pass)
            int[] cellVoxelCounts = ArrayPool<int>.Shared.Rent(microCellCount);
            Array.Clear(cellVoxelCounts, 0, microCellCount);

            // First pass – count per microcell & global occupancy bits
            int solidTotal = 0;
            for (int li = 0; li < voxelCount; li++)
            {
                ushort id = flatBlocks[li];
                if (id == emptyBlock) continue;
                // global occupancy
                globalOccupancy[li >> 6] |= 1UL << (li & 63);
                solidTotal++;
                // cell index
                Decode(li, maxY, maxZ, out int vx, out int vy, out int vz);
                int cx = vx / MICRO_CELL_SIZE;
                int cy = vy / MICRO_CELL_SIZE;
                int cz = vz / MICRO_CELL_SIZE;
                int cellIndex = (cx * cellsZ + cz) * cellsY + cy; // x-major, then z, then y (match flat ordering logic)
                cellVoxelCounts[cellIndex]++;
            }

            if (solidTotal == 0)
            {
                CleanupEarly();
                return new BuildResult
                {
                    UseUShort = true,
                    VertBuffer = Array.Empty<byte>(),
                    UVBuffer = Array.Empty<byte>(),
                    IndicesUShortBuffer = Array.Empty<ushort>(),
                    IndicesUIntBuffer = null,
                    VertBytesUsed = 0,
                    UVBytesUsed = 0,
                    IndicesUsed = 0
                };
            }

            // Build prefix sums for per-cell storage (stable ordering inside cell not required)
            int[] cellStart = ArrayPool<int>.Shared.Rent(microCellCount + 1);
            int running = 0;
            for (int ci = 0; ci < microCellCount; ci++)
            {
                cellStart[ci] = running;
                running += cellVoxelCounts[ci];
            }
            cellStart[microCellCount] = running; // sentinel

            // Allocate compact contiguous arrays for occupied linear indices and block IDs (grouped by cell)
            int[] occupiedLinearIndices = ArrayPool<int>.Shared.Rent(solidTotal);
            ushort[] occupiedBlocks = ArrayPool<ushort>.Shared.Rent(solidTotal);

            // We'll reuse cellVoxelCounts as a write cursor array: zero then increment.
            Array.Clear(cellVoxelCounts, 0, microCellCount);

            // Second pass – fill grouped arrays
            for (int li = 0; li < voxelCount; li++)
            {
                if (flatBlocks[li] == emptyBlock) continue;
                Decode(li, maxY, maxZ, out int vx, out int vy, out int vz);
                int cx = vx / MICRO_CELL_SIZE;
                int cy = vy / MICRO_CELL_SIZE;
                int cz = vz / MICRO_CELL_SIZE;
                int cellIndex = (cx * cellsZ + cz) * cellsY + cy;
                int writePos = cellStart[cellIndex] + cellVoxelCounts[cellIndex]++;
                occupiedLinearIndices[writePos] = li;
                occupiedBlocks[writePos] = flatBlocks[li];
            }

            // Face mask array (aligned with occupied* arrays)
            byte[] faceMasks = ArrayPool<byte>.Shared.Rent(solidTotal);
            int totalFaces = 0;

            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;

            // Iterate microcells; within each cell process only its voxels for masks
            for (int ci = 0; ci < microCellCount; ci++)
            {
                int start = cellStart[ci];
                int end = cellStart[ci + 1];
                if (start == end) continue; // empty cell
                for (int idx = start; idx < end; idx++)
                {
                    int li = occupiedLinearIndices[idx];
                    Decode(li, maxY, maxZ, out int x, out int y, out int z);
                    byte mask = 0;

                    // LEFT
                    if (x == 0)
                    {
                        if (!(faceNegX && nNegXPosX))
                        {
                            ushort nb = getWorldBlock(baseWX - 1, baseWY + y, baseWZ + z);
                            if (nb == emptyBlock) mask |= FACE_LEFT;
                        }
                    }
                    else
                    {
                        int liN = li - (maxZ * maxY);
                        if (!BitTest(globalOccupancy, liN)) mask |= FACE_LEFT;
                    }

                    // RIGHT
                    if (x == maxX - 1)
                    {
                        if (!(facePosX && nPosXNegX))
                        {
                            ushort nb = getWorldBlock(baseWX + maxX, baseWY + y, baseWZ + z);
                            if (nb == emptyBlock) mask |= FACE_RIGHT;
                        }
                    }
                    else
                    {
                        int liN = li + (maxZ * maxY);
                        if (!BitTest(globalOccupancy, liN)) mask |= FACE_RIGHT;
                    }

                    // BOTTOM
                    if (y == 0)
                    {
                        if (!(faceNegY && nNegYPosY))
                        {
                            ushort nb = getWorldBlock(baseWX + x, baseWY - 1, baseWZ + z);
                            if (nb == emptyBlock) mask |= FACE_BOTTOM;
                        }
                    }
                    else if (!BitTest(globalOccupancy, li - 1)) mask |= FACE_BOTTOM;

                    // TOP
                    if (y == maxY - 1)
                    {
                        if (!(facePosY && nPosYNegY))
                        {
                            ushort nb = getWorldBlock(baseWX + x, baseWY + maxY, baseWZ + z);
                            if (nb == emptyBlock) mask |= FACE_TOP;
                        }
                    }
                    else if (!BitTest(globalOccupancy, li + 1)) mask |= FACE_TOP;

                    // BACK (negative Z)
                    if (z == 0)
                    {
                        if (!(faceNegZ && nNegZPosZ))
                        {
                            ushort nb = getWorldBlock(baseWX + x, baseWY + y, baseWZ - 1);
                            if (nb == emptyBlock) mask |= FACE_BACK;
                        }
                    }
                    else
                    {
                        int liN = li - maxY;
                        if (!BitTest(globalOccupancy, liN)) mask |= FACE_BACK;
                    }

                    // FRONT (positive Z)
                    if (z == maxZ - 1)
                    {
                        if (!(facePosZ && nPosZNegZ))
                        {
                            ushort nb = getWorldBlock(baseWX + x, baseWY + y, baseWZ + maxZ);
                            if (nb == emptyBlock) mask |= FACE_FRONT;
                        }
                    }
                    else
                    {
                        int liN = li + maxY;
                        if (!BitTest(globalOccupancy, liN)) mask |= FACE_FRONT;
                    }

                    if (mask != 0)
                    {
                        int faces = ((mask & FACE_LEFT) != 0 ? 1 : 0) +
                                    ((mask & FACE_RIGHT) != 0 ? 1 : 0) +
                                    ((mask & FACE_TOP) != 0 ? 1 : 0) +
                                    ((mask & FACE_BOTTOM) != 0 ? 1 : 0) +
                                    ((mask & FACE_FRONT) != 0 ? 1 : 0) +
                                    ((mask & FACE_BACK) != 0 ? 1 : 0);
                        totalFaces += faces;
                    }
                    faceMasks[idx] = mask;
                }
            }

            if (totalFaces == 0)
            {
                CleanupAll();
                return new BuildResult
                {
                    UseUShort = true,
                    VertBuffer = Array.Empty<byte>(),
                    UVBuffer = Array.Empty<byte>(),
                    IndicesUShortBuffer = Array.Empty<ushort>(),
                    IndicesUIntBuffer = null,
                    VertBytesUsed = 0,
                    UVBytesUsed = 0,
                    IndicesUsed = 0
                };
            }

            int totalVerts = totalFaces * 4;
            bool useUShort = totalVerts <= 65535;
            byte[] vertBuf = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
            byte[] uvBuf = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
            ushort[] idxU16 = useUShort ? ArrayPool<ushort>.Shared.Rent(totalFaces * 6) : null;
            uint[] idxU32 = useUShort ? null : ArrayPool<uint>.Shared.Rent(totalFaces * 6);

            int faceIndex = 0;
            for (int ci = 0; ci < microCellCount; ci++)
            {
                int start = cellStart[ci];
                int end = cellStart[ci + 1];
                if (start == end) continue;
                for (int idx = start; idx < end; idx++)
                {
                    byte mask = faceMasks[idx];
                    if (mask == 0) continue;
                    int li = occupiedLinearIndices[idx];
                    Decode(li, maxY, maxZ, out int x, out int y, out int z);
                    ushort block = occupiedBlocks[idx];
                    Emit(mask, block, (byte)x, (byte)y, (byte)z, ref faceIndex, vertBuf, uvBuf, useUShort, idxU16, idxU32);
                }
            }

            var result = new BuildResult
            {
                UseUShort = useUShort,
                VertBuffer = vertBuf,
                UVBuffer = uvBuf,
                IndicesUShortBuffer = idxU16,
                IndicesUIntBuffer = idxU32,
                VertBytesUsed = totalVerts * 3,
                UVBytesUsed = totalVerts * 2,
                IndicesUsed = totalFaces * 6
            };

            CleanupAll();
            return result;

            // ----- Local cleanup helpers -----
            void CleanupEarly()
            {
                ArrayPool<ulong>.Shared.Return(globalOccupancy, false);
                ArrayPool<int>.Shared.Return(cellVoxelCounts, false);
            }
            void CleanupAll()
            {
                ArrayPool<ulong>.Shared.Return(globalOccupancy, false);
                ArrayPool<int>.Shared.Return(cellVoxelCounts, false);
                ArrayPool<int>.Shared.Return(cellStart, false);
                ArrayPool<int>.Shared.Return(occupiedLinearIndices, false);
                ArrayPool<ushort>.Shared.Return(occupiedBlocks, false);
                ArrayPool<byte>.Shared.Return(faceMasks, false);
            }
        }

        private void Emit(byte mask, ushort block,
                          byte x, byte y, byte z,
                          ref int faceIndex,
                          byte[] vertBuffer,
                          byte[] uvBuffer,
                          bool useUShort,
                          ushort[] indicesUShortBuffer,
                          uint[] indicesUIntBuffer)
        {
            if ((mask & FACE_LEFT) != 0)  WriteFace(block, Faces.LEFT,   x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_RIGHT) != 0) WriteFace(block, Faces.RIGHT,  x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_TOP) != 0)   WriteFace(block, Faces.TOP,    x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_BOTTOM) != 0)WriteFace(block, Faces.BOTTOM, x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_FRONT) != 0) WriteFace(block, Faces.FRONT,  x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_BACK) != 0)  WriteFace(block, Faces.BACK,   x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
        }

        private void WriteFace(ushort block,
                               Faces face,
                               byte bx, byte by, byte bz,
                               ref int faceIndex,
                               byte[] vertBuffer,
                               byte[] uvBuffer,
                               bool useUShort,
                               ushort[] indicesUShortBuffer,
                               uint[] indicesUIntBuffer)
        {
            int baseVertexIndex = faceIndex * 4;
            int vOff = baseVertexIndex * 3;
            int uvOff = baseVertexIndex * 2;
            int iOff = faceIndex * 6;

            var verts = RawFaceData.rawVertexData[face];
            for (int i = 0; i < 4; i++)
            {
                vertBuffer[vOff + i * 3 + 0] = (byte)(verts[i].x + bx);
                vertBuffer[vOff + i * 3 + 1] = (byte)(verts[i].y + by);
                vertBuffer[vOff + i * 3 + 2] = (byte)(verts[i].z + bz);
            }

            var uvList = atlas.GetBlockUVs(block, face);
            for (int i = 0; i < 4; i++)
            {
                uvBuffer[uvOff + i * 2 + 0] = uvList[i].x;
                uvBuffer[uvOff + i * 2 + 1] = uvList[i].y;
            }

            if (useUShort)
            {
                indicesUShortBuffer[iOff + 0] = (ushort)(baseVertexIndex + 0);
                indicesUShortBuffer[iOff + 1] = (ushort)(baseVertexIndex + 1);
                indicesUShortBuffer[iOff + 2] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[iOff + 3] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[iOff + 4] = (ushort)(baseVertexIndex + 3);
                indicesUShortBuffer[iOff + 5] = (ushort)(baseVertexIndex + 0);
            }
            else
            {
                indicesUIntBuffer[iOff + 0] = (uint)(baseVertexIndex + 0);
                indicesUIntBuffer[iOff + 1] = (uint)(baseVertexIndex + 1);
                indicesUIntBuffer[iOff + 2] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[iOff + 3] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[iOff + 4] = (uint)(baseVertexIndex + 3);
                indicesUIntBuffer[iOff + 5] = (uint)(baseVertexIndex + 0);
            }
            faceIndex++;
        }
    }
}

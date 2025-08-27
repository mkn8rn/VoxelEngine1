using System;
using System.Buffers;
using MVGE_GFX.Models;
using MVGE_INF.Models.Terrain;
using OpenTK.Mathematics;
using MVGE_GFX.Textures;
using MVGE_INF.Models.Generation;

namespace MVGE_GFX.Terrain
{
    // Sparse-focused renderer: O(visible) face discovery (after gating by exposure in ChunkRender)
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
        private readonly BlockTextureAtlas atlas;

        // Face solidity flags (self + neighbor opposing)
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;

        // Neighbor plane caches (null => treat as empty outside so faces will emit, matching previous conservative behavior)
        private readonly ulong[] nPlaneNegX; // neighbor at -X (+X face of neighbor)
        private readonly ulong[] nPlanePosX; // neighbor at +X (-X face)
        private readonly ulong[] nPlaneNegY; // neighbor at -Y (+Y face)
        private readonly ulong[] nPlanePosY; // neighbor at +Y (-Y face)
        private readonly ulong[] nPlaneNegZ; // neighbor at -Z (+Z face)
        private readonly ulong[] nPlanePosZ; // neighbor at +Z (-Z face)

        public SparseChunkRender(
            Vector3 chunkWorldPosition,
            int maxX, int maxY, int maxZ,
            ushort emptyBlock,
            ushort[] flatBlocks,
            BlockTextureAtlas atlas,
            ChunkPrerenderData prerender)
        {
            this.chunkWorldPosition = chunkWorldPosition;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            this.emptyBlock = emptyBlock;
            this.flatBlocks = flatBlocks;
            this.atlas = atlas;
            faceNegX = prerender.FaceNegX; facePosX = prerender.FacePosX; faceNegY = prerender.FaceNegY; facePosY = prerender.FacePosY; faceNegZ = prerender.FaceNegZ; facePosZ = prerender.FacePosZ;
            nNegXPosX = prerender.NeighborNegXPosX; nPosXNegX = prerender.NeighborPosXNegX; nNegYPosY = prerender.NeighborNegYPosY; nPosYNegY = prerender.NeighborPosYNegY; nNegZPosZ = prerender.NeighborNegZPosZ; nPosZNegZ = prerender.NeighborPosZNegZ;
            nPlaneNegX = prerender.NeighborPlaneNegX; nPlanePosX = prerender.NeighborPlanePosX;
            nPlaneNegY = prerender.NeighborPlaneNegY; nPlanePosY = prerender.NeighborPlanePosY;
            nPlaneNegZ = prerender.NeighborPlaneNegZ; nPlanePosZ = prerender.NeighborPlanePosZ;
        }

        private static readonly byte FACE_LEFT   = 1 << 0;
        private static readonly byte FACE_RIGHT  = 1 << 1;
        private static readonly byte FACE_TOP    = 1 << 2;
        private static readonly byte FACE_BOTTOM = 1 << 3;
        private static readonly byte FACE_FRONT  = 1 << 4;
        private static readonly byte FACE_BACK   = 1 << 5;

        // Popcount LUT for 6-bit masks (0..63)
        private static readonly byte[] FacePopCount = new byte[64]
        {
            0,1,1,2,1,2,2,3,1,2,2,3,2,3,3,4,
            1,2,2,3,2,3,3,4,2,3,3,4,3,4,4,5,
            1,2,2,3,2,3,3,4,2,3,3,4,3,4,4,5,
            2,3,3,4,3,4,4,5,3,4,4,5,4,5,5,6
        };

        public BuildResult Build()
        {
            int voxelCount = maxX * maxY * maxZ;
            if (voxelCount == 0)
            {
                return EmptyResult();
            }

            // Pass 1: occupancy bitset + solid count
            int globalWordCount = (voxelCount + 63) >> 6;
            ulong[] occupancy = ArrayPool<ulong>.Shared.Rent(globalWordCount);
            Array.Clear(occupancy, 0, globalWordCount);

            int solidTotal = 0;
            for (int x = 0; x < maxX; x++)
            {
                int baseXIndex = x * maxZ * maxY;
                for (int z = 0; z < maxZ; z++)
                {
                    int baseXZIndex = baseXIndex + z * maxY;
                    for (int y = 0; y < maxY; y++)
                    {
                        int li = baseXZIndex + y;
                        if (flatBlocks[li] == emptyBlock) continue;
                        occupancy[li >> 6] |= 1UL << (li & 63);
                        solidTotal++;
                    }
                }
            }

            if (solidTotal == 0)
            {
                ArrayPool<ulong>.Shared.Return(occupancy, false);
                return EmptyResult();
            }

            // Allocate arrays sized to total solids (we will compact to visibleCount).
            // Coordinate packing: x | (y<<8) | (z<<16) (assumes dims <= 255). If dims exceed 255 we fall back to linear decode later.
            bool dimsFitByte = maxX <= 255 && maxY <= 255 && maxZ <= 255;
            uint[] packedCoords = ArrayPool<uint>.Shared.Rent(solidTotal);
            ushort[] blocks = ArrayPool<ushort>.Shared.Rent(solidTotal);
            byte[] masks = ArrayPool<byte>.Shared.Rent(solidTotal);

            int visibleCount = 0;
            int totalFaces = 0;

            int xStride = maxZ * maxY;
            int zStride = maxY;

            // Visibility gating based on outer/neighbor face solidity flags
            bool leftVisible = !(faceNegX && nNegXPosX);
            bool rightVisible = !(facePosX && nPosXNegX);
            bool bottomVisible = !(faceNegY && nNegYPosY);
            bool topVisible = !(facePosY && nPosYNegY);
            bool backVisible = !(faceNegZ && nNegZPosZ);
            bool frontVisible = !(facePosZ && nPosZNegZ);

            for (int x = 0; x < maxX; x++)
            {
                int baseXIndex = x * maxZ * maxY;
                for (int z = 0; z < maxZ; z++)
                {
                    int baseXZIndex = baseXIndex + z * maxY;
                    for (int y = 0; y < maxY; y++)
                    {
                        int li = baseXZIndex + y;
                        ushort block = flatBlocks[li];
                        if (block == emptyBlock) continue;

                        byte mask = 0;
                        // LEFT
                        if (x == 0)
                        {
                            if (leftVisible)
                            {
                                int yzIndex = z * maxY + y; bool neighborSolid = nPlaneNegX != null && (yzIndex < maxY * maxZ) && ((nPlaneNegX[yzIndex >> 6] & (1UL << (yzIndex & 63))) != 0UL);
                                if (!neighborSolid) mask |= FACE_LEFT;
                            }
                        }
                        else if ((occupancy[(li - xStride) >> 6] & (1UL << ((li - xStride) & 63))) == 0UL) mask |= FACE_LEFT;

                        // RIGHT
                        if (x == maxX - 1)
                        {
                            if (rightVisible)
                            {
                                int yzIndex = z * maxY + y; bool neighborSolid = nPlanePosX != null && (yzIndex < maxY * maxZ) && ((nPlanePosX[yzIndex >> 6] & (1UL << (yzIndex & 63))) != 0UL);
                                if (!neighborSolid) mask |= FACE_RIGHT;
                            }
                        }
                        else if ((occupancy[(li + xStride) >> 6] & (1UL << ((li + xStride) & 63))) == 0UL) mask |= FACE_RIGHT;

                        // BOTTOM
                        if (y == 0)
                        {
                            if (bottomVisible)
                            {
                                int xzIndex = x * maxZ + z; bool neighborSolid = nPlaneNegY != null && (xzIndex < maxX * maxZ) && ((nPlaneNegY[xzIndex >> 6] & (1UL << (xzIndex & 63))) != 0UL);
                                if (!neighborSolid) mask |= FACE_BOTTOM;
                            }
                        }
                        else if ((occupancy[(li - 1) >> 6] & (1UL << ((li - 1) & 63))) == 0UL) mask |= FACE_BOTTOM;

                        // TOP
                        if (y == maxY - 1)
                        {
                            if (topVisible)
                            {
                                int xzIndex = x * maxZ + z; bool neighborSolid = nPlanePosY != null && (xzIndex < maxX * maxZ) && ((nPlanePosY[xzIndex >> 6] & (1UL << (xzIndex & 63))) != 0UL);
                                if (!neighborSolid) mask |= FACE_TOP;
                            }
                        }
                        else if ((occupancy[(li + 1) >> 6] & (1UL << ((li + 1) & 63))) == 0UL) mask |= FACE_TOP;

                        // BACK (negative Z)
                        if (z == 0)
                        {
                            if (backVisible)
                            {
                                int xyIndex = x * maxY + y; bool neighborSolid = nPlaneNegZ != null && (xyIndex < maxX * maxY) && ((nPlaneNegZ[xyIndex >> 6] & (1UL << (xyIndex & 63))) != 0UL);
                                if (!neighborSolid) mask |= FACE_BACK;
                            }
                        }
                        else if ((occupancy[(li - zStride) >> 6] & (1UL << ((li - zStride) & 63))) == 0UL) mask |= FACE_BACK;

                        // FRONT (positive Z)
                        if (z == maxZ - 1)
                        {
                            if (frontVisible)
                            {
                                int xyIndex = x * maxY + y; bool neighborSolid = nPlanePosZ != null && (xyIndex < maxX * maxY) && ((nPlanePosZ[xyIndex >> 6] & (1UL << (xyIndex & 63))) != 0UL);
                                if (!neighborSolid) mask |= FACE_FRONT;
                            }
                        }
                        else if ((occupancy[(li + zStride) >> 6] & (1UL << ((li + zStride) & 63))) == 0UL) mask |= FACE_FRONT;

                        if (mask == 0) continue;

                        if (dimsFitByte)
                            packedCoords[visibleCount] = (uint)(x | (y << 8) | (z << 16));
                        else
                            packedCoords[visibleCount] = (uint)li; // fallback linear index

                        blocks[visibleCount] = block;
                        masks[visibleCount] = mask;
                        totalFaces += FacePopCount[mask];
                        visibleCount++;
                    }
                }
            }

            if (totalFaces == 0)
            {
                Cleanup();
                return EmptyResult();
            }

            int totalVerts = totalFaces * 4;
            bool useUShort = totalVerts <= 65535;
            byte[] vertBuf = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
            byte[] uvBuf = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
            ushort[] idxU16 = useUShort ? ArrayPool<ushort>.Shared.Rent(totalFaces * 6) : null;
            uint[] idxU32 = useUShort ? null : ArrayPool<uint>.Shared.Rent(totalFaces * 6);

            int faceIndex = 0;
            for (int i = 0; i < visibleCount; i++)
            {
                byte mask = masks[i];
                uint packed = packedCoords[i];
                int x, y, z;
                if (dimsFitByte)
                {
                    x = (byte)(packed & 0xFF);
                    y = (byte)((packed >> 8) & 0xFF);
                    z = (byte)((packed >> 16) & 0xFF);
                }
                else
                {
                    // Fallback decode from linear index (rare path when any dimension > 255)
                    int li = (int)packed;
                    x = li / (maxZ * maxY);
                    int rem = li - x * maxZ * maxY;
                    z = rem / maxY;
                    y = rem - z * maxY;
                }
                ushort block = blocks[i];
                Emit(mask, block, (byte)x, (byte)y, (byte)z, ref faceIndex, vertBuf, uvBuf, useUShort, idxU16, idxU32);
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

            Cleanup();
            return result;

            BuildResult EmptyResult() => new BuildResult
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

            void Cleanup()
            {
                ArrayPool<ulong>.Shared.Return(occupancy, false);
                ArrayPool<uint>.Shared.Return(packedCoords, false);
                ArrayPool<ushort>.Shared.Return(blocks, false);
                ArrayPool<byte>.Shared.Return(masks, false);
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

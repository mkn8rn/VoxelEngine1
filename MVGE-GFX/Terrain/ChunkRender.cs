using MVGE_GFX.BufferObjects;
using MVGE_GFX.Models;
using MVGE_INF.Managers;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers;
using System.Runtime.CompilerServices;
using MVGE_INF.Models.Terrain;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private bool isBuilt = false;
        private Vector3 chunkWorldPosition;

        // Small-chunk fallback lists 
        private List<byte> chunkVertsList;
        private List<byte> chunkUVsList;
        private List<uint> chunkIndicesList;
        private List<ushort> chunkIndicesUShortList;

        // Pooled buffers for large chunks
        private byte[] vertBuffer;
        private byte[] uvBuffer;
        private uint[] indicesUIntBuffer;
        private ushort[] indicesUShortBuffer;
        private int vertBytesUsed;
        private int uvBytesUsed;
        private int indicesUsed;
        private bool useUShort;
        private bool usedPooling;

        private VAO chunkVAO;
        private VBO chunkVertexVBO;
        private VBO chunkUVVBO;
        private IBO chunkIBO;

        public static BlockTextureAtlas terrainTextureAtlas { get; set; }
        private static readonly List<ByteVector2> EmptyUVList = new(4); // reusable empty list

        private readonly ChunkData chunkMeta;
        private readonly Func<int, int, int, ushort> getWorldBlock;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;

        // Flattened ephemeral blocks (x-major, then z, then y). Returned to pool after GenerateFaces
        private readonly ushort[] flatBlocks;
        private readonly int maxX;
        private readonly int maxY;
        private readonly int maxZ;

        // Incoming solidity flags (current chunk faces)
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        // Neighbor opposing face solidity flags
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;

        // Uniform single-block fast path (post-replacement) flags
        private readonly bool allOneBlock;
        private readonly ushort allOneBlockId;

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;

        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        // Fast-path flag when chunk fully enclosed (no visible faces)
        private bool fullyOccluded;

        // Popcount LUT for 6-bit mask (bits: L,R,T,B,F,Bk)
        private static readonly byte[] FacePopCount = InitPopCount();

        // Bit flags for mask
        private const byte FACE_LEFT = 1 << 0;
        private const byte FACE_RIGHT = 1 << 1;
        private const byte FACE_TOP = 1 << 2;
        private const byte FACE_BOTTOM = 1 << 3;
        private const byte FACE_FRONT = 1 << 4;
        private const byte FACE_BACK = 1 << 5;

        public ChunkRender(
            ChunkData chunkData,
            Func<int, int, int, ushort> worldBlockGetter,
            ushort[] flatBlocks,
            int maxX,
            int maxY,
            int maxZ,
            bool faceNegX,
            bool facePosX,
            bool faceNegY,
            bool facePosY,
            bool faceNegZ,
            bool facePosZ,
            bool nNegXPosX,
            bool nPosXNegX,
            bool nNegYPosY,
            bool nPosYNegY,
            bool nNegZPosZ,
            bool nPosZNegZ,
            bool allOneBlock = false,
            ushort allOneBlockId = 0)
        {
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            this.flatBlocks = flatBlocks;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            chunkWorldPosition = new Vector3(chunkData.x, chunkData.y, chunkData.z);
            this.faceNegX = faceNegX; this.facePosX = facePosX; this.faceNegY = faceNegY; this.facePosY = facePosY; this.faceNegZ = faceNegZ; this.facePosZ = facePosZ;
            this.nNegXPosX = nNegXPosX; this.nPosXNegX = nPosXNegX; this.nNegYPosY = nNegYPosY; this.nPosYNegY = nPosYNegY; this.nNegZPosZ = nNegZPosZ; this.nPosZNegZ = nPosZNegZ;
            this.allOneBlock = allOneBlock; this.allOneBlockId = allOneBlockId;
            GenerateFaces();
        }

        public void Build()
        {
            if (isBuilt) return;

            if (fullyOccluded)
            {
                chunkVAO = new VAO();
                chunkVAO.Bind();
                chunkVertexVBO = new VBO(Array.Empty<byte>(), 0);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);
                chunkUVVBO = new VBO(Array.Empty<byte>(), 0);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);
                chunkIBO = new IBO(Array.Empty<uint>(), 0);
                isBuilt = true; return;
            }

            chunkVAO = new VAO();
            chunkVAO.Bind();

            if (usedPooling)
            {
                chunkVertexVBO = new VBO(vertBuffer, vertBytesUsed);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

                chunkUVVBO = new VBO(uvBuffer, uvBytesUsed);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

                chunkIBO = useUShort
                    ? new IBO(indicesUShortBuffer, indicesUsed)
                    : new IBO(indicesUIntBuffer, indicesUsed);

                // Null checks before returning to pool to avoid ArgumentNullException if state inconsistent
                if (vertBuffer != null) ArrayPool<byte>.Shared.Return(vertBuffer, false);
                if (uvBuffer != null) ArrayPool<byte>.Shared.Return(uvBuffer, false);
                if (useUShort)
                {
                    if (indicesUShortBuffer != null) ArrayPool<ushort>.Shared.Return(indicesUShortBuffer, false);
                }
                else
                {
                    if (indicesUIntBuffer != null) ArrayPool<uint>.Shared.Return(indicesUIntBuffer, false);
                }
                vertBuffer = uvBuffer = null; indicesUIntBuffer = null; indicesUShortBuffer = null;
            }
            else
            {
                // indexFormat already decided during generation (two-pass)
                chunkVertexVBO = new VBO(chunkVertsList);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

                chunkUVVBO = new VBO(chunkUVsList);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

                chunkIBO = indexFormat == IndexFormat.UShort
                    ? new IBO(chunkIndicesUShortList)
                    : new IBO(chunkIndicesList);

                chunkVertsList.Clear(); chunkVertsList.TrimExcess();
                chunkUVsList.Clear(); chunkUVsList.TrimExcess();
                if (chunkIndicesList != null) { chunkIndicesList.Clear(); chunkIndicesList.TrimExcess(); }
                if (chunkIndicesUShortList != null) { chunkIndicesUShortList.Clear(); chunkIndicesUShortList.TrimExcess(); }
            }

            isBuilt = true;
        }

        public void Render(ShaderProgram program)
        {
            ProcessPendingDeletes();
            if (!isBuilt) Build();
            if (fullyOccluded) return;

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            chunkVAO.Bind();
            chunkIBO.Bind();
            int count = chunkIBO.Count;
            if (count <= 0) return;
            GL.DrawElements(
                PrimitiveType.Triangles,
                count,
                (usedPooling && useUShort) || (!usedPooling && indexFormat == IndexFormat.UShort)
                    ? DrawElementsType.UnsignedShort
                    : DrawElementsType.UnsignedInt,
                0);
        }

        // Bounding-box aware masked two-pass generation.
        private void GenerateFacesListFlatMaskedTwoPass_BB()
        {
            int strideX = maxZ * maxY;
            int strideZ = maxY;

            // First pass: determine bounding box of non-empty voxels (no allocations yet)
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

            // If bounding box covers entire chunk, fall back to existing method (no extra indirection)
            if (minX == 0 && minY == 0 && minZ == 0 && maxXb == maxX - 1 && maxYb == maxY - 1 && maxZb == maxZ - 1)
            {
                GenerateFacesListFlatMaskedTwoPass_Full();
                return;
            }

            int spanX = maxXb - minX + 1;
            int spanY = maxYb - minY + 1;
            int spanZ = maxZb - minZ + 1;
            int regionVolume = spanX * spanY * spanZ;

            byte[] masks = ArrayPool<byte>.Shared.Rent(regionVolume);
            Array.Clear(masks, 0, regionVolume);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int MaskIndex(int x, int y, int z) => ((x - minX) * spanZ + (z - minZ)) * spanY + (y - minY);

            int totalFaces = 0;
            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;

            // PASS 1 restricted to bounding box
            bool leftVisible = !(faceNegX && nNegXPosX);
            bool rightVisible = !(facePosX && nPosXNegX);
            bool bottomVisible = !(faceNegY && nNegYPosY);
            bool topVisible = !(facePosY && nPosYNegY);
            bool backVisible = !(faceNegZ && nNegZPosZ);
            bool frontVisible = !(facePosZ && nPosZNegZ);

            for (int x = minX; x <= maxXb; x++)
            {
                int xBase = x * strideX;
                int wx = baseWX + x;
                bool chunkMinX = x == 0;
                bool chunkMaxX = x == maxX - 1;
                for (int z = minZ; z <= maxZb; z++)
                {
                    int zBase = xBase + z * maxY;
                    int wz = baseWZ + z;
                    bool chunkMinZ = z == 0;
                    bool chunkMaxZ = z == maxZ - 1;
                    for (int y = minY; y <= maxYb; y++)
                    {
                        int li = zBase + y;
                        ushort block = flatBlocks[li];
                        if (block == emptyBlock) continue;
                        int wy = baseWY + y;
                        bool chunkMinY = y == 0;
                        bool chunkMaxY = y == maxY - 1;
                        byte mask = 0;
                        if (leftVisible && (chunkMinX ? getWorldBlock(wx - 1, wy, wz) == emptyBlock : flatBlocks[li - strideX] == emptyBlock)) mask |= FACE_LEFT;
                        if (rightVisible && (chunkMaxX ? getWorldBlock(wx + 1, wy, wz) == emptyBlock : flatBlocks[li + strideX] == emptyBlock)) mask |= FACE_RIGHT;
                        if (topVisible && (chunkMaxY ? getWorldBlock(wx, wy + 1, wz) == emptyBlock : flatBlocks[li + 1] == emptyBlock)) mask |= FACE_TOP;
                        if (bottomVisible && (chunkMinY ? getWorldBlock(wx, wy - 1, wz) == emptyBlock : flatBlocks[li - 1] == emptyBlock)) mask |= FACE_BOTTOM;
                        if (frontVisible && (chunkMaxZ ? getWorldBlock(wx, wy, wz + 1) == emptyBlock : flatBlocks[li + strideZ] == emptyBlock)) mask |= FACE_FRONT;
                        if (backVisible && (chunkMinZ ? getWorldBlock(wx, wy, wz - 1) == emptyBlock : flatBlocks[li - strideZ] == emptyBlock)) mask |= FACE_BACK;
                        if (mask == 0) continue;
                        masks[MaskIndex(x, y, z)] = mask;
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
                ArrayPool<byte>.Shared.Return(masks, false);
                return;
            }

            int totalVerts = totalFaces * 4;
            bool useUShortIndices = totalVerts <= 65535;
            indexFormat = useUShortIndices ? IndexFormat.UShort : IndexFormat.UInt;

            chunkVertsList = new List<byte>(totalVerts * 3);
            chunkUVsList = new List<byte>(totalVerts * 2);
            if (useUShortIndices) chunkIndicesUShortList = new List<ushort>(totalFaces * 6); else chunkIndicesList = new List<uint>(totalFaces * 6);

            int currentVertexBase = 0;
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

            ArrayPool<byte>.Shared.Return(masks, false);
        }

        // Fallback full-volume masked two-pass (previous implementation) when bounding box covers whole chunk
        private void GenerateFacesListFlatMaskedTwoPass_Full()
        {
            int strideX = maxZ * maxY; int strideZ = maxY;
            byte[] masks = ArrayPool<byte>.Shared.Rent(flatBlocks.Length);
            Array.Clear(masks, 0, flatBlocks.Length);
            int totalFaces = 0; int baseWX = (int)chunkWorldPosition.X; int baseWY = (int)chunkWorldPosition.Y; int baseWZ = (int)chunkWorldPosition.Z;

            bool leftVisible = !(faceNegX && nNegXPosX);
            bool rightVisible = !(facePosX && nPosXNegX);
            bool bottomVisible = !(faceNegY && nNegYPosY);
            bool topVisible = !(facePosY && nPosYNegY);
            bool backVisible = !(faceNegZ && nNegZPosZ);
            bool frontVisible = !(facePosZ && nPosZNegZ);

            for (int x = 0; x < maxX; x++)
            {
                int xBase = x * strideX; int wx = baseWX + x; bool atMinX = x == 0; bool atMaxX = x == maxX - 1;
                for (int z = 0; z < maxZ; z++)
                {
                    int zBase = xBase + z * maxY; int wz = baseWZ + z; bool atMinZ = z == 0; bool atMaxZ = z == maxZ - 1;
                    for (int y = 0; y < maxY; y++)
                    {
                        int li = zBase + y; ushort block = flatBlocks[li]; if (block == emptyBlock) continue; int wy = baseWY + y; bool atMinY = y == 0; bool atMaxY = y == maxY - 1; byte mask = 0;
                        if (leftVisible && (atMinX ? getWorldBlock(wx - 1, wy, wz) == emptyBlock : flatBlocks[li - strideX] == emptyBlock)) mask |= FACE_LEFT;
                        if (rightVisible && (atMaxX ? getWorldBlock(wx + 1, wy, wz) == emptyBlock : flatBlocks[li + strideX] == emptyBlock)) mask |= FACE_RIGHT;
                        if (topVisible && (atMaxY ? getWorldBlock(wx, wy + 1, wz) == emptyBlock : flatBlocks[li + 1] == emptyBlock)) mask |= FACE_TOP;
                        if (bottomVisible && (atMinY ? getWorldBlock(wx, wy - 1, wz) == emptyBlock : flatBlocks[li - 1] == emptyBlock)) mask |= FACE_BOTTOM;
                        if (frontVisible && (atMaxZ ? getWorldBlock(wx, wy, wz + 1) == emptyBlock : flatBlocks[li + strideZ] == emptyBlock)) mask |= FACE_FRONT;
                        if (backVisible && (atMinZ ? getWorldBlock(wx, wy, wz - 1) == emptyBlock : flatBlocks[li - strideZ] == emptyBlock)) mask |= FACE_BACK;
                        if (mask == 0) continue; masks[li] = mask; totalFaces += FacePopCount[mask];
                    }
                }
            }
            if (totalFaces == 0)
            {
                chunkVertsList = new List<byte>(0); chunkUVsList = new List<byte>(0); chunkIndicesList = new List<uint>(0); indexFormat = IndexFormat.UInt; ArrayPool<byte>.Shared.Return(masks, false); return;
            }
            int totalVerts = totalFaces * 4; bool useUShortIndices = totalVerts <= 65535; indexFormat = useUShortIndices ? IndexFormat.UShort : IndexFormat.UInt;
            chunkVertsList = new List<byte>(totalVerts * 3); chunkUVsList = new List<byte>(totalVerts * 2); if (useUShortIndices) chunkIndicesUShortList = new List<ushort>(totalFaces * 6); else chunkIndicesList = new List<uint>(totalFaces * 6);
            int currentVertexBase = 0;
            for (int x = 0; x < maxX; x++)
            {
                int xBase = x * strideX;
                for (int z = 0; z < maxZ; z++)
                {
                    int zBase = xBase + z * maxY;
                    for (int y = 0; y < maxY; y++)
                    {
                        int li = zBase + y; byte mask = masks[li]; if (mask == 0) continue; ushort block = flatBlocks[li]; var bp = new ByteVector3 { x = (byte)x, y = (byte)y, z = (byte)z };
                        if ((mask & FACE_LEFT) != 0) IntegrateFaceListEmit(block, Faces.LEFT, bp, ref currentVertexBase, useUShortIndices);
                        if ((mask & FACE_RIGHT) != 0) IntegrateFaceListEmit(block, Faces.RIGHT, bp, ref currentVertexBase, useUShortIndices);
                        if ((mask & FACE_TOP) != 0) IntegrateFaceListEmit(block, Faces.TOP, bp, ref currentVertexBase, useUShortIndices);
                        if ((mask & FACE_BOTTOM) != 0) IntegrateFaceListEmit(block, Faces.BOTTOM, bp, ref currentVertexBase, useUShortIndices);
                        if ((mask & FACE_FRONT) != 0) IntegrateFaceListEmit(block, Faces.FRONT, bp, ref currentVertexBase, useUShortIndices);
                        if ((mask & FACE_BACK) != 0) IntegrateFaceListEmit(block, Faces.BACK, bp, ref currentVertexBase, useUShortIndices);
                    }
                }
            }
            ArrayPool<byte>.Shared.Return(masks, false);
        }
    }
}
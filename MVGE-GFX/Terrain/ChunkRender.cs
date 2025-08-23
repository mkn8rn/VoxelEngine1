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
    public class ChunkRender
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

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;

        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        // Fast-path flag when chunk fully enclosed (no visible faces)
        private bool fullyOccluded;

        // Popcount LUT for 6-bit mask (bits: L,R,T,B,F,Bk)
        private static readonly byte[] FacePopCount = InitPopCount();
        private static byte[] InitPopCount()
        {
            var arr = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                int v = i; int c = 0; while (v != 0) { v &= v - 1; c++; } arr[i] = (byte)c;
            }
            return arr;
        }

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
            bool nPosZNegZ)
        {
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            this.flatBlocks = flatBlocks;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            chunkWorldPosition = new Vector3(chunkData.x, chunkData.y, chunkData.z);
            this.faceNegX = faceNegX; this.facePosX = facePosX; this.faceNegY = faceNegY; this.facePosY = facePosY; this.faceNegZ = faceNegZ; this.facePosZ = facePosZ;
            this.nNegXPosX = nNegXPosX; this.nPosXNegX = nPosXNegX; this.nNegYPosY = nNegYPosY; this.nPosYNegY = nPosYNegY; this.nNegZPosZ = nNegZPosZ; this.nPosZNegZ = nPosZNegZ;
            GenerateFaces();
        }

        public static void ProcessPendingDeletes()
        {
            while (pendingDeletion.TryDequeue(out var cr)) cr.DeleteGL();
        }

        public void ScheduleDelete()
        {
            if (!isBuilt) return;
            pendingDeletion.Enqueue(this);
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
            OpenTK.Graphics.OpenGL4.GL.DrawElements(
                PrimitiveType.Triangles,
                count,
                (usedPooling && useUShort) || (!usedPooling && indexFormat == IndexFormat.UShort)
                    ? DrawElementsType.UnsignedShort
                    : DrawElementsType.UnsignedInt,
                0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FlatIndex(int x, int y, int z) => (x * maxZ + z) * maxY + y;

        private void GenerateFaces()
        {
            // Decide if pooling should be used based on non-empty voxel count instead of raw volume
            bool usePooling = false;
            if (FlagManager.flags.useFacePooling.GetValueOrDefault())
            {
                int threshold = FlagManager.flags.faceAmountToPool.GetValueOrDefault(int.MaxValue);
                if (threshold >= 0)
                {
                    int nonEmpty = 0;
                    int total = flatBlocks.Length;
                    for (int i = 0; i < total; i++)
                    {
                        if (flatBlocks[i] != emptyBlock)
                        {
                            nonEmpty++;
                            if (nonEmpty >= threshold)
                            {
                                usePooling = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (CheckFullyOccluded(maxX, maxY, maxZ)) { fullyOccluded = true; ReturnFlat(); return; }

            if (usePooling)
            {
                var builder = new PooledFacesRender(
                    chunkWorldPosition, maxX, maxY, maxZ, emptyBlock,
                    getWorldBlock, null, null, terrainTextureAtlas, flatBlocks,
                    faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ,
                    nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ);
                var res = builder.Build();
                usedPooling = true;
                useUShort = res.UseUShort;
                vertBuffer = res.VertBuffer; uvBuffer = res.UVBuffer;
                indicesUIntBuffer = res.IndicesUIntBuffer; indicesUShortBuffer = res.IndicesUShortBuffer;
                vertBytesUsed = res.VertBytesUsed; uvBytesUsed = res.UVBytesUsed; indicesUsed = res.IndicesUsed;
                indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;
                ReturnFlat();
            }
            else
            {
                GenerateFacesListFlatMaskedTwoPass_BB();
                ReturnFlat();
            }
        }

        private void ReturnFlat()
        {
            if (flatBlocks != null) ArrayPool<ushort>.Shared.Return(flatBlocks, false);
        }

        private bool CheckFullyOccluded(int maxX, int maxY, int maxZ)
        {
            // Fast constant-time short-circuit using face + neighbor opposing face solidity flags.
            if (faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                return true;
            }

            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;
            // Check chunk boundary only using flat blocks
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    int z0 = 0; int z1 = maxZ - 1;
                    if (flatBlocks[FlatIndex(x, y, z0)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, y, z1)] == emptyBlock) return false;
                }
            for (int z = 0; z < maxZ; z++)
                for (int y = 0; y < maxY; y++)
                {
                    int x0 = 0; int x1 = maxX - 1;
                    if (flatBlocks[FlatIndex(x0, y, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x1, y, z)] == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    int y0 = 0; int y1 = maxY - 1;
                    if (flatBlocks[FlatIndex(x, y0, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, y1, z)] == emptyBlock) return false;
                }
            // Neighbor shell checks
            for (int y = 0; y < maxY; y++)
                for (int z = 0; z < maxZ; z++)
                {
                    if (getWorldBlock(baseWX - 1, baseWY + y, baseWZ + z) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + maxX, baseWY + y, baseWZ + z) == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    if (getWorldBlock(baseWX + x, baseWY - 1, baseWZ + z) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + x, baseWY + maxY, baseWZ + z) == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ - 1) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ + maxZ) == emptyBlock) return false;
                }
            return true;
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

        private void IntegrateFaceListEmit(ushort block, Faces face, ByteVector3 bp, ref int currentVertexBase, bool useUShortIndices)
        {
            var verts = RawFaceData.rawVertexData[face];
            // Append vertex positions
            foreach (var v in verts)
            {
                chunkVertsList.Add((byte)(v.x + bp.x));
                chunkVertsList.Add((byte)(v.y + bp.y));
                chunkVertsList.Add((byte)(v.z + bp.z));
            }
            // UVs
            var blockUVs = block != emptyBlock ? terrainTextureAtlas.GetBlockUVs(block, face) : EmptyUVList;
            foreach (var uv in blockUVs)
            {
                chunkUVsList.Add(uv.x);
                chunkUVsList.Add(uv.y);
            }
            // Indices (two triangles)
            if (useUShortIndices)
            {
                chunkIndicesUShortList.Add((ushort)(currentVertexBase + 0));
                chunkIndicesUShortList.Add((ushort)(currentVertexBase + 1));
                chunkIndicesUShortList.Add((ushort)(currentVertexBase + 2));
                chunkIndicesUShortList.Add((ushort)(currentVertexBase + 2));
                chunkIndicesUShortList.Add((ushort)(currentVertexBase + 3));
                chunkIndicesUShortList.Add((ushort)(currentVertexBase + 0));
            }
            else
            {
                chunkIndicesList.Add((uint)(currentVertexBase + 0));
                chunkIndicesList.Add((uint)(currentVertexBase + 1));
                chunkIndicesList.Add((uint)(currentVertexBase + 2));
                chunkIndicesList.Add((uint)(currentVertexBase + 2));
                chunkIndicesList.Add((uint)(currentVertexBase + 3));
                chunkIndicesList.Add((uint)(currentVertexBase + 0));
            }
            currentVertexBase += 4;
        }

        private void DeleteGL()
        {
            if (!isBuilt) return;
            chunkVAO.Delete();
            chunkVertexVBO.Delete();
            chunkUVVBO.Delete();
            chunkIBO.Delete();
            isBuilt = false;
        }
    }
}
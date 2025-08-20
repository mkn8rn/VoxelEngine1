using MVGE_GFX.BufferObjects;
using MVGE_GFX.Models;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers;
using System.Runtime.CompilerServices;

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

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;

        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        // Fast-path flag when chunk fully enclosed (no visible faces)
        private bool fullyOccluded;

        public ChunkRender(
            ChunkData chunkData,
            Func<int, int, int, ushort> worldBlockGetter,
            ushort[] flatBlocks,
            int maxX,
            int maxY,
            int maxZ)
        {
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            this.flatBlocks = flatBlocks;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            chunkWorldPosition = new OpenTK.Mathematics.Vector3(chunkData.x, chunkData.y, chunkData.z);
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

                ArrayPool<byte>.Shared.Return(vertBuffer, false);
                ArrayPool<byte>.Shared.Return(uvBuffer, false);
                if (useUShort) ArrayPool<ushort>.Shared.Return(indicesUShortBuffer, false); else ArrayPool<uint>.Shared.Return(indicesUIntBuffer, false);
                vertBuffer = uvBuffer = null; indicesUIntBuffer = null; indicesUShortBuffer = null;
            }
            else
            {
                TryFinalizeIndexFormatList();

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
            long volume = (long)maxX * maxY * maxZ;
            bool usePooling = false;
            if (FlagManager.flags.useFacePooling.GetValueOrDefault())
            {
                int threshold = FlagManager.flags.faceAmountToPool.GetValueOrDefault(int.MaxValue);
                if (threshold >= 0 && volume >= threshold) usePooling = true;
            }

            if (CheckFullyOccluded(maxX, maxY, maxZ)) { fullyOccluded = true; ReturnFlat(); return; }

            if (usePooling)
            {
                var builder = new PooledFacesRender(chunkWorldPosition, maxX, maxY, maxZ, emptyBlock, getWorldBlock, null, null, terrainTextureAtlas, flatBlocks);
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
                GenerateFacesListFlat();
                ReturnFlat();
            }
        }

        private void ReturnFlat()
        {
            if (flatBlocks != null) ArrayPool<ushort>.Shared.Return(flatBlocks, false);
        }

        private bool CheckFullyOccluded(int maxX, int maxY, int maxZ)
        {
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

        private void GenerateFacesListFlat()
        {
            int blockCount = maxX * maxY * maxZ;
            int worstFaces = blockCount * 6;
            int vertsCapacity = Math.Min(worstFaces * 12, 8 * 1024 * 1024);
            int uvsCapacity = Math.Min(worstFaces * 8, 8 * 1024 * 1024);
            int indicesCapacity = Math.Min(worstFaces * 6, (8 * 1024 * 1024) / 4);

            chunkVertsList = new List<byte>(vertsCapacity);
            chunkUVsList = new List<byte>(uvsCapacity);
            chunkIndicesList = new List<uint>(indicesCapacity);

            int strideX = maxZ * maxY; // delta for +/- X neighbor
            int strideZ = maxY;       // delta for +/- Z neighbor
            for (int x = 0; x < maxX; x++)
            {
                int xBase = x * strideX;
                for (int z = 0; z < maxZ; z++)
                {
                    int zBase = xBase + z * maxY;
                    for (int y = 0; y < maxY; y++)
                    {
                        int li = zBase + y;
                        ushort block = flatBlocks[li];
                        if (block == emptyBlock) continue;
                        int wx = (int)chunkWorldPosition.X + x;
                        int wy = (int)chunkWorldPosition.Y + y;
                        int wz = (int)chunkWorldPosition.Z + z;
                        var bp = new ByteVector3 { x = (byte)x, y = (byte)y, z = (byte)z };
                        int localFaces = 0;
                        // -X
                        if (x == 0)
                        {
                            if (getWorldBlock(wx - 1, wy, wz) == emptyBlock) { IntegrateFaceList(block, Faces.LEFT, bp); localFaces++; }
                        }
                        else if (flatBlocks[li - strideX] == emptyBlock) { IntegrateFaceList(block, Faces.LEFT, bp); localFaces++; }
                        // +X
                        if (x == maxX - 1)
                        {
                            if (getWorldBlock(wx + 1, wy, wz) == emptyBlock) { IntegrateFaceList(block, Faces.RIGHT, bp); localFaces++; }
                        }
                        else if (flatBlocks[li + strideX] == emptyBlock) { IntegrateFaceList(block, Faces.RIGHT, bp); localFaces++; }
                        // +Y
                        if (y == maxY - 1)
                        {
                            if (getWorldBlock(wx, wy + 1, wz) == emptyBlock) { IntegrateFaceList(block, Faces.TOP, bp); localFaces++; }
                        }
                        else if (flatBlocks[li + 1] == emptyBlock) { IntegrateFaceList(block, Faces.TOP, bp); localFaces++; }
                        // -Y
                        if (y == 0)
                        {
                            if (getWorldBlock(wx, wy - 1, wz) == emptyBlock) { IntegrateFaceList(block, Faces.BOTTOM, bp); localFaces++; }
                        }
                        else if (flatBlocks[li - 1] == emptyBlock) { IntegrateFaceList(block, Faces.BOTTOM, bp); localFaces++; }
                        // +Z
                        if (z == maxZ - 1)
                        {
                            if (getWorldBlock(wx, wy, wz + 1) == emptyBlock) { IntegrateFaceList(block, Faces.FRONT, bp); localFaces++; }
                        }
                        else if (flatBlocks[li + strideZ] == emptyBlock) { IntegrateFaceList(block, Faces.FRONT, bp); localFaces++; }
                        // -Z
                        if (z == 0)
                        {
                            if (getWorldBlock(wx, wy, wz - 1) == emptyBlock) { IntegrateFaceList(block, Faces.BACK, bp); localFaces++; }
                        }
                        else if (flatBlocks[li - strideZ] == emptyBlock) { IntegrateFaceList(block, Faces.BACK, bp); localFaces++; }

                        if (localFaces > 0) AddIndicesList(localFaces);
                    }
                }
            }
        }

        private void IntegrateFaceList(ushort block, Faces face, ByteVector3 bp)
        {
            var verts = RawFaceData.rawVertexData[face];
            foreach (var v in verts)
            {
                chunkVertsList.Add((byte)(v.x + bp.x));
                chunkVertsList.Add((byte)(v.y + bp.y));
                chunkVertsList.Add((byte)(v.z + bp.z));
            }
            var blockUVs = block != emptyBlock ? terrainTextureAtlas.GetBlockUVs(block, face) : EmptyUVList;
            foreach (var uv in blockUVs)
            {
                chunkUVsList.Add(uv.x);
                chunkUVsList.Add(uv.y);
            }
        }

        private void AddIndicesList(int faces)
        {
            int currentVertIndex = (chunkVertsList.Count / 3) - faces * 4;
            for (int i = 0; i < faces; i++)
            {
                int baseIndex = currentVertIndex + i * 4;
                chunkIndicesList.Add((uint)(baseIndex + 0));
                chunkIndicesList.Add((uint)(baseIndex + 1));
                chunkIndicesList.Add((uint)(baseIndex + 2));
                chunkIndicesList.Add((uint)(baseIndex + 2));
                chunkIndicesList.Add((uint)(baseIndex + 3));
                chunkIndicesList.Add((uint)(baseIndex + 0));
            }
        }

        private void TryFinalizeIndexFormatList()
        {
            int vertCount = chunkVertsList.Count / 3;
            if (vertCount <= 65535)
            {
                indexFormat = IndexFormat.UShort;
                chunkIndicesUShortList = new List<ushort>(chunkIndicesList.Count);
                foreach (var i in chunkIndicesList) chunkIndicesUShortList.Add((ushort)i);
                chunkIndicesList.Clear();
            }
            else indexFormat = IndexFormat.UInt;
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
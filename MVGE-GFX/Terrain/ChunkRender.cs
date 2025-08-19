using MVGE_GFX.BufferObjects;
using MVGE_GFX.Models;
using MVGE_GFX.Utils;
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
        private OpenTK.Mathematics.Vector3 chunkWorldPosition;

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
        private readonly Func<int, int, int, ushort> getLocalBlock;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;

        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        // UV cache: key = (blockId << 3) | face (face fits in 3 bits). Value = 8-byte array (4 UV pairs)
        private static readonly ConcurrentDictionary<int, byte[]> uvByteCache = new();

        // Fast-path flag when chunk fully enclosed (no visible faces)
        private bool fullyOccluded;

        public ChunkRender(
            ChunkData chunkData,
            Func<int, int, int, ushort> worldBlockGetter,
            Func<int, int, int, ushort> localBlockGetter)
        {
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            getLocalBlock = localBlockGetter;
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

            OpenTK.Mathematics.Vector3 adjustedChunkPosition = chunkWorldPosition + new OpenTK.Mathematics.Vector3(1f, 1f, 1f);
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

        private void GenerateFaces()
        {
            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;

            long volume = (long)maxX * maxY * maxZ;
            bool usePooling = false;
            if (FlagManager.flags.useFacePooling.GetValueOrDefault())
            {
                int threshold = FlagManager.flags.faceAmountToPool.GetValueOrDefault(int.MaxValue);
                if (threshold >= 0 && volume >= threshold)
                {
                    usePooling = true;
                }
            }

            if (CheckFullyOccluded(maxX, maxY, maxZ)) { fullyOccluded = true; return; }

            if (usePooling)
            {
                var res = RenderPooledFacesUtils.BuildPooledFaces(
                    chunkWorldPosition,
                    maxX, maxY, maxZ,
                    emptyBlock,
                    getWorldBlock,
                    getLocalBlock,
                    terrainTextureAtlas);
                usedPooling = true;
                useUShort = res.UseUShort;
                vertBuffer = res.VertBuffer;
                uvBuffer = res.UVBuffer;
                indicesUIntBuffer = res.IndicesUIntBuffer;
                indicesUShortBuffer = res.IndicesUShortBuffer;
                vertBytesUsed = res.VertBytesUsed;
                uvBytesUsed = res.UVBytesUsed;
                indicesUsed = res.IndicesUsed;
                indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;
            }
            else
            {
                GenerateFacesList(maxX, maxY, maxZ);
            }
        }

        private bool CheckFullyOccluded(int maxX, int maxY, int maxZ)
        {
            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                    for (int z = 0; z < maxZ; z++)
                    {
                        if (x != 0 && x != maxX - 1 && y != 0 && y != maxY - 1 && z != 0 && z != maxZ - 1) continue;
                        if (getLocalBlock(x, y, z) == emptyBlock) return false;
                    }
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

        private void GenerateFacesList(int maxX, int maxY, int maxZ)
        {
            int blockCount = maxX * maxY * maxZ;
            int worstFaces = blockCount * 6;
            int vertsCapacity = worstFaces * 12;
            int uvsCapacity = worstFaces * 8;
            int indicesCapacity = worstFaces * 6;
            const int MaxListBytes = 8 * 1024 * 1024;
            if (vertsCapacity > MaxListBytes) vertsCapacity = MaxListBytes;
            if (uvsCapacity > MaxListBytes) uvsCapacity = MaxListBytes;
            if (indicesCapacity > (MaxListBytes / 4)) indicesCapacity = MaxListBytes / 4;

            chunkVertsList = new List<byte>(vertsCapacity);
            chunkUVsList = new List<byte>(uvsCapacity);
            chunkIndicesList = new List<uint>(indicesCapacity);

            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                    for (int y = 0; y < maxY; y++)
                    {
                        ushort block = getLocalBlock(x, y, z);
                        if (block == emptyBlock) continue;
                        int wx = (int)chunkWorldPosition.X + x;
                        int wy = (int)chunkWorldPosition.Y + y;
                        int wz = (int)chunkWorldPosition.Z + z;
                        var bp = new ByteVector3 { x = (byte)x, y = (byte)y, z = (byte)z };
                        int localFaces = 0;
                        if ((x > 0 && getLocalBlock(x - 1, y, z) == emptyBlock) || (x == 0 && getWorldBlock(wx - 1, wy, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.LEFT, bp); localFaces++; }
                        if ((x < maxX - 1 && getLocalBlock(x + 1, y, z) == emptyBlock) || (x == maxX - 1 && getWorldBlock(wx + 1, wy, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.RIGHT, bp); localFaces++; }
                        if ((y < maxY - 1 && getLocalBlock(x, y + 1, z) == emptyBlock) || (y == maxY - 1 && getWorldBlock(wx, wy + 1, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.TOP, bp); localFaces++; }
                        if ((y > 0 && getLocalBlock(x, y - 1, z) == emptyBlock) || (y == 0 && getWorldBlock(wx, wy - 1, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.BOTTOM, bp); localFaces++; }
                        if ((z < maxZ - 1 && getLocalBlock(x, y, z + 1) == emptyBlock) || (z == maxZ - 1 && getWorldBlock(wx, wy, wz + 1) == emptyBlock)) { IntegrateFaceList(block, Faces.FRONT, bp); localFaces++; }
                        if ((z > 0 && getLocalBlock(x, y, z - 1) == emptyBlock) || (z == 0 && getWorldBlock(wx, wy, wz - 1) == emptyBlock)) { IntegrateFaceList(block, Faces.BACK, bp); localFaces++; }
                        if (localFaces > 0) AddIndicesList(localFaces);
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
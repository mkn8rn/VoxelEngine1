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
using System.Threading.Tasks;

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
        private readonly Func<int, int, int, ushort> getLocalBlock;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;

        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        public ChunkRender(
            ChunkData chunkData,
            Func<int, int, int, ushort> worldBlockGetter,
            Func<int, int, int, ushort> localBlockGetter)
        {
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            getLocalBlock = localBlockGetter;
            chunkWorldPosition = new Vector3(chunkData.x, chunkData.y, chunkData.z);
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

            chunkVAO = new VAO();
            chunkVAO.Bind();

            if (usedPooling)
            {
                // Pooled path
                chunkVertexVBO = new VBO(vertBuffer, vertBytesUsed);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

                chunkUVVBO = new VBO(uvBuffer, uvBytesUsed);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

                if (useUShort)
                {
                    chunkIBO = new IBO(indicesUShortBuffer, indicesUsed);
                }
                else
                {
                    chunkIBO = new IBO(indicesUIntBuffer, indicesUsed);
                }

                // Return pooled arrays AFTER upload
                ArrayPool<byte>.Shared.Return(vertBuffer, false);
                ArrayPool<byte>.Shared.Return(uvBuffer, false);
                if (useUShort) ArrayPool<ushort>.Shared.Return(indicesUShortBuffer, false); else ArrayPool<uint>.Shared.Return(indicesUIntBuffer, false);
                vertBuffer = uvBuffer = null; indicesUIntBuffer = null; indicesUShortBuffer = null;
            }
            else
            {
                // List fallback path (small volume)
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

                // Release list storage
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

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            chunkVAO.Bind();
            chunkIBO.Bind();

            GL.DrawElements(
                PrimitiveType.Triangles,
                usedPooling ? indicesUsed : (indexFormat == IndexFormat.UShort ? chunkIndicesUShortList.Count : chunkIndicesList.Count),
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
            bool usePoolingPath = volume >= 16_000; // threshold consistent with previous parallel switch

            if (usePoolingPath)
            {
                GenerateFacesPooled(maxX, maxY, maxZ);
            }
            else
            {
                GenerateFacesList(maxX, maxY, maxZ);
            }
        }

        private void GenerateFacesList(int maxX, int maxY, int maxZ)
        {
            chunkVertsList = new List<byte>();
            chunkUVsList = new List<byte>();
            chunkIndicesList = new List<uint>();

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
            int currentVertIndex = (chunkVertsList.Count / 3) - faces * 4; // starting index of first face added in this batch
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

        private void GenerateFacesPooled(int maxX, int maxY, int maxZ)
        {
            int proc = Environment.ProcessorCount;
            int slices = Math.Min(proc, maxX);
            int sliceWidth = (int)Math.Ceiling(maxX / (double)slices);
            var faceLists = new List<PendingFace>[slices];

            Parallel.For(0, slices, s =>
            {
                var local = new List<PendingFace>(4096);
                int startX = s * sliceWidth;
                int endX = Math.Min(maxX, startX + sliceWidth);
                for (int x = startX; x < endX; x++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        for (int y = 0; y < maxY; y++)
                        {
                            ushort block = getLocalBlock(x, y, z);
                            if (block == emptyBlock) continue;
                            int wx = (int)chunkWorldPosition.X + x;
                            int wy = (int)chunkWorldPosition.Y + y;
                            int wz = (int)chunkWorldPosition.Z + z;

                            if ((x > 0 && getLocalBlock(x - 1, y, z) == emptyBlock) || (x == 0 && getWorldBlock(wx - 1, wy, wz) == emptyBlock))
                                local.Add(new PendingFace(block, Faces.LEFT, (byte)x, (byte)y, (byte)z));
                            if ((x < maxX - 1 && getLocalBlock(x + 1, y, z) == emptyBlock) || (x == maxX - 1 && getWorldBlock(wx + 1, wy, wz) == emptyBlock))
                                local.Add(new PendingFace(block, Faces.RIGHT, (byte)x, (byte)y, (byte)z));
                            if ((y < maxY - 1 && getLocalBlock(x, y + 1, z) == emptyBlock) || (y == maxY - 1 && getWorldBlock(wx, wy + 1, wz) == emptyBlock))
                                local.Add(new PendingFace(block, Faces.TOP, (byte)x, (byte)y, (byte)z));
                            if ((y > 0 && getLocalBlock(x, y - 1, z) == emptyBlock) || (y == 0 && getWorldBlock(wx, wy - 1, wz) == emptyBlock))
                                local.Add(new PendingFace(block, Faces.BOTTOM, (byte)x, (byte)y, (byte)z));
                            if ((z < maxZ - 1 && getLocalBlock(x, y, z + 1) == emptyBlock) || (z == maxZ - 1 && getWorldBlock(wx, wy, wz + 1) == emptyBlock))
                                local.Add(new PendingFace(block, Faces.FRONT, (byte)x, (byte)y, (byte)z));
                            if ((z > 0 && getLocalBlock(x, y, z - 1) == emptyBlock) || (z == 0 && getWorldBlock(wx, wy, wz - 1) == emptyBlock))
                                local.Add(new PendingFace(block, Faces.BACK, (byte)x, (byte)y, (byte)z));
                        }
                    }
                }
                faceLists[s] = local;
            });

            int totalFaces = 0;
            foreach (var fl in faceLists) totalFaces += fl.Count;
            int totalVerts = totalFaces * 4;
            useUShort = totalVerts <= 65535;
            usedPooling = true;
            indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;

            vertBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
            uvBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
            if (useUShort)
                indicesUShortBuffer = ArrayPool<ushort>.Shared.Rent(totalFaces * 6);
            else
                indicesUIntBuffer = ArrayPool<uint>.Shared.Rent(totalFaces * 6);

            vertBytesUsed = 0; uvBytesUsed = 0; indicesUsed = 0;
            int currentVertIndex = 0; // in vertices (not bytes)

            foreach (var list in faceLists)
            {
                foreach (var pf in list)
                {
                    WriteFacePooled(pf.Block, pf.Face, pf.X, pf.Y, pf.Z, ref currentVertIndex);
                }
            }
        }

        private void WriteFacePooled(ushort block, Faces face, byte bx, byte by, byte bz, ref int currentVertIndex)
        {
            var verts = RawFaceData.rawVertexData[face];
            for (int i = 0; i < 4; i++)
            {
                vertBuffer[vertBytesUsed++] = (byte)(verts[i].x + bx);
                vertBuffer[vertBytesUsed++] = (byte)(verts[i].y + by);
                vertBuffer[vertBytesUsed++] = (byte)(verts[i].z + bz);
            }
            var blockUVs = block != emptyBlock ? terrainTextureAtlas.GetBlockUVs(block, face) : null;
            if (blockUVs != null)
            {
                for (int i = 0; i < blockUVs.Count; i++)
                {
                    uvBuffer[uvBytesUsed++] = blockUVs[i].x;
                    uvBuffer[uvBytesUsed++] = blockUVs[i].y;
                }
            }
            else
            {
                // If empty (shouldn't happen for non-empty block), fill zeros
                for (int i = 0; i < 8; i++) uvBuffer[uvBytesUsed++] = 0;
            }
            // Indices (two triangles)
            if (useUShort)
            {
                indicesUShortBuffer[indicesUsed++] = (ushort)(currentVertIndex + 0);
                indicesUShortBuffer[indicesUsed++] = (ushort)(currentVertIndex + 1);
                indicesUShortBuffer[indicesUsed++] = (ushort)(currentVertIndex + 2);
                indicesUShortBuffer[indicesUsed++] = (ushort)(currentVertIndex + 2);
                indicesUShortBuffer[indicesUsed++] = (ushort)(currentVertIndex + 3);
                indicesUShortBuffer[indicesUsed++] = (ushort)(currentVertIndex + 0);
            }
            else
            {
                indicesUIntBuffer[indicesUsed++] = (uint)(currentVertIndex + 0);
                indicesUIntBuffer[indicesUsed++] = (uint)(currentVertIndex + 1);
                indicesUIntBuffer[indicesUsed++] = (uint)(currentVertIndex + 2);
                indicesUIntBuffer[indicesUsed++] = (uint)(currentVertIndex + 2);
                indicesUIntBuffer[indicesUsed++] = (uint)(currentVertIndex + 3);
                indicesUIntBuffer[indicesUsed++] = (uint)(currentVertIndex + 0);
            }
            currentVertIndex += 4;
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

        private readonly struct PendingFace
        {
            public readonly ushort Block;
            public readonly Faces Face;
            public readonly byte X, Y, Z;
            public PendingFace(ushort b, Faces f, byte x, byte y, byte z)
            { Block = b; Face = f; X = x; Y = y; Z = z; }
        }
    }
}

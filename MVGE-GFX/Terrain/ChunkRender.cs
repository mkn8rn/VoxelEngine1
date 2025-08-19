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

                chunkIBO = useUShort
                    ? new IBO(indicesUShortBuffer, indicesUsed)
                    : new IBO(indicesUIntBuffer, indicesUsed);

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

                // Release list storage after buffer upload
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

            if (usePooling)
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

            // Pre-cache all local blocks into a flat array to avoid repeated palette / delegate cost
            int sliceSizeY = maxY; // y fastest for locality between vertical neighbors
            int sliceSizeZ = maxZ;
            int planeSize = sliceSizeY * sliceSizeZ; // number of voxels in one X column slab
            int totalVoxels = maxX * planeSize;
            ushort[] localBlocks = new ushort[totalVoxels];
            int idx = 0;
            for (int x = 0; x < maxX; x++)
            {
                int baseXOffset = x * planeSize;
                for (int z = 0; z < maxZ; z++)
                {
                    int baseZXOffset = baseXOffset + z * sliceSizeY;
                    for (int y = 0; y < maxY; y++)
                    {
                        localBlocks[baseZXOffset + y] = getLocalBlock(x, y, z);
                    }
                }
            }

            static int LocalIndex(int x, int y, int z, int maxY, int maxZ)
                => (x * maxZ + z) * maxY + y; // matches population order

            // First pass: count faces per slice (parallel) using cached blocks
            int[] faceCounts = new int[slices];
            Parallel.For(0, slices, s =>
            {
                int startX = s * sliceWidth;
                int endX = Math.Min(maxX, startX + sliceWidth);
                int count = 0;
                for (int x = startX; x < endX; x++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        for (int y = 0; y < maxY; y++)
                        {
                            ushort block = localBlocks[LocalIndex(x, y, z, maxY, maxZ)];
                            if (block == emptyBlock) continue;
                            int wx = (int)chunkWorldPosition.X + x;
                            int wy = (int)chunkWorldPosition.Y + y;
                            int wz = (int)chunkWorldPosition.Z + z;
                            // Left
                            if (x == 0)
                            {
                                if (getWorldBlock(wx - 1, wy, wz) == emptyBlock) count++;
                            }
                            else if (localBlocks[LocalIndex(x - 1, y, z, maxY, maxZ)] == emptyBlock) count++;
                            // Right
                            if (x == maxX - 1)
                            {
                                if (getWorldBlock(wx + 1, wy, wz) == emptyBlock) count++;
                            }
                            else if (localBlocks[LocalIndex(x + 1, y, z, maxY, maxZ)] == emptyBlock) count++;
                            // Top
                            if (y == maxY - 1)
                            {
                                if (getWorldBlock(wx, wy + 1, wz) == emptyBlock) count++;
                            }
                            else if (localBlocks[LocalIndex(x, y + 1, z, maxY, maxZ)] == emptyBlock) count++;
                            // Bottom
                            if (y == 0)
                            {
                                if (getWorldBlock(wx, wy - 1, wz) == emptyBlock) count++;
                            }
                            else if (localBlocks[LocalIndex(x, y - 1, z, maxY, maxZ)] == emptyBlock) count++;
                            // Front
                            if (z == maxZ - 1)
                            {
                                if (getWorldBlock(wx, wy, wz + 1) == emptyBlock) count++;
                            }
                            else if (localBlocks[LocalIndex(x, y, z + 1, maxY, maxZ)] == emptyBlock) count++;
                            // Back
                            if (z == 0)
                            {
                                if (getWorldBlock(wx, wy, wz - 1) == emptyBlock) count++;
                            }
                            else if (localBlocks[LocalIndex(x, y, z - 1, maxY, maxZ)] == emptyBlock) count++;
                        }
                    }
                }
                faceCounts[s] = count;
            });

            // Prefix sums for face offsets
            int totalFaces = 0;
            int[] faceOffsets = new int[slices];
            for (int i = 0; i < slices; i++)
            {
                faceOffsets[i] = totalFaces;
                totalFaces += faceCounts[i];
            }
            int totalVerts = totalFaces * 4;

            useUShort = totalVerts <= 65535;
            usedPooling = true;
            indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;

            // Allocate pooled buffers sized for all faces
            vertBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
            uvBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
            if (useUShort)
                indicesUShortBuffer = ArrayPool<ushort>.Shared.Rent(totalFaces * 6);
            else
                indicesUIntBuffer = ArrayPool<uint>.Shared.Rent(totalFaces * 6);

            // Second pass: write faces directly in parallel using disjoint ranges and cached blocks
            Parallel.For(0, slices, s =>
            {
                int startX = s * sliceWidth;
                int endX = Math.Min(maxX, startX + sliceWidth);
                int writeFaceIndex = faceOffsets[s];
                for (int x = startX; x < endX; x++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        for (int y = 0; y < maxY; y++)
                        {
                            ushort block = localBlocks[LocalIndex(x, y, z, maxY, maxZ)];
                            if (block == emptyBlock) continue;
                            int wx = (int)chunkWorldPosition.X + x;
                            int wy = (int)chunkWorldPosition.Y + y;
                            int wz = (int)chunkWorldPosition.Z + z;

                            if (x == 0)
                            {
                                if (getWorldBlock(wx - 1, wy, wz) == emptyBlock) WriteFace(block, Faces.LEFT, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);
                            }
                            else if (localBlocks[LocalIndex(x - 1, y, z, maxY, maxZ)] == emptyBlock) WriteFace(block, Faces.LEFT, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);

                            if (x == maxX - 1)
                            {
                                if (getWorldBlock(wx + 1, wy, wz) == emptyBlock) WriteFace(block, Faces.RIGHT, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);
                            }
                            else if (localBlocks[LocalIndex(x + 1, y, z, maxY, maxZ)] == emptyBlock) WriteFace(block, Faces.RIGHT, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);

                            if (y == maxY - 1)
                            {
                                if (getWorldBlock(wx, wy + 1, wz) == emptyBlock) WriteFace(block, Faces.TOP, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);
                            }
                            else if (localBlocks[LocalIndex(x, y + 1, z, maxY, maxZ)] == emptyBlock) WriteFace(block, Faces.TOP, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);

                            if (y == 0)
                            {
                                if (getWorldBlock(wx, wy - 1, wz) == emptyBlock) WriteFace(block, Faces.BOTTOM, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);
                            }
                            else if (localBlocks[LocalIndex(x, y - 1, z, maxY, maxZ)] == emptyBlock) WriteFace(block, Faces.BOTTOM, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);

                            if (z == maxZ - 1)
                            {
                                if (getWorldBlock(wx, wy, wz + 1) == emptyBlock) WriteFace(block, Faces.FRONT, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);
                            }
                            else if (localBlocks[LocalIndex(x, y, z + 1, maxY, maxZ)] == emptyBlock) WriteFace(block, Faces.FRONT, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);

                            if (z == 0)
                            {
                                if (getWorldBlock(wx, wy, wz - 1) == emptyBlock) WriteFace(block, Faces.BACK, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);
                            }
                            else if (localBlocks[LocalIndex(x, y, z - 1, maxY, maxZ)] == emptyBlock) WriteFace(block, Faces.BACK, (byte)x, (byte)y, (byte)z, ref writeFaceIndex);
                        }
                    }
                }
            });

            // Set used byte counts
            vertBytesUsed = totalVerts * 3;
            uvBytesUsed = totalVerts * 2;
            indicesUsed = totalFaces * 6;
        }

        private void WriteFace(ushort block, Faces face, byte bx, byte by, byte bz, ref int faceIndex)
        {
            int baseVertexIndex = faceIndex * 4;
            int vertexByteOffset = baseVertexIndex * 3;
            int uvByteOffset = baseVertexIndex * 2;
            int indexOffset = faceIndex * 6;

            var verts = RawFaceData.rawVertexData[face];
            // Vertices (12 bytes)
            for (int i = 0; i < 4; i++)
            {
                vertBuffer[vertexByteOffset + i * 3 + 0] = (byte)(verts[i].x + bx);
                vertBuffer[vertexByteOffset + i * 3 + 1] = (byte)(verts[i].y + by);
                vertBuffer[vertexByteOffset + i * 3 + 2] = (byte)(verts[i].z + bz);
            }

            var blockUVs = block != emptyBlock ? terrainTextureAtlas.GetBlockUVs(block, face) : null;
            if (blockUVs != null)
            {
                for (int i = 0; i < blockUVs.Count; i++)
                {
                    uvBuffer[uvByteOffset + i * 2 + 0] = blockUVs[i].x;
                    uvBuffer[uvByteOffset + i * 2 + 1] = blockUVs[i].y;
                }
            }
            else
            {
                for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = 0;
            }

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

            faceIndex++; // advance to next face slot
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
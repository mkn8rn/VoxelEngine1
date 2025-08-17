using MVGE_GFX.BufferObjects;
using MVGE_GFX.Models;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

namespace MVGE_GFX.Terrain
{
    public class ChunkRender
    {
        private bool isBuilt = false;
        private Vector3 chunkWorldPosition;

        private readonly List<byte> chunkVerts;
        private readonly List<byte> chunkUVs;
        private readonly List<uint> chunkIndices;
        private uint indexCount;

        private VAO chunkVAO;
        private VBO chunkVertexVBO;
        private VBO chunkUVVBO;
        private IBO chunkIBO;

        public static BlockTextureAtlas terrainTextureAtlas { get; set; }

        private readonly ChunkData sourceChunkData;
        private readonly Func<int, int, int, ushort> getWorldBlock;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;

        public ChunkRender(ChunkData chunkData, Func<int, int, int, ushort> worldBlockGetter)
        {
            sourceChunkData = chunkData;
            getWorldBlock = worldBlockGetter;

            chunkUVs = new List<byte>();
            chunkVerts = new List<byte>();
            chunkIndices = new List<uint>();

            chunkWorldPosition = new Vector3(chunkData.x, chunkData.y, chunkData.z);

            GenerateFaces();
        }

        public void IntegrateFace(ushort block, Faces face, ByteVector3 blockPosition)
        {
            List<ByteVector2> blockUVs = new List<ByteVector2>();

            if (block != emptyBlock)
            {
                blockUVs = terrainTextureAtlas.GetBlockUVs(block, face);
            }

            FaceData faceData = new FaceData
            {
                vertices = AddTransformedVertices(RawFaceData.rawVertexData[face], blockPosition),
                uvs = blockUVs
            };

            foreach (var vert in faceData.vertices)
            {
                chunkVerts.Add(vert.x);
                chunkVerts.Add(vert.y);
                chunkVerts.Add(vert.z);
            }

            foreach (var uv in faceData.uvs)
            {
                chunkUVs.Add(uv.x);
                chunkUVs.Add(uv.y);
            }
        }

        public void AddIndices(int amountFaces)
        {
            for (int i = 0; i < amountFaces; i++)
            {
                chunkIndices.Add(0 + indexCount);
                chunkIndices.Add(1 + indexCount);
                chunkIndices.Add(2 + indexCount);
                chunkIndices.Add(2 + indexCount);
                chunkIndices.Add(3 + indexCount);
                chunkIndices.Add(0 + indexCount);

                indexCount += 4;
            }
        }

        public List<ByteVector3> AddTransformedVertices(List<ByteVector3> vertices, ByteVector3 blockPosition)
        {
            List<ByteVector3> transformedVertices = new List<ByteVector3>(vertices.Count);
            foreach (var vert in vertices)
            {
                transformedVertices.Add(new ByteVector3
                {
                    x = (byte)(vert.x + blockPosition.x),
                    y = (byte)(vert.y + blockPosition.y),
                    z = (byte)(vert.z + blockPosition.z),
                });
            }
            return transformedVertices;
        }

        public void Build()
        {
            chunkVAO = new VAO();
            chunkVAO.Bind();

            chunkVertexVBO = new VBO(chunkVerts);
            chunkVertexVBO.Bind();
            chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

            chunkUVVBO = new VBO(chunkUVs);
            chunkUVVBO.Bind();
            chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

            chunkIBO = new IBO(chunkIndices);

            isBuilt = true;
        }

        public void Render(ShaderProgram program)
        {
            if (!isBuilt)
            {
                Build();
            }

            Vector3 coordsAdjustment = new Vector3(1f, 1f, 1f);
            Vector3 adjustedChunkPosition = chunkWorldPosition + coordsAdjustment;

            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            chunkVAO.Bind();
            chunkIBO.Bind();

            GL.DrawElements(PrimitiveType.Triangles, chunkIndices.Count, DrawElementsType.UnsignedInt, 0);
        }

        public void Delete()
        {
            if (!isBuilt) return;
            chunkVAO.Delete();
            chunkVertexVBO.Delete();
            chunkUVVBO.Delete();
            chunkIBO.Delete();
            isBuilt = false;
        }

        private void GenerateFaces()
        {
            var blocks = sourceChunkData.blocks;
            int maxX = TerrainDataManager.CHUNK_MAX_X;
            int maxY = TerrainDataManager.CHUNK_MAX_Y;
            int maxZ = TerrainDataManager.CHUNK_MAX_Z;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    for (int y = 0; y < maxY; y++)
                    {
                        ushort block = blocks[x, y, z];
                        if (block == emptyBlock) continue;

                        int facesAdded = 0;

                        int wx = (int)chunkWorldPosition.X + x;
                        int wy = (int)chunkWorldPosition.Y + y;
                        int wz = (int)chunkWorldPosition.Z + z;

                        ByteVector3 bp = new ByteVector3 { x = (byte)x, y = (byte)y, z = (byte)z };

                        // LEFT (x-1)
                        if (x > 0)
                        {
                            if (blocks[x - 1, y, z] == emptyBlock)
                            {
                                IntegrateFace(block, Faces.LEFT, bp);
                                facesAdded++;
                            }
                        }
                        else
                        {
                            if (getWorldBlock(wx - 1, wy, wz) == emptyBlock)
                            {
                                IntegrateFace(block, Faces.LEFT, bp);
                                facesAdded++;
                            }
                        }

                        // RIGHT (x+1)
                        if (x < maxX - 1)
                        {
                            if (blocks[x + 1, y, z] == emptyBlock)
                            {
                                IntegrateFace(block, Faces.RIGHT, bp);
                                facesAdded++;
                            }
                        }
                        else
                        {
                            if (getWorldBlock(wx + 1, wy, wz) == emptyBlock)
                            {
                                IntegrateFace(block, Faces.RIGHT, bp);
                                facesAdded++;
                            }
                        }

                        // TOP (y+1)
                        if (y < maxY - 1)
                        {
                            if (blocks[x, y + 1, z] == emptyBlock)
                            {
                                IntegrateFace(block, Faces.TOP, bp);
                                facesAdded++;
                            }
                        }
                        else
                        {
                            if (getWorldBlock(wx, wy + 1, wz) == emptyBlock)
                            {
                                IntegrateFace(block, Faces.TOP, bp);
                                facesAdded++;
                            }
                        }

                        // BOTTOM (y-1)
                        if (y > 0)
                        {
                            if (blocks[x, y - 1, z] == emptyBlock)
                            {
                                IntegrateFace(block, Faces.BOTTOM, bp);
                                facesAdded++;
                            }
                        }
                        else
                        {
                            if (getWorldBlock(wx, wy - 1, wz) == emptyBlock)
                            {
                                IntegrateFace(block, Faces.BOTTOM, bp);
                                facesAdded++;
                            }
                        }

                        // FRONT (z+1)
                        if (z < maxZ - 1)
                        {
                            if (blocks[x, y, z + 1] == emptyBlock)
                            {
                                IntegrateFace(block, Faces.FRONT, bp);
                                facesAdded++;
                            }
                        }
                        else
                        {
                            if (getWorldBlock(wx, wy, wz + 1) == emptyBlock)
                            {
                                IntegrateFace(block, Faces.FRONT, bp);
                                facesAdded++;
                            }
                        }

                        // BACK (z-1)
                        if (z > 0)
                        {
                            if (blocks[x, y, z - 1] == emptyBlock)
                            {
                                IntegrateFace(block, Faces.BACK, bp);
                                facesAdded++;
                            }
                        }
                        else
                        {
                            if (getWorldBlock(wx, wy, wz - 1) == emptyBlock)
                            {
                                IntegrateFace(block, Faces.BACK, bp);
                                facesAdded++;
                            }
                        }

                        if (facesAdded > 0)
                        {
                            AddIndices(facesAdded);
                        }
                    }
                }
            }
        }
    }
}

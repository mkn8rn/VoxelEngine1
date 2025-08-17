using MVGE_GFX.BufferObjects;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static OpenTK.Graphics.OpenGL.GL;

namespace MVGE_GFX.Terrain
{
    public class MeshRender
    {
        bool isBuilt = false;
        private Vector3 chunkWorldPosition;

        private List<byte> chunkVerts;
        private List<byte> chunkUVs;
        private List<uint> chunkIndices;

        private uint indexCount;
        VAO chunkVAO;
        VBO chunkVertexVBO;
        VBO chunkUVVBO;
        IBO chunkIBO;
        public static BlockTextureAtlas terrainTextureAtlas { get; set; }
        public MeshRender(ChunkData chunkData)
        {
            this.chunkUVs = new List<byte>();
            this.chunkVerts = new List<byte>();
            this.chunkIndices = new List<uint>();

            this.chunkWorldPosition = new Vector3(chunkData.x, chunkData.y, chunkData.z);

            GenerateFaces(chunkData);
        }

        public void IntegrateFace(ushort block, Faces face, ByteVector3 blockPosition)
        {
            List<ByteVector2> blockUVs = new List<ByteVector2>();

            if (block != 0)
            {
                blockUVs = terrainTextureAtlas.GetBlockUVs(block, face);
            }

            FaceData faceData = new FaceData
            {
                vertices = AddTransformedVertices(RawFaceData.rawVertexData[face], blockPosition),
                uvs = blockUVs
            };

            List<byte> verticesList = new List<byte>();
            foreach (var vert in faceData.vertices)
            {
                verticesList.Add(vert.x);
                verticesList.Add(vert.y);
                verticesList.Add(vert.z);
            }

            List<byte> uvsList = new List<byte>();
            foreach (var uv in faceData.uvs)
            {
                uvsList.Add(uv.x);
                uvsList.Add(uv.y);
            }

            chunkVerts.AddRange(verticesList);
            chunkUVs.AddRange(uvsList);
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
            List<ByteVector3> transformedVertices = new List<ByteVector3>();
            foreach (var vert in vertices)
            {
                ByteVector3 scaledVert = new ByteVector3
                {
                    x = (byte)(vert.x + blockPosition.x),
                    y = (byte)(vert.y + blockPosition.y),
                    z = (byte)(vert.z + blockPosition.z),
                };

                transformedVertices.Add(scaledVert);
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
            Vector3 adjustedChunkPosition = this.chunkWorldPosition + coordsAdjustment;

            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);
            //todo: program.SetUniform("blockSize", TerrainDataLoader.BLOCK_SIZE);

            chunkVAO.Bind();
            chunkIBO.Bind();

            GL.DrawElements(PrimitiveType.Triangles, chunkIndices.Count, DrawElementsType.UnsignedInt, 0);
        }

        public void Delete()
        {
            chunkVAO.Delete();
            chunkVertexVBO.Delete();
            chunkUVVBO.Delete();
            chunkIBO.Delete();
        }

        public void GenerateFaces(ChunkData chunkData)
        {
            ushort emptyBlock = (ushort)BaseBlockType.Empty;

            for (int x = 0; x < TerrainDataLoader.CHUNK_SIZE; x++)
            {
                for (int z = 0; z < TerrainDataLoader.CHUNK_SIZE; z++)
                {
                    for (int y = 0; y < TerrainDataLoader.CHUNK_MAX_HEIGHT; y++)
                    {
                        int numFaces = 0;
                        ByteVector3 blockPosition = new ByteVector3 { x = (byte)x, y = (byte)y, z = (byte)z };

                        if (chunkData.blocks[x, y, z] == emptyBlock)
                        {
                            continue;
                        }

                        // Left Faces
                        if (x > 0)
                        {
                            if (chunkData.blocks[x - 1, y, z] == emptyBlock)
                            {
                                IntegrateFace(chunkData.blocks[x, y, z], Faces.LEFT, blockPosition);
                                numFaces++;
                            }
                        }
                        else
                        {
                            IntegrateFace(chunkData.blocks[x, y, z], Faces.LEFT, blockPosition);
                            numFaces++;
                        }

                        // Right Faces
                        if (x < TerrainDataLoader.CHUNK_SIZE - 1)
                        {
                            if (chunkData.blocks[x + 1, y, z] == emptyBlock)
                            {
                                IntegrateFace(chunkData.blocks[x, y, z], Faces.RIGHT, blockPosition);
                                numFaces++;
                            }
                        }
                        else
                        {
                            IntegrateFace(chunkData.blocks[x, y, z], Faces.RIGHT, blockPosition);
                            numFaces++;
                        }

                        //Top Faces
                        if (y < TerrainDataLoader.CHUNK_MAX_HEIGHT - 1)
                        {
                            if (chunkData.blocks[x, y + 1, z] == emptyBlock)
                            {
                                IntegrateFace(chunkData.blocks[x, y, z], Faces.TOP, blockPosition);
                                numFaces++;
                            }
                        }
                        else
                        {
                            IntegrateFace(chunkData.blocks[x, y, z], Faces.TOP, blockPosition);
                            numFaces++;
                        }

                        // Bottom Faces
                        if (y > 0)
                        {
                            if (chunkData.blocks[x, y - 1, z] == emptyBlock)
                            {
                                IntegrateFace(chunkData.blocks[x, y, z], Faces.BOTTOM, blockPosition);
                                numFaces++;
                            }
                        }
                        else
                        {
                            IntegrateFace(chunkData.blocks[x, y, z], Faces.BOTTOM, blockPosition);
                            numFaces++;
                        }

                        // Front Faces
                        if (z < TerrainDataLoader.CHUNK_SIZE - 1)
                        {
                            if (chunkData.blocks[x, y, z + 1] == emptyBlock)
                            {
                                IntegrateFace(chunkData.blocks[x, y, z], Faces.FRONT, blockPosition);
                                numFaces++;
                            }
                        }
                        else
                        {
                            IntegrateFace(chunkData.blocks[x, y, z], Faces.FRONT, blockPosition);
                            numFaces++;
                        }

                        // Back Faces
                        if (z > 0)
                        {
                            if (chunkData.blocks[x, y, z - 1] == emptyBlock)
                            {
                                IntegrateFace(chunkData.blocks[x, y, z], Faces.BACK, blockPosition);
                                numFaces++;
                            }
                        }
                        else
                        {
                            IntegrateFace(chunkData.blocks[x, y, z], Faces.BACK, blockPosition);
                            numFaces++;
                        }
                        AddIndices(numFaces);
                    }
                }
            }
        }
    }
}

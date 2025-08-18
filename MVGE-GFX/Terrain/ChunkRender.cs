using MVGE_GFX.BufferObjects;
using MVGE_GFX.Models;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

        private readonly ChunkData chunkMeta;
        private readonly Func<int, int, int, ushort> getWorldBlock;
        private readonly Func<int, int, int, ushort> getLocalBlock;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;
        private List<ushort> chunkIndicesUShort;

        public ChunkRender(
            ChunkData chunkData,
            Func<int, int, int, ushort> worldBlockGetter,
            Func<int, int, int, ushort> localBlockGetter)
        {
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            getLocalBlock = localBlockGetter;

            chunkVerts = new List<byte>();
            chunkUVs = new List<byte>();
            chunkIndices = new List<uint>();

            chunkWorldPosition = new Vector3(chunkData.x, chunkData.y, chunkData.z);

            GenerateFacesParallel();
        }

        public void IntegrateFace(ushort block, Faces face, ByteVector3 blockPosition)
        {
            var blockUVs = block != emptyBlock
                ? terrainTextureAtlas.GetBlockUVs(block, face)
                : new List<ByteVector2>();

            var faceData = new FaceData
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

        private void AddIndices(int faces)
        {
            for (int i = 0; i < faces; i++)
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

        public List<ByteVector3> AddTransformedVertices(List<ByteVector3> verts, ByteVector3 bp)
        {
            var list = new List<ByteVector3>(verts.Count);
            foreach (var v in verts)
            {
                list.Add(new ByteVector3
                {
                    x = (byte)(v.x + bp.x),
                    y = (byte)(v.y + bp.y),
                    z = (byte)(v.z + bp.z)
                });
            }
            return list;
        }

        public void Build()
        {
            TryFinalizeIndexFormat();

            chunkVAO = new VAO();
            chunkVAO.Bind();

            chunkVertexVBO = new VBO(chunkVerts);
            chunkVertexVBO.Bind();
            chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

            chunkUVVBO = new VBO(chunkUVs);
            chunkUVVBO.Bind();
            chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

            chunkIBO = indexFormat == IndexFormat.UShort
                ? new IBO(chunkIndicesUShort)
                : new IBO(chunkIndices);

            isBuilt = true;
        }

        public void Render(ShaderProgram program)
        {
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
                indexFormat == IndexFormat.UShort ? chunkIndicesUShort.Count : chunkIndices.Count,
                indexFormat == IndexFormat.UShort ? DrawElementsType.UnsignedShort : DrawElementsType.UnsignedInt,
                0);
        }

        private void GenerateFacesParallel()
        {
            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;

            if (maxX * maxY * maxZ < 16_000)
            {
                GenerateFacesSingle();
                return;
            }

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

                            // Check if the left face is empty
                            if ((x > 0 && getLocalBlock(x - 1, y, z) == emptyBlock) 
                                || (x == 0 && getWorldBlock(wx - 1, wy, wz) == emptyBlock))
                            {
                                local.Add(new PendingFace(block, Faces.LEFT, (byte)x, (byte)y, (byte)z));
                            }

                            // Check if the right face is empty
                            if ((x < maxX - 1 && getLocalBlock(x + 1, y, z) == emptyBlock) 
                                || (x == maxX - 1 && getWorldBlock(wx + 1, wy, wz) == emptyBlock))
                            {
                                local.Add(new PendingFace(block, Faces.RIGHT, (byte)x, (byte)y, (byte)z));
                            }

                            // Check if the top face is empty
                            if ((y < maxY - 1 && getLocalBlock(x, y + 1, z) == emptyBlock) 
                                || (y == maxY - 1 && getWorldBlock(wx, wy + 1, wz) == emptyBlock))
                            {  
                                local.Add(new PendingFace(block, Faces.TOP, (byte)x, (byte)y, (byte)z));
                            }

                            // Check if the bottom face is empty
                            if ((y > 0 && getLocalBlock(x, y - 1, z) == emptyBlock) 
                                || (y == 0 && getWorldBlock(wx, wy - 1, wz) == emptyBlock))
                            {
                                local.Add(new PendingFace(block, Faces.BOTTOM, (byte)x, (byte)y, (byte)z));
                            }

                            // Check if the front face is empty
                            if ((z < maxZ - 1 && getLocalBlock(x, y, z + 1) == emptyBlock) 
                                || (z == maxZ - 1 && getWorldBlock(wx, wy, wz + 1) == emptyBlock))
                            {
                                local.Add(new PendingFace(block, Faces.FRONT, (byte)x, (byte)y, (byte)z));
                            }

                            // Check if the back face is empty
                            if ((z > 0 && getLocalBlock(x, y, z - 1) == emptyBlock) ||
                                (z == 0 && getWorldBlock(wx, wy, wz - 1) == emptyBlock))
                            {
                                local.Add(new PendingFace(block, Faces.BACK, (byte)x, (byte)y, (byte)z));
                            }
                        }
                    }
                }
                faceLists[s] = local;
            });

            int totalFaces = 0;
            foreach (var fl in faceLists) totalFaces += fl.Count;
            chunkVerts.Capacity = totalFaces * 12;
            chunkUVs.Capacity = totalFaces * 8;

            foreach (var list in faceLists)
            {
                foreach (var pf in list)
                {
                    var bp = new ByteVector3 { x = pf.X, y = pf.Y, z = pf.Z };
                    IntegrateFace(pf.Block, pf.Face, bp);
                    AddIndices(1);
                }
            }
        }

        private void GenerateFacesSingle()
        {
            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;

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
                int faces = 0;

                // Check if the left face is empty
                if ((x > 0 && getLocalBlock(x - 1, y, z) == emptyBlock) 
                   || (x == 0 && getWorldBlock(wx - 1, wy, wz) == emptyBlock))
                { 
                    IntegrateFace(block, Faces.LEFT, bp); 
                    faces++; 
                }

                // Check if the right face is empty
                if ((x < maxX - 1 && getLocalBlock(x + 1, y, z) == emptyBlock) 
                   || (x == maxX - 1 && getWorldBlock(wx + 1, wy, wz) == emptyBlock))
                { 
                   IntegrateFace(block, Faces.RIGHT, bp); 
                   faces++;
                }

                // Check if the top face is empty
                if ((y < maxY - 1 && getLocalBlock(x, y + 1, z) == emptyBlock) 
                   || (y == maxY - 1 && getWorldBlock(wx, wy + 1, wz) == emptyBlock))
                { 
                    IntegrateFace(block, Faces.TOP, bp); 
                    faces++; 
                }

                // Check if the bottom face is empty
                if ((y > 0 && getLocalBlock(x, y - 1, z) == emptyBlock) 
                   || (y == 0 && getWorldBlock(wx, wy - 1, wz) == emptyBlock))
                { 
                    IntegrateFace(block, Faces.BOTTOM, bp); 
                    faces++; 
                }

                // Check if the front face is empty
                if ((z < maxZ - 1 && getLocalBlock(x, y, z + 1) == emptyBlock) 
                   || (z == maxZ - 1 && getWorldBlock(wx, wy, wz + 1) == emptyBlock))
                { 
                    IntegrateFace(block, Faces.FRONT, bp); 
                    faces++; 
                }
                
                // Check if the back face is empty
                if ((z > 0 && getLocalBlock(x, y, z - 1) == emptyBlock) 
                   || (z == 0 && getWorldBlock(wx, wy, wz - 1) == emptyBlock))
                { 
                    IntegrateFace(block, Faces.BACK, bp); 
                    faces++; 
                }

                if (faces > 0) AddIndices(faces);
            }
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

        private void TryFinalizeIndexFormat()
        {
            if (isBuilt) return;
            int vertCount = chunkVerts.Count / 3;
            if (vertCount <= 65535)
            {
                indexFormat = IndexFormat.UShort;
                chunkIndicesUShort = new List<ushort>(chunkIndices.Count);
                foreach (var i in chunkIndices) chunkIndicesUShort.Add((ushort)i);
                chunkIndices.Clear();
            }
            else
            {
                indexFormat = IndexFormat.UInt;
            }
        }

        private readonly struct PendingFace
        {
            public readonly ushort Block;
            public readonly Faces Face;
            public readonly byte X, Y, Z;
            public PendingFace(ushort b, Faces f, byte x, byte y, byte z)
            {
                Block = b; Face = f; X = x; Y = y; Z = z;
            }
        }
    }
}

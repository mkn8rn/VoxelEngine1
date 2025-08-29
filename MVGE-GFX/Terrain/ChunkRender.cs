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
using MVGE_GFX.Textures;
using MVGE_INF.Models.Generation;
using MVGE_GFX.Terrain.Sections;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        private bool isBuilt = false;
        private Vector3 chunkWorldPosition;
        private byte[] vertBuffer;
        private byte[] uvBuffer;
        private uint[] indicesUIntBuffer;
        private ushort[] indicesUShortBuffer;
        private int vertBytesUsed;
        private int uvBytesUsed;
        private int indicesUsed;
        private bool useUShort;

        private VAO chunkVAO;
        private VBO chunkVertexVBO;
        private VBO chunkUVVBO;
        private IBO chunkIBO;

        public static BlockTextureAtlas terrainTextureAtlas { get; set; }

        private readonly ChunkData chunkMeta;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;
        private readonly int maxX; private readonly int maxY; private readonly int maxZ;
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;
        private readonly bool allOneBlock; private readonly ushort allOneBlockId;
        private readonly int prepassSolidCount; private readonly int prepassExposureEstimate;
        private readonly ChunkPrerenderData prerenderData;
        private bool fullyOccluded;

        private SectionRender sectionRender; // new renderer abstraction

        public ChunkRender(ChunkPrerenderData prerenderData)
        {
            this.prerenderData = prerenderData;
            this.prepassSolidCount = prerenderData.PrepassSolidCount;
            this.prepassExposureEstimate = prerenderData.PrepassExposureEstimate;
            this.chunkMeta = prerenderData.chunkData;
            this.maxX = prerenderData.maxX; this.maxY = prerenderData.maxY; this.maxZ = prerenderData.maxZ;
            chunkWorldPosition = new Vector3(prerenderData.chunkData.x, prerenderData.chunkData.y, prerenderData.chunkData.z);
            faceNegX = prerenderData.FaceNegX; facePosX = prerenderData.FacePosX; faceNegY = prerenderData.FaceNegY; facePosY = prerenderData.FacePosY; faceNegZ = prerenderData.FaceNegZ; facePosZ = prerenderData.FacePosZ;
            nNegXPosX = prerenderData.NeighborNegXPosX; nPosXNegX = prerenderData.NeighborPosXNegX; nNegYPosY = prerenderData.NeighborNegYPosY; nPosYNegY = prerenderData.NeighborPosYNegY; nNegZPosZ = prerenderData.NeighborNegZPosZ; nPosZNegZ = prerenderData.NeighborPosZNegZ;
            allOneBlock = prerenderData.AllOneBlock; allOneBlockId = prerenderData.AllOneBlockId;
            GenerateFaces();
        }

        private void GenerateFaces()
        {
            // Full enclosure early exit using face flags + neighbor flags (simple conservative check)
            if (prepassSolidCount > 0 && faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                fullyOccluded = true; return;
            }

            // Build section-level renderer abstraction (no legacy flattened array path)
            sectionRender = new SectionRender(prerenderData, terrainTextureAtlas);
            sectionRender.Build(out vertBuffer, out uvBuffer, out indicesUShortBuffer, out indicesUIntBuffer, out useUShort, out vertBytesUsed, out uvBytesUsed, out indicesUsed);
        }

        public void Build()
        {
            if (isBuilt) return;
            if (fullyOccluded)
            {
                chunkVAO = new VAO(); chunkVAO.Bind();
                chunkVertexVBO = new VBO(Array.Empty<byte>(), 0); chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);
                chunkUVVBO = new VBO(Array.Empty<byte>(), 0); chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);
                chunkIBO = new IBO(Array.Empty<uint>(), 0); isBuilt = true; return;
            }

            chunkVAO = new VAO(); chunkVAO.Bind();
            chunkVertexVBO = new VBO(vertBuffer ?? Array.Empty<byte>(), vertBytesUsed); chunkVertexVBO.Bind();
            chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);
            chunkUVVBO = new VBO(uvBuffer ?? Array.Empty<byte>(), uvBytesUsed); chunkUVVBO.Bind();
            chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);
            chunkIBO = useUShort ? new IBO(indicesUShortBuffer ?? Array.Empty<ushort>(), indicesUsed) : new IBO(indicesUIntBuffer ?? Array.Empty<uint>(), indicesUsed);
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
            int count = chunkIBO.Count; if (count <= 0) return;
            GL.DrawElements(PrimitiveType.Triangles, count, useUShort ? DrawElementsType.UnsignedShort : DrawElementsType.UnsignedInt, 0);
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
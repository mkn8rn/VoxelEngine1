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
using System.Linq;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        private bool isBuilt = false;
        private Vector3 chunkWorldPosition;

        private byte[] instanceOffsetBuffer; // 3 bytes per face
        private uint[] instanceTileIndexBuffer; // 1 uint per face
        private byte[] instanceFaceDirBuffer; // 1 byte per face
        private int instanceCount;

        private VAO chunkVAO;
        private VBO quadPosVBO;
        private VBO quadUVVBO;
        private VBO instanceOffsetVBO;
        private VBO instanceTileIndexVBO;
        private VBO instanceFaceDirVBO;
        private IBO quadIndexIBO; // index buffer for the shared quad

        public static BlockTextureAtlas terrainTextureAtlas { get; set; }

        private readonly ChunkData chunkMeta;
        private readonly int maxX; private readonly int maxY; private readonly int maxZ;
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;
        private readonly bool allOneBlock; private readonly ushort allOneBlockId;
        private readonly int prepassSolidCount; private readonly int prepassExposureEstimate;
        private readonly ChunkPrerenderData prerenderData;
        private bool fullyOccluded;

        private SectionRender sectionRender;

        // Static quad data (positions & base UVs 0..1) reused for all faces.
        private static readonly byte[] QuadPositions = new byte[]
        {
            0,0,0,  1,0,0,  1,1,0,  0,1,0 // a flat unit quad in XY plane; orientation adjusted in shader using faceDir
        };
        private static readonly byte[] QuadUVs = new byte[]
        {
            0,0, 1,0, 1,1, 0,1
        };
        // If you're reading this, you need to know:
        // Must be like this due to vertex shader's row-major style: vec4(pos)*model*view*projection
        // Usually we use column-major: projection*view*model*vec4(pos)
        // That mismatch mirrors geometry turning front faces into back faces,
        // Which is why our indices are flipped from how they normally are (0,1,2,0,2,3 -> 0,2,1,0,3,2)
        private static readonly ushort[] QuadIndices = new ushort[] { 0, 2, 1, 0, 3, 2 }; // two triangles

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
            if (prepassSolidCount > 0 && faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                fullyOccluded = true; return;
            }

            sectionRender = new SectionRender(prerenderData, terrainTextureAtlas);
            sectionRender.Build(out instanceCount, out instanceOffsetBuffer, out instanceTileIndexBuffer, out instanceFaceDirBuffer);
        }

        public void Build()
        {
            if (isBuilt) return;
            chunkVAO = new VAO(); chunkVAO.Bind();

            // Static quad position VBO (location 0)
            quadPosVBO = new VBO(QuadPositions, QuadPositions.Length); quadPosVBO.Bind();
            chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, quadPosVBO);

            // Instance offsets (location 2)
            instanceOffsetVBO = new VBO(instanceOffsetBuffer ?? Array.Empty<byte>(), instanceOffsetBuffer?.Length ?? 0); instanceOffsetVBO.Bind();
            chunkVAO.LinkToVAO(2, 3, VertexAttribPointerType.UnsignedByte, false, instanceOffsetVBO);
            chunkVAO.SetDivisor(2, 1);

            // Instance tile indices (location 3) - pack uints into byte[] for VBO
            byte[] tileBytes;
            if (instanceTileIndexBuffer == null || instanceTileIndexBuffer.Length == 0)
            {
                tileBytes = Array.Empty<byte>();
            }
            else
            {
                tileBytes = new byte[instanceTileIndexBuffer.Length * sizeof(uint)];
                System.Buffer.BlockCopy(instanceTileIndexBuffer, 0, tileBytes, 0, tileBytes.Length);
            }
            instanceTileIndexVBO = new VBO(tileBytes, tileBytes.Length); instanceTileIndexVBO.Bind();
            chunkVAO.LinkIntegerToVAO(3, 1, VertexAttribIntegerType.UnsignedInt, instanceTileIndexVBO);
            chunkVAO.SetDivisor(3, 1);

            // Instance face directions (location 4) 
            var faceDirBytes = instanceFaceDirBuffer ?? Array.Empty<byte>();
            instanceFaceDirVBO = new VBO(faceDirBytes, faceDirBytes.Length); instanceFaceDirVBO.Bind();
            chunkVAO.LinkIntegerToVAO(4, 1, VertexAttribIntegerType.UnsignedByte, instanceFaceDirVBO);
            chunkVAO.SetDivisor(4, 1);

            // Index buffer for quad
            quadIndexIBO = new IBO(QuadIndices, QuadIndices.Length);

            isBuilt = true;
        }

        public void Render(ShaderProgram program)
        {
            ProcessPendingDeletes();
            if (!isBuilt) Build();
            if (fullyOccluded || instanceCount == 0) return;
            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);
            chunkVAO.Bind();
            quadIndexIBO.Bind();
            GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, instanceCount);
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
            quadPosVBO.Delete();
            instanceOffsetVBO.Delete();
            instanceTileIndexVBO.Delete();
            instanceFaceDirVBO.Delete();
            quadIndexIBO.Delete();
            isBuilt = false;
        }
    }
}
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

        // Opaque instance data
        private byte[] instanceOffsetBuffer; // 3 bytes per face (opaque)
        private uint[] instanceTileIndexBuffer; // 1 uint per face (opaque)
        private byte[] instanceFaceDirBuffer; // 1 byte per face (opaque)
        private int instanceCount;            // opaque instance count

        // Transparent instance data groundwork (emitted by SectionRender fallback currently)
        private byte[] transparentInstanceOffsetBuffer;   // 3 bytes per face (transparent)
        private uint[] transparentInstanceTileIndexBuffer; // 1 uint per face (transparent)
        private byte[] transparentInstanceFaceDirBuffer;   // 1 byte per face (transparent)
        private int transparentInstanceCount;              // transparent instance count

        private VAO chunkVAO;
        private VBO quadPosVBO;
        private VBO instanceOffsetVBO;        // opaque offsets (attrib 2)
        private VBO instanceTileIndexVBO;     // opaque tile indices (attrib 3)
        private VBO instanceFaceDirVBO;       // opaque face dirs (attrib 4)
        private VBO transparentOffsetVBO;     // transparent offsets (attrib 5)
        private VBO transparentTileIndexVBO;  // transparent tile indices (attrib 6)
        private VBO transparentFaceDirVBO;    // transparent face dirs (attrib 7)
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
            sectionRender.Build(out instanceCount, out instanceOffsetBuffer, out instanceTileIndexBuffer, out instanceFaceDirBuffer,
                                out transparentInstanceCount, out transparentInstanceOffsetBuffer, out transparentInstanceTileIndexBuffer, out transparentInstanceFaceDirBuffer);
        }

        public void Build()
        {
            if (isBuilt) return;
            chunkVAO = new VAO(); chunkVAO.Bind();

            // Static quad position VBO (location 0)
            quadPosVBO = new VBO(QuadPositions, QuadPositions.Length); quadPosVBO.Bind();
            chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, quadPosVBO);

            // Instance offsets (opaque, location 2)
            instanceOffsetVBO = new VBO(instanceOffsetBuffer ?? Array.Empty<byte>(), instanceOffsetBuffer?.Length ?? 0); instanceOffsetVBO.Bind();
            chunkVAO.LinkToVAO(2, 3, VertexAttribPointerType.UnsignedByte, false, instanceOffsetVBO);
            chunkVAO.SetDivisor(2, 1);

            // Instance tile indices (opaque, location 3) - pack uints into byte[] for VBO
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

            // Instance face directions (opaque, location 4) 
            var faceDirBytes = instanceFaceDirBuffer ?? Array.Empty<byte>();
            instanceFaceDirVBO = new VBO(faceDirBytes, faceDirBytes.Length); instanceFaceDirVBO.Bind();
            chunkVAO.LinkIntegerToVAO(4, 1, VertexAttribIntegerType.UnsignedByte, instanceFaceDirVBO);
            chunkVAO.SetDivisor(4, 1);

            // Transparent instance attribute set (locations 5,6,7) only when we actually have transparent faces.
            if (transparentInstanceCount > 0)
            {
                // Offsets (transparent, location 5)
                transparentOffsetVBO = new VBO(transparentInstanceOffsetBuffer ?? Array.Empty<byte>(), transparentInstanceOffsetBuffer?.Length ?? 0); transparentOffsetVBO.Bind();
                chunkVAO.LinkToVAO(5, 3, VertexAttribPointerType.UnsignedByte, false, transparentOffsetVBO);
                chunkVAO.SetDivisor(5, 1);

                // Tile indices (transparent, location 6) – same packing pattern as opaque
                byte[] tTileBytes;
                if (transparentInstanceTileIndexBuffer == null || transparentInstanceTileIndexBuffer.Length == 0)
                {
                    tTileBytes = Array.Empty<byte>();
                }
                else
                {
                    tTileBytes = new byte[transparentInstanceTileIndexBuffer.Length * sizeof(uint)];
                    System.Buffer.BlockCopy(transparentInstanceTileIndexBuffer, 0, tTileBytes, 0, tTileBytes.Length);
                }
                transparentTileIndexVBO = new VBO(tTileBytes, tTileBytes.Length); transparentTileIndexVBO.Bind();
                chunkVAO.LinkIntegerToVAO(6, 1, VertexAttribIntegerType.UnsignedInt, transparentTileIndexVBO);
                chunkVAO.SetDivisor(6, 1);

                // Face dirs (transparent, location 7)
                var tFaceDirBytes = transparentInstanceFaceDirBuffer ?? Array.Empty<byte>();
                transparentFaceDirVBO = new VBO(tFaceDirBytes, tFaceDirBytes.Length); transparentFaceDirVBO.Bind();
                chunkVAO.LinkIntegerToVAO(7, 1, VertexAttribIntegerType.UnsignedByte, transparentFaceDirVBO);
                chunkVAO.SetDivisor(7, 1);
            }

            // Index buffer for quad
            quadIndexIBO = new IBO(QuadIndices, QuadIndices.Length);

            isBuilt = true;
        }

        public void Render(ShaderProgram program)
        {
            ProcessPendingDeletes();
            if (!isBuilt) Build();

            // Only skip when there is nothing at all to draw (no opaque AND no transparent),
            // or when the chunk was classified as fully occluded.
            if (fullyOccluded || (instanceCount == 0 && transparentInstanceCount == 0))
                return;

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);
            chunkVAO.Bind();
            quadIndexIBO.Bind();

            // ----- OPAQUE PASS (only if we actually have opaque instances) -----
            if (instanceCount > 0)
            {
                program.SetUniform("useTransparentList", 0); // use opaque attribute set (locations 2,3,4)
                GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, instanceCount);
            }

            // ----- TRANSPARENT PASS -----
            if (transparentInstanceCount > 0)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.DepthMask(false);
                program.SetUniform("useTransparentList", 1); // use transparent attribute set (locations 5,6,7)
                GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, transparentInstanceCount);
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
            }
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
            transparentOffsetVBO?.Delete();
            transparentTileIndexVBO?.Delete();
            transparentFaceDirVBO?.Delete();
            quadIndexIBO.Delete();
            isBuilt = false;
        }
    }
}
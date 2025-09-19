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

        private VAO opaqueVAO;                // opaque pass VAO
        private VAO transparentVAO;           // transparent pass VAO
        private VBO quadPosVBO;               // shared static quad positions (attrib 0)
        private VBO instanceOffsetVBO;        // opaque offsets (attrib 2)
        private VBO instanceTileIndexVBO;     // opaque tile indices (attrib 3)
        private VBO instanceFaceDirVBO;       // opaque face dirs (attrib 4)
        private VBO transparentOffsetVBO;     // transparent offsets (attrib 5)
        private VBO transparentTileIndexVBO;  // transparent tile indices (attrib 6)
        private VBO transparentFaceDirVBO;    // transparent face dirs (attrib 7)
        private IBO quadIndexIBO; // index buffer for the shared quad

        // Built flags for each buffer object; ensure deletion only when created.
        private bool opaqueVaoBuilt;
        private bool transparentVaoBuilt;
        private bool quadPosBuilt;
        private bool instanceOffsetBuilt;
        private bool instanceTileIndexBuilt;
        private bool instanceFaceDirBuilt;
        private bool transparentOffsetBuilt;
        private bool transparentTileIndexBuilt;
        private bool transparentFaceDirBuilt;
        private bool quadIndexBuilt;

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

            // Shared index buffer (bind per-VAO after VAO bind to attach)
            quadIndexIBO = new IBO(QuadIndices, QuadIndices.Length);
            quadIndexBuilt = true;

            // Shared static quad position VBO
            quadPosVBO = new VBO(QuadPositions, QuadPositions.Length);
            quadPosBuilt = true;

            // ----- OPAQUE VAO -----
            if (instanceCount > 0)
            {
                opaqueVAO = new VAO();
                opaqueVAO.Bind();

                // position (location 0)
                quadPosVBO.Bind();
                opaqueVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, quadPosVBO);

                // Instance offsets (location 2)
                instanceOffsetVBO = new VBO(instanceOffsetBuffer ?? Array.Empty<byte>(), instanceOffsetBuffer?.Length ?? 0);
                opaqueVAO.LinkToVAO(2, 3, VertexAttribPointerType.UnsignedByte, false, instanceOffsetVBO);
                opaqueVAO.SetDivisor(2, 1);
                instanceOffsetBuilt = true;

                // Tile indices (location 3)
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
                instanceTileIndexVBO = new VBO(tileBytes, tileBytes.Length);
                opaqueVAO.LinkIntegerToVAO(3, 1, VertexAttribIntegerType.UnsignedInt, instanceTileIndexVBO);
                opaqueVAO.SetDivisor(3, 1);
                instanceTileIndexBuilt = true;

                // Face dirs (location 4)
                var faceDirBytes = instanceFaceDirBuffer ?? Array.Empty<byte>();
                instanceFaceDirVBO = new VBO(faceDirBytes, faceDirBytes.Length);
                opaqueVAO.LinkIntegerToVAO(4, 1, VertexAttribIntegerType.UnsignedByte, instanceFaceDirVBO);
                opaqueVAO.SetDivisor(4, 1);
                instanceFaceDirBuilt = true;

                // Attach IBO to this VAO
                quadIndexIBO.Bind();

                // Mark this VAO as built for safe deletion later.
                opaqueVaoBuilt = true;
            }

            // ----- TRANSPARENT VAO -----
            if (transparentInstanceCount > 0)
            {
                transparentVAO = new VAO();
                transparentVAO.Bind();

                // position (location 0)
                quadPosVBO.Bind();
                transparentVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, quadPosVBO);

                // Offsets (location 5)
                transparentOffsetVBO = new VBO(transparentInstanceOffsetBuffer ?? Array.Empty<byte>(), transparentInstanceOffsetBuffer?.Length ?? 0);
                transparentVAO.LinkToVAO(5, 3, VertexAttribPointerType.UnsignedByte, false, transparentOffsetVBO);
                transparentVAO.SetDivisor(5, 1);
                transparentOffsetBuilt = true;

                // Tile indices (location 6)
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
                transparentTileIndexVBO = new VBO(tTileBytes, tTileBytes.Length);
                transparentVAO.LinkIntegerToVAO(6, 1, VertexAttribIntegerType.UnsignedInt, transparentTileIndexVBO);
                transparentVAO.SetDivisor(6, 1);
                transparentTileIndexBuilt = true;

                // Face dirs (location 7)
                var tFaceDirBytes = transparentInstanceFaceDirBuffer ?? Array.Empty<byte>();
                transparentFaceDirVBO = new VBO(tFaceDirBytes, tFaceDirBytes.Length);
                transparentVAO.LinkIntegerToVAO(7, 1, VertexAttribIntegerType.UnsignedByte, transparentFaceDirVBO);
                transparentVAO.SetDivisor(7, 1);
                transparentFaceDirBuilt = true;

                // Attach IBO to this VAO
                quadIndexIBO.Bind();

                // Mark this VAO as built for safe deletion later.
                transparentVaoBuilt = true;
            }

            isBuilt = true;
        }

        public void Render(ShaderProgram program)
        {
            ProcessPendingDeletes();
            if (!isBuilt) Build();

            if (fullyOccluded || (instanceCount == 0 && transparentInstanceCount == 0))
                return;

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            // ----- OPAQUE PASS -----
            if (instanceCount > 0 && opaqueVAO != null)
            {
                opaqueVAO.Bind();
                quadIndexIBO.Bind(); // ensure IBO bound to this VAO if driver disassociates

                program.SetUniform("useTransparentList", 0f);
                GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, instanceCount);
            }

            // ----- TRANSPARENT PASS -----
            if (transparentInstanceCount > 0 && transparentVAO != null)
            {
                transparentVAO.Bind();
                quadIndexIBO.Bind(); // ensure IBO bound to this VAO

                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.DepthMask(false);
                program.SetUniform("useTransparentList", 1f);
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

            if (opaqueVaoBuilt) { opaqueVAO.Delete(); opaqueVAO = null; opaqueVaoBuilt = false; }
            if (transparentVaoBuilt) { transparentVAO.Delete(); transparentVAO = null; transparentVaoBuilt = false; }
            if (quadPosBuilt) { quadPosVBO.Delete(); quadPosVBO = null; quadPosBuilt = false; }
            if (instanceOffsetBuilt) { instanceOffsetVBO.Delete(); instanceOffsetVBO = null; instanceOffsetBuilt = false; }
            if (instanceTileIndexBuilt) { instanceTileIndexVBO.Delete(); instanceTileIndexVBO = null; instanceTileIndexBuilt = false; }
            if (instanceFaceDirBuilt) { instanceFaceDirVBO.Delete(); instanceFaceDirVBO = null; instanceFaceDirBuilt = false; }
            if (transparentOffsetBuilt) { transparentOffsetVBO.Delete(); transparentOffsetVBO = null; transparentOffsetBuilt = false; }
            if (transparentTileIndexBuilt) { transparentTileIndexVBO.Delete(); transparentTileIndexVBO = null; transparentTileIndexBuilt = false; }
            if (transparentFaceDirBuilt) { transparentFaceDirVBO.Delete(); transparentFaceDirVBO = null; transparentFaceDirBuilt = false; }
            if (quadIndexBuilt) { quadIndexIBO.Delete(); quadIndexIBO = null; quadIndexBuilt = false; }

            isBuilt = false;
        }
    }
}
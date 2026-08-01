using MVoxelEngine1.Graphics.BufferObjects;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Infrastructure.Managers;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers;
using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using Vector3 = OpenTK.Mathematics.Vector3;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Graphics.Terrain.Sections;
using System.Linq;
using System.Threading;

namespace MVoxelEngine1.Graphics.Terrain
{
    public partial class ChunkRender
    {
        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();
        private static long nextRenderDataId;

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
        private ChunkRenderUploadData uploadData;

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

        public static ReadOnlyMemory<byte> QuadPositionUploadData => QuadPositions;

        public static ReadOnlyMemory<ushort> QuadIndexUploadData => QuadIndices;

        public ChunkRenderUploadData UploadData => uploadData;

        public bool IsOpenGlUploaded => isBuilt;

        public ChunkRender(
            ChunkPrerenderData prerenderData,
            FaceGenerationMode faceGenerationMode,
            Func<int, int, int, ushort>? getLocalBlock,
            ReferenceNeighborBlockPlanes? referenceNeighbors)
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
            GenerateFaces(faceGenerationMode, getLocalBlock, referenceNeighbors);
            uploadData = new ChunkRenderUploadData(
                Interlocked.Increment(ref nextRenderDataId),
                chunkWorldPosition.X,
                chunkWorldPosition.Y,
                chunkWorldPosition.Z,
                fullyOccluded,
                faceGenerationMode,
                instanceCount,
                instanceOffsetBuffer,
                instanceTileIndexBuffer,
                instanceFaceDirBuffer,
                transparentInstanceCount,
                transparentInstanceOffsetBuffer,
                transparentInstanceTileIndexBuffer,
                transparentInstanceFaceDirBuffer);
        }

        private void GenerateFaces(
            FaceGenerationMode faceGenerationMode,
            Func<int, int, int, ushort>? getLocalBlock,
            ReferenceNeighborBlockPlanes? referenceNeighbors)
        {
            if (faceGenerationMode == FaceGenerationMode.Reference)
            {
                if (getLocalBlock is null)
                    throw new ArgumentNullException(nameof(getLocalBlock));
                if (referenceNeighbors is null)
                    throw new ArgumentNullException(nameof(referenceNeighbors));

                GenerateReferenceFaces(getLocalBlock, referenceNeighbors);
                return;
            }

            if (faceGenerationMode != FaceGenerationMode.Optimized)
                throw new ArgumentOutOfRangeException(nameof(faceGenerationMode));

            if (prepassSolidCount > 0 && faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                fullyOccluded = true; return;
            }

            sectionRender = new SectionRender(prerenderData, terrainTextureAtlas);
            sectionRender.Build(out instanceCount, out instanceOffsetBuffer, out instanceTileIndexBuffer, out instanceFaceDirBuffer,
                                out transparentInstanceCount, out transparentInstanceOffsetBuffer, out transparentInstanceTileIndexBuffer, out transparentInstanceFaceDirBuffer);
        }

        private void GenerateReferenceFaces(
            Func<int, int, int, ushort> getLocalBlock,
            ReferenceNeighborBlockPlanes referenceNeighbors)
        {
            ReferenceFaceGenerationResult faces = allOneBlock && allOneBlockId != 0
                ? ReferenceFaceGenerator.GenerateUniform(
                    maxX,
                    maxY,
                    maxZ,
                    allOneBlockId,
                    referenceNeighbors,
                    TerrainLoader.IsOpaque)
                : ReferenceFaceGenerator.GenerateSections(
                    maxX,
                    maxY,
                    maxZ,
                    getLocalBlock,
                    referenceNeighbors,
                    TerrainLoader.IsOpaque,
                    prerenderData.SectionDescs);

            instanceCount = faces.OpaqueFaceCount;
            instanceOffsetBuffer = faces.OpaqueOffsets;
            instanceFaceDirBuffer = faces.OpaqueDirections;
            instanceTileIndexBuffer = BuildReferenceTileIndices(
                faces.OpaqueBlockIds,
                faces.OpaqueDirections);

            transparentInstanceCount = faces.TransparentFaceCount;
            transparentInstanceOffsetBuffer = faces.TransparentOffsets;
            transparentInstanceFaceDirBuffer = faces.TransparentDirections;
            transparentInstanceTileIndexBuffer = BuildReferenceTileIndices(
                faces.TransparentBlockIds,
                faces.TransparentDirections);

            fullyOccluded = instanceCount == 0 && transparentInstanceCount == 0;
        }

        private static uint[] BuildReferenceTileIndices(
            ReadOnlySpan<ushort> blockIds,
            ReadOnlySpan<byte> directions)
        {
            if (blockIds.Length != directions.Length)
                throw new InvalidOperationException("Reference face arrays have different lengths.");

            var result = new uint[blockIds.Length];
            var cache = new Dictionary<int, uint>();
            for (int index = 0; index < result.Length; index++)
            {
                int key = (blockIds[index] << 3) | directions[index];
                if (!cache.TryGetValue(key, out uint tileIndex))
                {
                    tileIndex = SectionRender.ComputeTileIndex(
                        terrainTextureAtlas,
                        blockIds[index],
                        (Faces)directions[index]);
                    cache.Add(key, tileIndex);
                }

                result[index] = tileIndex;
            }

            return result;
        }

        public void Build()
        {
            if (isBuilt) return;

            StartupPerformanceRecorder.RecordGpuStreamingStart();

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

                // Explicitly disable transparent-only attributes on this VAO
                opaqueVAO.SetAttribEnabled(5, false);
                opaqueVAO.SetAttribEnabled(6, false);
                opaqueVAO.SetAttribEnabled(7, false);

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

                // Explicitly disable opaque-only attributes on this VAO
                transparentVAO.SetAttribEnabled(2, false);
                transparentVAO.SetAttribEnabled(3, false);
                transparentVAO.SetAttribEnabled(4, false);

                // Mark this VAO as built for safe deletion later.
                transparentVaoBuilt = true;
            }

            isBuilt = true;
            if (instanceCount != 0 || transparentInstanceCount != 0)
            {
                double? generationToRender =
                    StartupPerformanceRecorder.RecordGenerationToRender();
                if (generationToRender.HasValue)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"Generation to Render time (GTRT): {generationToRender.Value:R} ms."));
                }
            }
        }

        // Opaque pass: draws opaque face instances only. Depth test/write is managed by the caller.
        public void RenderOpaque(ShaderProgram program)
        {
            ProcessPendingDeletes();
            if (!isBuilt) Build();

            if (fullyOccluded || instanceCount == 0)
                return;

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            if (opaqueVAO != null)
            {
                opaqueVAO.Bind();
                quadIndexIBO.Bind(); // ensure IBO bound to this VAO if driver disassociates

                program.SetUniform("useTransparentList", 0f);
                GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, instanceCount);
            }
        }

        // Transparent pass: draws transparent face instances only with blending enabled.
        // Depth test is respected but this pass does not write depth; caller coordinates depth mask globally.
        public void RenderTransparent(ShaderProgram program)
        {
            ProcessPendingDeletes();
            if (!isBuilt) Build();

            if (fullyOccluded || transparentInstanceCount == 0)
                return;

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            if (transparentVAO != null)
            {
                transparentVAO.Bind();
                quadIndexIBO.Bind(); // ensure IBO bound to this VAO

                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                program.SetUniform("useTransparentList", 1f);
                GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, transparentInstanceCount);
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

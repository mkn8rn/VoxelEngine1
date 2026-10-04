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
using System.Linq;
using System.Threading;
using System.Diagnostics;

namespace MVoxelEngine1.Graphics.Terrain
{
    public partial class ChunkRender : INativeChunkRenderer
    {
        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        private bool isBuilt = false;
        private Vector3 chunkWorldPosition;

        private int opaqueFaceCount;
        private int opaqueRectangleCount;
        private int transparentFaceCount;
        private int transparentRectangleCount;
        private int deletionScheduled;

        private VAO? opaqueVAO;                // opaque pass VAO
        private VAO? transparentVAO;           // transparent pass VAO
        private VBO? quadPosVBO;               // shared static quad positions (attrib 0)
        private VBO? opaqueRectangleVBO;
        private VBO? transparentRectangleVBO;
        private IBO? quadIndexIBO; // index buffer for the shared quad

        // Built flags for each buffer object; ensure deletion only when created.
        private bool opaqueVaoBuilt;
        private bool transparentVaoBuilt;
        private bool quadPosBuilt;
        private bool opaqueRectangleBuilt;
        private bool transparentRectangleBuilt;
        private bool quadIndexBuilt;

        private static BlockTextureAtlas? sharedTerrainTextureAtlas;

        public static BlockTextureAtlas terrainTextureAtlas
        {
            get => sharedTerrainTextureAtlas ?? throw new InvalidOperationException("The terrain texture atlas has not been initialized.");
            set => sharedTerrainTextureAtlas = value ?? throw new ArgumentNullException(nameof(value));
        }

        private bool fullyOccluded;

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

        public bool IsOpenGlUploaded => isBuilt;

        private ChunkRender(
            in NativeChunkRenderPacketDescriptor descriptor)
        {
            chunkWorldPosition = new Vector3(
                descriptor.ChunkWorldX,
                descriptor.ChunkWorldY,
                descriptor.ChunkWorldZ);
            opaqueFaceCount = descriptor.OpaqueFaceCount;
            opaqueRectangleCount = descriptor.OpaqueRectangleCount;
            transparentFaceCount = descriptor.TransparentFaceCount;
            transparentRectangleCount =
                descriptor.TransparentRectangleCount;
            fullyOccluded = descriptor.IsEmpty;
        }

        public static ChunkRender? UploadNative(
            in NativeChunkRenderPacketDescriptor descriptor,
            ReadOnlySpan<uint> opaqueWords,
            ReadOnlySpan<uint> transparentWords)
        {
            if (descriptor.OpaqueWordCount != opaqueWords.Length ||
                descriptor.TransparentWordCount != transparentWords.Length)
            {
                throw new InvalidDataException(
                    "The native packet word ranges do not match its descriptor.");
            }
            if (descriptor.IsEmpty)
                return null;

            var renderer = new ChunkRender(in descriptor);
            try
            {
                renderer.BuildNative(opaqueWords, transparentWords);
                return renderer;
            }
            catch
            {
                renderer.DeleteGL();
                throw;
            }
        }

        private void BuildNative(
            ReadOnlySpan<uint> opaqueWords,
            ReadOnlySpan<uint> transparentWords)
        {
            StartupPerformanceRecorder.RecordGpuStreamingStart();

            quadIndexIBO = new IBO(QuadIndices, QuadIndices.Length);
            quadIndexBuilt = true;
            quadPosVBO = new VBO(QuadPositions, QuadPositions.Length);
            quadPosBuilt = true;

            if (opaqueRectangleCount > 0)
            {
                opaqueVAO = new VAO();
                opaqueVAO.Bind();
                quadPosVBO.Bind();
                opaqueVAO.LinkToVAO(
                    0,
                    3,
                    VertexAttribPointerType.UnsignedByte,
                    false,
                    quadPosVBO);
                opaqueRectangleVBO = new VBO(opaqueWords);
                opaqueVAO.LinkIntegerToVAO(
                    2,
                    PackedFaceRectangle.WordsPerRectangle,
                    VertexAttribIntegerType.UnsignedInt,
                    opaqueRectangleVBO);
                opaqueVAO.SetDivisor(2, 1);
                opaqueRectangleBuilt = true;
                quadIndexIBO.Bind();
                opaqueVAO.SetAttribEnabled(5, false);
                opaqueVaoBuilt = true;
            }

            if (transparentRectangleCount > 0)
            {
                transparentVAO = new VAO();
                transparentVAO.Bind();
                quadPosVBO.Bind();
                transparentVAO.LinkToVAO(
                    0,
                    3,
                    VertexAttribPointerType.UnsignedByte,
                    false,
                    quadPosVBO);
                transparentRectangleVBO = new VBO(
                    transparentWords,
                    RenderPass.Transparent);
                transparentVAO.LinkIntegerToVAO(
                    5,
                    PackedFaceRectangle.WordsPerRectangle,
                    VertexAttribIntegerType.UnsignedInt,
                    transparentRectangleVBO);
                transparentVAO.SetDivisor(5, 1);
                transparentRectangleBuilt = true;
                quadIndexIBO.Bind();
                transparentVAO.SetAttribEnabled(2, false);
                transparentVaoBuilt = true;
            }

            isBuilt = true;
        }

        // Opaque pass: draws opaque face instances only. Depth test/write is managed by the caller.
        public void RenderOpaque(ShaderProgram program)
        {
            ProcessPendingDeletes();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref deletionScheduled) != 0, this);
            if (!isBuilt) throw new InvalidOperationException("Native words must be uploaded before rendering.");

            if (fullyOccluded || opaqueRectangleCount == 0)
                return;

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            if (opaqueVAO != null)
            {
                opaqueVAO.Bind();
                (quadIndexIBO ?? throw new InvalidOperationException("The shared quad index buffer has not been uploaded.")).Bind(); // ensure IBO bound to this VAO if driver disassociates

                program.SetUniform("useTransparentList", 0f);
                GL.DrawElementsInstanced(
                    PrimitiveType.Triangles,
                    6,
                    DrawElementsType.UnsignedShort,
                    IntPtr.Zero,
                    opaqueRectangleCount);
            }
        }

        // Transparent pass: draws transparent face instances only with blending enabled.
        // Depth test is respected but this pass does not write depth; caller coordinates depth mask globally.
        public void RenderTransparent(ShaderProgram program)
        {
            ProcessPendingDeletes();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref deletionScheduled) != 0, this);
            if (!isBuilt) throw new InvalidOperationException("Native words must be uploaded before rendering.");

            if (fullyOccluded || transparentRectangleCount == 0)
                return;

            Vector3 adjustedChunkPosition = chunkWorldPosition + new Vector3(1f, 1f, 1f);
            program.Bind();
            program.SetUniform("chunkPosition", adjustedChunkPosition);
            program.SetUniform("tilesX", terrainTextureAtlas.tilesX);
            program.SetUniform("tilesY", terrainTextureAtlas.tilesY);

            if (transparentVAO != null)
            {
                transparentVAO.Bind();
                (quadIndexIBO ?? throw new InvalidOperationException("The shared quad index buffer has not been uploaded.")).Bind(); // ensure IBO bound to this VAO

                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                program.SetUniform("useTransparentList", 1f);
                GL.DrawElementsInstanced(
                    PrimitiveType.Triangles,
                    6,
                    DrawElementsType.UnsignedShort,
                    IntPtr.Zero,
                    transparentRectangleCount);
                GL.Disable(EnableCap.Blend);
            }
        }

        public static void ProcessPendingDeletes()
        {
            while (pendingDeletion.TryDequeue(out var cr)) cr.DeleteGL();
        }

        public void ScheduleDelete()
        {
            if (Interlocked.Exchange(ref deletionScheduled, 1) != 0)
                return;

            if (isBuilt)
                pendingDeletion.Enqueue(this);
        }

        public void Dispose() => ScheduleDelete();

        private void DeleteGL()
        {
            if (opaqueVaoBuilt) { opaqueVAO?.Delete(); opaqueVAO = null; opaqueVaoBuilt = false; }
            if (transparentVaoBuilt) { transparentVAO?.Delete(); transparentVAO = null; transparentVaoBuilt = false; }
            if (quadPosBuilt) { quadPosVBO?.Delete(); quadPosVBO = null; quadPosBuilt = false; }
            if (opaqueRectangleBuilt) { opaqueRectangleVBO?.Delete(); opaqueRectangleVBO = null; opaqueRectangleBuilt = false; }
            if (transparentRectangleBuilt) { transparentRectangleVBO?.Delete(); transparentRectangleVBO = null; transparentRectangleBuilt = false; }
            if (quadIndexBuilt) { quadIndexIBO?.Delete(); quadIndexIBO = null; quadIndexBuilt = false; }

            isBuilt = false;
        }
    }
}

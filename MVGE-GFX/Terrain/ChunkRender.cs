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

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private bool isBuilt = false;
        private Vector3 chunkWorldPosition;

        // Small-chunk fallback lists 
        private List<byte> chunkVertsList;
        private List<byte> chunkUVsList;
        private List<uint> chunkIndicesList;
        private List<ushort> chunkIndicesUShortList;

        // Pooled / sparse buffers (shared handling path)
        private byte[] vertBuffer;
        private byte[] uvBuffer;
        private uint[] indicesUIntBuffer;
        private ushort[] indicesUShortBuffer;
        private int vertBytesUsed;
        private int uvBytesUsed;
        private int indicesUsed;
        private bool useUShort;
        private bool usedPooling;   // true when PooledFacesRender chosen
        private bool usedSparse;    // true when SparseChunkRender chosen

        private VAO chunkVAO;
        private VBO chunkVertexVBO;
        private VBO chunkUVVBO;
        private IBO chunkIBO;

        public static BlockTextureAtlas terrainTextureAtlas { get; set; }
        private static readonly List<ByteVector2> EmptyUVList = new(4); // reusable empty list

        private readonly ChunkData chunkMeta;
        private readonly Func<int, int, int, ushort> getWorldBlock;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;

        // Flattened ephemeral blocks (x-major, then z, then y). Returned to pool after GenerateFaces
        private readonly ushort[] flatBlocks;
        private readonly int maxX;
        private readonly int maxY;
        private readonly int maxZ;

        // Incoming solidity flags (current chunk faces)
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        // Neighbor opposing face solidity flags
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;

        // Uniform single-block fast path (post-replacement) flags
        private readonly bool allOneBlock;
        private readonly ushort allOneBlockId;

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;

        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        // Fast-path flag when chunk fully enclosed (no visible faces)
        private bool fullyOccluded;

        // Popcount LUT for 6-bit mask (bits: L,R,T,B,F,Bk)
        private static readonly byte[] FacePopCount = InitPopCount();

        // Bit flags for mask
        private const byte FACE_LEFT = 1 << 0;
        private const byte FACE_RIGHT = 1 << 1;
        private const byte FACE_TOP = 1 << 2;
        private const byte FACE_BOTTOM = 1 << 3;
        private const byte FACE_FRONT = 1 << 4;
        private const byte FACE_BACK = 1 << 5;

        // Prepass data (ALWAYS supplied by generation; no legacy scan fallback)
        private readonly int prepassSolidCount;
        private readonly int prepassExposureEstimate;

        public ChunkRender(
            ChunkData chunkData,
            Func<int, int, int, ushort> worldBlockGetter,
            ushort[] flatBlocks,
            int maxX,
            int maxY,
            int maxZ,
            bool faceNegX,
            bool facePosX,
            bool faceNegY,
            bool facePosY,
            bool faceNegZ,
            bool facePosZ,
            bool nNegXPosX,
            bool nPosXNegX,
            bool nNegYPosY,
            bool nPosYNegY,
            bool nNegZPosZ,
            bool nPosZNegZ,
            bool allOneBlock = false,
            ushort allOneBlockId = 0,
            int prepassSolidCount = 0,
            int prepassExposureEstimate = 0)
        {
            // Prepass values MUST be supplied by generation phase.
            this.prepassSolidCount = prepassSolidCount;
            this.prepassExposureEstimate = prepassExposureEstimate;
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            this.flatBlocks = flatBlocks;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            chunkWorldPosition = new Vector3(chunkData.x, chunkData.y, chunkData.z);
            this.faceNegX = faceNegX; this.facePosX = facePosX; this.faceNegY = faceNegY; this.facePosY = facePosY; this.faceNegZ = faceNegZ; this.facePosZ = facePosZ;
            this.nNegXPosX = nNegXPosX; this.nPosXNegX = nPosXNegX; this.nNegYPosY = nNegYPosY; this.nPosYNegY = nPosYNegY; this.nNegZPosZ = nNegZPosZ; this.nPosZNegZ = nPosZNegZ;
            this.allOneBlock = allOneBlock; this.allOneBlockId = allOneBlockId;
            GenerateFaces();
        }

        private void GenerateFaces()
        {
            // Full enclosure early exit (cheap flags + boundary scans inside helper)
            if (CheckFullyOccluded(maxX, maxY, maxZ))
            {
                fullyOccluded = true;
                ReturnFlat();
                return;
            }

            int voxelCount = maxX * maxY * maxZ;
            int solidVoxelCount = prepassSolidCount;
            long exposureEstimate = prepassExposureEstimate;

            // --- Uniform single-block shortcut (fastest path) ---
            if (allOneBlock)
            {
                GenerateUniformFacesList();
                ReturnFlat();
                return;
            }

            // --- Sparse path ---
            bool sparseFeatureEnabled = true;
            float avgExposurePerSolid = solidVoxelCount == 0 ? 0f : (float)exposureEstimate / solidVoxelCount;
            const float SparseMinAvgExposure = 0.25f;     // average exposed faces per solid to justify sparse path
            bool chooseSparse = solidVoxelCount > 0 &&
                                 avgExposurePerSolid >= SparseMinAvgExposure;

            if (chooseSparse && sparseFeatureEnabled)
            {
                var sparseBuilder = new SparseChunkRender(
                    chunkWorldPosition,
                    maxX, maxY, maxZ,
                    emptyBlock,
                    flatBlocks,
                    getWorldBlock,
                    terrainTextureAtlas,
                    faceNegX, facePosX,
                    faceNegY, facePosY,
                    faceNegZ, facePosZ,
                    nNegXPosX, nPosXNegX,
                    nNegYPosY, nPosYNegY,
                    nNegZPosZ, nPosZNegZ);

                var sparseResult = sparseBuilder.Build();
                usedSparse = true;
                useUShort = sparseResult.UseUShort;
                vertBuffer = sparseResult.VertBuffer; uvBuffer = sparseResult.UVBuffer;
                indicesUIntBuffer = sparseResult.IndicesUIntBuffer; indicesUShortBuffer = sparseResult.IndicesUShortBuffer;
                vertBytesUsed = sparseResult.VertBytesUsed; uvBytesUsed = sparseResult.UVBytesUsed; indicesUsed = sparseResult.IndicesUsed;
                indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;
                ReturnFlat();
                return;
            }

            // --- Dense path (formerly: pooled) ---
            bool pooledFeatureEnabled = FlagManager.flags.useFacePooling.GetValueOrDefault();
            int pooledVoxelThreshold = pooledFeatureEnabled ? FlagManager.flags.faceAmountToPool.GetValueOrDefault(int.MaxValue) : int.MaxValue;
            bool choosePooled = solidVoxelCount >= pooledVoxelThreshold;
            if (choosePooled && pooledFeatureEnabled)
            {
                var pooledBuilder = new DenseChunkRender(
                    chunkWorldPosition, maxX, maxY, maxZ, emptyBlock,
                    getWorldBlock, null, null, terrainTextureAtlas, flatBlocks,
                    faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ,
                    nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ,
                    allOneBlock, allOneBlockId);
                var pooledResult = pooledBuilder.Build();
                usedPooling = true;
                useUShort = pooledResult.UseUShort;
                vertBuffer = pooledResult.VertBuffer; uvBuffer = pooledResult.UVBuffer;
                indicesUIntBuffer = pooledResult.IndicesUIntBuffer; indicesUShortBuffer = pooledResult.IndicesUShortBuffer;
                vertBytesUsed = pooledResult.VertBytesUsed; uvBytesUsed = pooledResult.UVBytesUsed; indicesUsed = pooledResult.IndicesUsed;
                indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;
                ReturnFlat();
                return;
            }

            // Fallback: legacy masked two-pass list builder (handles mid-density irregular cases)
            GenerateFacesListMaskedTwoPass();
            ReturnFlat();
        }
        public void Build()
        {
            if (isBuilt) return;

            if (fullyOccluded)
            {
                chunkVAO = new VAO();
                chunkVAO.Bind();
                chunkVertexVBO = new VBO(Array.Empty<byte>(), 0);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);
                chunkUVVBO = new VBO(Array.Empty<byte>(), 0);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);
                chunkIBO = new IBO(Array.Empty<uint>(), 0);
                isBuilt = true; return;
            }

            chunkVAO = new VAO();
            chunkVAO.Bind();

            if (usedPooling || usedSparse)
            {
                // Shared upload path for pooled & sparse builders
                chunkVertexVBO = new VBO(vertBuffer, vertBytesUsed);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

                chunkUVVBO = new VBO(uvBuffer, uvBytesUsed);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

                chunkIBO = useUShort
                    ? new IBO(indicesUShortBuffer, indicesUsed)
                    : new IBO(indicesUIntBuffer, indicesUsed);

                // Null checks before returning to pool to avoid ArgumentNullException if state inconsistent
                if (vertBuffer != null) ArrayPool<byte>.Shared.Return(vertBuffer, false);
                if (uvBuffer != null) ArrayPool<byte>.Shared.Return(uvBuffer, false);
                if (useUShort)
                {
                    if (indicesUShortBuffer != null) ArrayPool<ushort>.Shared.Return(indicesUShortBuffer, false);
                }
                else
                {
                    if (indicesUIntBuffer != null) ArrayPool<uint>.Shared.Return(indicesUIntBuffer, false);
                }
                vertBuffer = uvBuffer = null; indicesUIntBuffer = null; indicesUShortBuffer = null;
            }
            else
            {
                // indexFormat already decided during generation (two-pass)
                chunkVertexVBO = new VBO(chunkVertsList);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

                chunkUVVBO = new VBO(chunkUVsList);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

                chunkIBO = indexFormat == IndexFormat.UShort
                    ? new IBO(chunkIndicesUShortList)
                    : new IBO(chunkIndicesList);

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
            if (fullyOccluded) return;

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
                (usedPooling || usedSparse) && useUShort || (!usedPooling && !usedSparse && indexFormat == IndexFormat.UShort)
                    ? DrawElementsType.UnsignedShort
                    : DrawElementsType.UnsignedInt,
                0);
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
    }
}
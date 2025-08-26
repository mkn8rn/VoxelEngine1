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

        // Pooled buffers for large chunks
        private byte[] vertBuffer;
        private byte[] uvBuffer;
        private uint[] indicesUIntBuffer;
        private ushort[] indicesUShortBuffer;
        private int vertBytesUsed;
        private int uvBytesUsed;
        private int indicesUsed;
        private bool useUShort;
        private bool usedPooling;

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
            ushort allOneBlockId = 0)
        {
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
            // if enclosed (all boundary voxels solid and neighbors sealing) skip mesh generation entirely
            if (CheckFullyOccluded(maxX, maxY, maxZ))
            {
                fullyOccluded = true;
                ReturnFlat();
                return;
            }

            bool usePooling = false;
            int nonEmpty = 0; // total solid voxels
            long faceEstimate = 0; // approximate visible face count (only valid if we finish full pass without triggering classic threshold)

            if (FlagManager.flags.useFacePooling.GetValueOrDefault())
            {
                int threshold = FlagManager.flags.faceAmountToPool.GetValueOrDefault(int.MaxValue);
                int strideX = maxZ * maxY; // distance between x slices
                int strideZ = maxY;         // distance between z rows

                // Single unified pass: counts solids & (unless early threshold trigger) accumulates face estimate.
                for (int x = 0; x < maxX && !usePooling; x++)
                {
                    int slabBase = x * strideX;
                    for (int z = 0; z < maxZ && !usePooling; z++)
                    {
                        int rowBase = slabBase + z * strideZ;
                        for (int y = 0; y < maxY; y++)
                        {
                            int li = rowBase + y;
                            if (flatBlocks[li] == emptyBlock) continue;
                            nonEmpty++;
                            // Classic threshold check – if reached we select pooled path & abandon face estimate refinement.
                            if (nonEmpty >= threshold)
                            {
                                usePooling = true;
                                break;
                            }
                            // Exposure estimate: add 6, subtract 2 per previously visited solid neighbor (x-1, y-1, z-1)
                            faceEstimate += 6;
                            if (x > 0 && flatBlocks[li - strideX] != emptyBlock) faceEstimate -= 2; // -X shared pair
                            if (z > 0 && flatBlocks[li - strideZ] != emptyBlock) faceEstimate -= 2; // -Z shared pair
                            if (y > 0 && flatBlocks[li - 1] != emptyBlock) faceEstimate -= 2;       // -Y shared pair
                        }
                    }
                }
            }

            if (usePooling)
            {
                var builder = new PooledFacesRender(
                    chunkWorldPosition, maxX, maxY, maxZ, emptyBlock,
                    getWorldBlock, null, null, terrainTextureAtlas, flatBlocks,
                    faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ,
                    nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ,
                    allOneBlock, allOneBlockId);
                var res = builder.Build();
                usedPooling = true;
                useUShort = res.UseUShort;
                vertBuffer = res.VertBuffer; uvBuffer = res.UVBuffer;
                indicesUIntBuffer = res.IndicesUIntBuffer; indicesUShortBuffer = res.IndicesUShortBuffer;
                vertBytesUsed = res.VertBytesUsed; uvBytesUsed = res.UVBytesUsed; indicesUsed = res.IndicesUsed;
                indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;
                ReturnFlat();
            }
            else
            {
                // If we are in list mode but the chunk is uniform, we can still exploit a simpler path by building faces directly.
                if (allOneBlock)
                {
                    GenerateUniformFacesList();
                    ReturnFlat();
                    return;
                }
                GenerateFacesListMaskedTwoPass();
                ReturnFlat();
            }
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

            if (usedPooling)
            {
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
                (usedPooling && useUShort) || (!usedPooling && indexFormat == IndexFormat.UShort)
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
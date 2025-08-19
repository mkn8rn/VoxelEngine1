using MVGE_GFX.BufferObjects;
using MVGE_GFX.Models;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Numerics; // for BitOperations

namespace MVGE_GFX.Terrain
{
    public class ChunkRender
    {
        private bool isBuilt = false; // restored missing field
        private OpenTK.Mathematics.Vector3 chunkWorldPosition;

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
        private readonly Func<int, int, int, ushort> getLocalBlock;
        private readonly ushort emptyBlock = (ushort)BaseBlockType.Empty;

        private enum IndexFormat : byte { UShort, UInt }
        private IndexFormat indexFormat;

        private static readonly ConcurrentQueue<ChunkRender> pendingDeletion = new();

        // UV cache: key = (blockId << 3) | face (face fits in 3 bits). Value = 8-byte array (4 UV pairs)
        private static readonly ConcurrentDictionary<int, byte[]> uvByteCache = new();

        // Popcount table for 6-bit masks
        private static readonly byte[] popCount6 = new byte[64];
        static ChunkRender()
        {
            for (int i = 0; i < 64; i++)
            {
                int c = 0; int v = i; while (v != 0) { v &= (v - 1); c++; }
                popCount6[i] = (byte)c;
            }
        }

        // Fast-path flag when chunk fully enclosed (no visible faces)
        private bool fullyOccluded;

        public ChunkRender(
            ChunkData chunkData,
            Func<int, int, int, ushort> worldBlockGetter,
            Func<int, int, int, ushort> localBlockGetter)
        {
            chunkMeta = chunkData;
            getWorldBlock = worldBlockGetter;
            getLocalBlock = localBlockGetter;
            chunkWorldPosition = new OpenTK.Mathematics.Vector3(chunkData.x, chunkData.y, chunkData.z);
            GenerateFaces();
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

        public void Build()
        {
            if (isBuilt) return;

            // If fully occluded we still create minimal empty buffers so rendering path is uniform
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
                // Pooled path
                chunkVertexVBO = new VBO(vertBuffer, vertBytesUsed);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

                chunkUVVBO = new VBO(uvBuffer, uvBytesUsed);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

                chunkIBO = useUShort
                    ? new IBO(indicesUShortBuffer, indicesUsed)
                    : new IBO(indicesUIntBuffer, indicesUsed);

                // Return pooled arrays AFTER upload
                ArrayPool<byte>.Shared.Return(vertBuffer, false);
                ArrayPool<byte>.Shared.Return(uvBuffer, false);
                if (useUShort) ArrayPool<ushort>.Shared.Return(indicesUShortBuffer, false); else ArrayPool<uint>.Shared.Return(indicesUIntBuffer, false);
                vertBuffer = uvBuffer = null; indicesUIntBuffer = null; indicesUShortBuffer = null;
            }
            else
            {
                // List fallback path (small volume)
                TryFinalizeIndexFormatList();

                chunkVertexVBO = new VBO(chunkVertsList);
                chunkVertexVBO.Bind();
                chunkVAO.LinkToVAO(0, 3, VertexAttribPointerType.UnsignedByte, false, chunkVertexVBO);

                chunkUVVBO = new VBO(chunkUVsList);
                chunkUVVBO.Bind();
                chunkVAO.LinkToVAO(1, 2, VertexAttribPointerType.UnsignedByte, false, chunkUVVBO);

                chunkIBO = indexFormat == IndexFormat.UShort
                    ? new IBO(chunkIndicesUShortList)
                    : new IBO(chunkIndicesList);

                // Release list storage after buffer upload
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
            if (fullyOccluded) return; // nothing to draw

            OpenTK.Mathematics.Vector3 adjustedChunkPosition = chunkWorldPosition + new OpenTK.Mathematics.Vector3(1f, 1f, 1f);
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

        private void GenerateFaces()
        {
            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;
            long volume = (long)maxX * maxY * maxZ;
            bool usePooling = false;
            if (FlagManager.flags.useFacePooling.GetValueOrDefault())
            {
                int threshold = FlagManager.flags.faceAmountToPool.GetValueOrDefault(int.MaxValue);
                if (threshold >= 0 && volume >= threshold)
                {
                    usePooling = true;
                }
            }

            if (usePooling)
            {
                GenerateFacesPooled(maxX, maxY, maxZ);
            }
            else
            {
                if (CheckFullyOccluded(maxX, maxY, maxZ)) { fullyOccluded = true; return; }
                GenerateFacesList(maxX, maxY, maxZ);
            }
        }

        // Quick full occlusion test used by both paths
        private bool CheckFullyOccluded(int maxX, int maxY, int maxZ)
        {
            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;
            // Boundary shell local
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                    for (int z = 0; z < maxZ; z++)
                    {
                        if (x != 0 && x != maxX - 1 && y != 0 && y != maxY - 1 && z != 0 && z != maxZ - 1) continue;
                        if (getLocalBlock(x, y, z) == emptyBlock) return false;
                    }
            // External shell
            for (int y = 0; y < maxY; y++)
                for (int z = 0; z < maxZ; z++)
                {
                    if (getWorldBlock(baseWX - 1, baseWY + y, baseWZ + z) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + maxX, baseWY + y, baseWZ + z) == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    if (getWorldBlock(baseWX + x, baseWY - 1, baseWZ + z) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + x, baseWY + maxY, baseWZ + z) == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ - 1) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ + maxZ) == emptyBlock) return false;
                }
            return true;
        }

        private void GenerateFacesList(int maxX, int maxY, int maxZ)
        {
            chunkVertsList = new List<byte>();
            chunkUVsList = new List<byte>();
            chunkIndicesList = new List<uint>();

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
                        int localFaces = 0;
                        if ((x > 0 && getLocalBlock(x - 1, y, z) == emptyBlock) || (x == 0 && getWorldBlock(wx - 1, wy, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.LEFT, bp); localFaces++; }
                        if ((x < maxX - 1 && getLocalBlock(x + 1, y, z) == emptyBlock) || (x == maxX - 1 && getWorldBlock(wx + 1, wy, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.RIGHT, bp); localFaces++; }
                        if ((y < maxY - 1 && getLocalBlock(x, y + 1, z) == emptyBlock) || (y == maxY - 1 && getWorldBlock(wx, wy + 1, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.TOP, bp); localFaces++; }
                        if ((y > 0 && getLocalBlock(x, y - 1, z) == emptyBlock) || (y == 0 && getWorldBlock(wx, wy - 1, wz) == emptyBlock)) { IntegrateFaceList(block, Faces.BOTTOM, bp); localFaces++; }
                        if ((z < maxZ - 1 && getLocalBlock(x, y, z + 1) == emptyBlock) || (z == maxZ - 1 && getWorldBlock(wx, wy, wz + 1) == emptyBlock)) { IntegrateFaceList(block, Faces.FRONT, bp); localFaces++; }
                        if ((z > 0 && getLocalBlock(x, y, z - 1) == emptyBlock) || (z == 0 && getWorldBlock(wx, wy, wz - 1) == emptyBlock)) { IntegrateFaceList(block, Faces.BACK, bp); localFaces++; }
                        if (localFaces > 0) AddIndicesList(localFaces);
                    }
        }

        private void IntegrateFaceList(ushort block, Faces face, ByteVector3 bp)
        {
            var verts = RawFaceData.rawVertexData[face];
            foreach (var v in verts)
            {
                chunkVertsList.Add((byte)(v.x + bp.x));
                chunkVertsList.Add((byte)(v.y + bp.y));
                chunkVertsList.Add((byte)(v.z + bp.z));
            }
            var blockUVs = block != emptyBlock ? terrainTextureAtlas.GetBlockUVs(block, face) : EmptyUVList;
            foreach (var uv in blockUVs)
            {
                chunkUVsList.Add(uv.x);
                chunkUVsList.Add(uv.y);
            }
        }

        private void AddIndicesList(int faces)
        {
            int currentVertIndex = (chunkVertsList.Count / 3) - faces * 4; // starting index of first face added in this batch
            for (int i = 0; i < faces; i++)
            {
                int baseIndex = currentVertIndex + i * 4;
                chunkIndicesList.Add((uint)(baseIndex + 0));
                chunkIndicesList.Add((uint)(baseIndex + 1));
                chunkIndicesList.Add((uint)(baseIndex + 2));
                chunkIndicesList.Add((uint)(baseIndex + 2));
                chunkIndicesList.Add((uint)(baseIndex + 3));
                chunkIndicesList.Add((uint)(baseIndex + 0));
            }
        }

        private void TryFinalizeIndexFormatList()
        {
            int vertCount = chunkVertsList.Count / 3;
            if (vertCount <= 65535)
            {
                indexFormat = IndexFormat.UShort;
                chunkIndicesUShortList = new List<ushort>(chunkIndicesList.Count);
                foreach (var i in chunkIndicesList) chunkIndicesUShortList.Add((ushort)i);
                chunkIndicesList.Clear();
            }
            else indexFormat = IndexFormat.UInt;
        }

        // Bit positions in face mask
        private const int FACE_LEFT = 1 << 0;
        private const int FACE_RIGHT = 1 << 1;
        private const int FACE_TOP = 1 << 2;
        private const int FACE_BOTTOM = 1 << 3;
        private const int FACE_FRONT = 1 << 4;
        private const int FACE_BACK = 1 << 5;

        private static int LocalIndex(int x, int y, int z, int maxY, int maxZ) => (x * maxZ + z) * maxY + y; // x-major, then z, then y

        private void GenerateFacesPooled(int maxX, int maxY, int maxZ)
        {
            if (CheckFullyOccluded(maxX, maxY, maxZ)) { fullyOccluded = true; return; }

            int yzPlaneBits = maxY * maxZ;
            int yzWC = (yzPlaneBits + 63) >> 6;
            int xzPlaneBits = maxX * maxZ;
            int xzWC = (xzPlaneBits + 63) >> 6;
            int xyPlaneBits = maxX * maxY;
            int xyWC = (xyPlaneBits + 63) >> 6;

            int planeSize = maxY * maxZ; // for linear index neighbor offset along x
            int voxelCount = maxX * planeSize;

            // Block storage & single-solid detection
            ushort[] localBlocks = new ushort[voxelCount];
            bool haveSolid = false; bool singleSolidType = true; ushort singleSolidId = 0;

            // Axis slabs (bitsets)
            ulong[] xSlabs = new ulong[maxX * yzWC];
            ulong[] ySlabs = new ulong[maxY * xzWC];
            ulong[] zSlabs = new ulong[maxZ * xyWC];

            // Slab non-empty flags
            bool[] xSlabNonEmpty = new bool[maxX];
            bool[] ySlabNonEmpty = new bool[maxY];
            bool[] zSlabNonEmpty = new bool[maxZ];

            // Neighbor boundary slabs
            ulong[] neighborLeft = new ulong[yzWC];
            ulong[] neighborRight = new ulong[yzWC];
            ulong[] neighborBottom = new ulong[xzWC];
            ulong[] neighborTop = new ulong[xzWC];
            ulong[] neighborBack = new ulong[xyWC];
            ulong[] neighborFront = new ulong[xyWC];

            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;

            // Build neighbor slabs (outside solid = bit 1 if not empty)
            for (int z = 0; z < maxZ; z++)
                for (int y = 0; y < maxY; y++)
                {
                    int yzIndex = z * maxY + y; int w = yzIndex >> 6; int b = yzIndex & 63;
                    if (getWorldBlock(baseWX - 1, baseWY + y, baseWZ + z) != emptyBlock) neighborLeft[w] |= 1UL << b;
                    if (getWorldBlock(baseWX + maxX, baseWY + y, baseWZ + z) != emptyBlock) neighborRight[w] |= 1UL << b;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    int xzIndex = x * maxZ + z; int w = xzIndex >> 6; int b = xzIndex & 63;
                    if (getWorldBlock(baseWX + x, baseWY - 1, baseWZ + z) != emptyBlock) neighborBottom[w] |= 1UL << b;
                    if (getWorldBlock(baseWX + x, baseWY + maxY, baseWZ + z) != emptyBlock) neighborTop[w] |= 1UL << b;
                }
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    int xyIndex = x * maxY + y; int w = xyIndex >> 6; int b = xyIndex & 63;
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ - 1) != emptyBlock) neighborBack[w] |= 1UL << b;
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ + maxZ) != emptyBlock) neighborFront[w] |= 1UL << b;
                }

            // Populate slabs & block IDs
            for (int x = 0; x < maxX; x++)
            {
                int xSlabOffset = x * yzWC;
                ulong slabAccumX = 0UL; // track non-empty
                for (int z = 0; z < maxZ; z++)
                {
                    for (int y = 0; y < maxY; y++)
                    {
                        int li = LocalIndex(x, y, z, maxY, maxZ);
                        ushort bId = getLocalBlock(x, y, z);
                        localBlocks[li] = bId;
                        if (bId != emptyBlock)
                        {
                            int yzIndex = z * maxY + y; int wy = yzIndex >> 6; int by = yzIndex & 63;
                            ulong bit = 1UL << by;
                            xSlabs[xSlabOffset + wy] |= bit;
                            slabAccumX |= bit;

                            int xzIndex = x * maxZ + z; int wxz = xzIndex >> 6; int bxz = xzIndex & 63;
                            ulong bitXZ = 1UL << bxz;
                            ySlabs[y * xzWC + wxz] |= bitXZ; // accumulate implicitly

                            int xyIndex = x * maxY + y; int wxy = xyIndex >> 6; int bxy = xyIndex & 63;
                            zSlabs[z * xyWC + wxy] |= 1UL << bxy;

                            if (!haveSolid) { haveSolid = true; singleSolidId = bId; }
                            else if (singleSolidType && bId != singleSolidId) singleSolidType = false;
                        }
                    }
                }
                if (slabAccumX != 0) xSlabNonEmpty[x] = true;
            }
            // ySlabNonEmpty and zSlabNonEmpty
            for (int y = 0; y < maxY; y++)
            {
                int off = y * xzWC; ulong acc = 0UL; for (int w = 0; w < xzWC; w++) acc |= ySlabs[off + w]; ySlabNonEmpty[y] = acc != 0;
            }
            for (int z = 0; z < maxZ; z++)
            {
                int off = z * xyWC; ulong acc = 0UL; for (int w = 0; w < xyWC; w++) acc |= zSlabs[off + w]; zSlabNonEmpty[z] = acc != 0;
            }

            bool hasSingleOpaque = haveSolid && singleSolidType;

            // Precompute identical adjacent slab pairs (non-empty & equal)
            bool[] identicalPairX = new bool[maxX - 1];
            for (int x = 0; x < maxX - 1; x++)
            {
                if (!xSlabNonEmpty[x] || !xSlabNonEmpty[x + 1]) continue;
                int aOff = x * yzWC; int bOff = (x + 1) * yzWC; bool eq = true;
                for (int w = 0; w < yzWC; w++) { if (xSlabs[aOff + w] != xSlabs[bOff + w]) { eq = false; break; } }
                identicalPairX[x] = eq;
            }
            bool[] identicalPairY = new bool[maxY - 1];
            for (int y = 0; y < maxY - 1; y++)
            {
                if (!ySlabNonEmpty[y] || !ySlabNonEmpty[y + 1]) continue;
                int aOff = y * xzWC; int bOff = (y + 1) * xzWC; bool eq = true;
                for (int w = 0; w < xzWC; w++) { if (ySlabs[aOff + w] != ySlabs[bOff + w]) { eq = false; break; } }
                identicalPairY[y] = eq;
            }
            bool[] identicalPairZ = new bool[maxZ - 1];
            for (int z = 0; z < maxZ - 1; z++)
            {
                if (!zSlabNonEmpty[z] || !zSlabNonEmpty[z + 1]) continue;
                int aOff = z * xyWC; int bOff = (z + 1) * xyWC; bool eq = true;
                for (int w = 0; w < xyWC; w++) { if (zSlabs[aOff + w] != zSlabs[bOff + w]) { eq = false; break; } }
                identicalPairZ[z] = eq;
            }

            // Pass 1: count faces via differencing with skips
            int totalFaces = 0;
            // X-axis
            for (int x = 0; x < maxX; x++)
            {
                if (!xSlabNonEmpty[x]) continue; // empty slab produces no x faces
                int curOff = x * yzWC;
                int prevOff = (x - 1) * yzWC;
                int nextOff = (x + 1) * yzWC;
                bool skipLeft = x > 0 && identicalPairX[x - 1];
                bool skipRight = x < maxX - 1 && identicalPairX[x];
                for (int w = 0; w < yzWC; w++)
                {
                    ulong cur = xSlabs[curOff + w];
                    if (cur == 0) continue;
                    if (!skipLeft)
                    {
                        ulong leftBits = (x == 0) ? (cur & ~neighborLeft[w]) : (cur & ~xSlabs[prevOff + w]);
                        if (leftBits != 0) totalFaces += BitOperations.PopCount(leftBits);
                    }
                    if (!skipRight)
                    {
                        ulong rightBits = (x == maxX - 1) ? (cur & ~neighborRight[w]) : (cur & ~xSlabs[nextOff + w]);
                        if (rightBits != 0) totalFaces += BitOperations.PopCount(rightBits);
                    }
                }
            }
            // Y-axis
            for (int y = 0; y < maxY; y++)
            {
                if (!ySlabNonEmpty[y]) continue;
                int curOff = y * xzWC;
                int prevOff = (y - 1) * xzWC;
                int nextOff = (y + 1) * xzWC;
                bool skipBottom = y > 0 && identicalPairY[y - 1];
                bool skipTop = y < maxY - 1 && identicalPairY[y];
                for (int w = 0; w < xzWC; w++)
                {
                    ulong cur = ySlabs[curOff + w];
                    if (cur == 0) continue;
                    if (!skipBottom)
                    {
                        ulong bottomBits = (y == 0) ? (cur & ~neighborBottom[w]) : (cur & ~ySlabs[prevOff + w]);
                        if (bottomBits != 0) totalFaces += BitOperations.PopCount(bottomBits);
                    }
                    if (!skipTop)
                    {
                        ulong topBits = (y == maxY - 1) ? (cur & ~neighborTop[w]) : (cur & ~ySlabs[nextOff + w]);
                        if (topBits != 0) totalFaces += BitOperations.PopCount(topBits);
                    }
                }
            }
            // Z-axis
            for (int z = 0; z < maxZ; z++)
            {
                if (!zSlabNonEmpty[z]) continue;
                int curOff = z * xyWC;
                int prevOff = (z - 1) * xyWC;
                int nextOff = (z + 1) * xyWC;
                bool skipBack = z > 0 && identicalPairZ[z - 1];
                bool skipFront = z < maxZ - 1 && identicalPairZ[z];
                for (int w = 0; w < xyWC; w++)
                {
                    ulong cur = zSlabs[curOff + w];
                    if (cur == 0) continue;
                    if (!skipBack)
                    {
                        ulong backBits = (z == 0) ? (cur & ~neighborBack[w]) : (cur & ~zSlabs[prevOff + w]);
                        if (backBits != 0) totalFaces += BitOperations.PopCount(backBits);
                    }
                    if (!skipFront)
                    {
                        ulong frontBits = (z == maxZ - 1) ? (cur & ~neighborFront[w]) : (cur & ~zSlabs[nextOff + w]);
                        if (frontBits != 0) totalFaces += BitOperations.PopCount(frontBits);
                    }
                }
            }

            int totalVerts = totalFaces * 4;
            useUShort = totalVerts <= 65535;
            usedPooling = true;
            indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;

            vertBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
            uvBuffer = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
            if (useUShort) indicesUShortBuffer = ArrayPool<ushort>.Shared.Rent(totalFaces * 6); else indicesUIntBuffer = ArrayPool<uint>.Shared.Rent(totalFaces * 6);

            // Single solid UV concat
            byte[] singleSolidUVConcat = null;
            if (hasSingleOpaque)
            {
                singleSolidUVConcat = new byte[48];
                for (int f = 0; f < 6; f++)
                {
                    var uvBytes = GetOrCreateUv(singleSolidId, (Faces)f);
                    System.Buffer.BlockCopy(uvBytes, 0, singleSolidUVConcat, f * 8, 8);
                }
            }

            int faceIndex = 0;
            void EmitFaces(ushort block, Faces face, byte x, byte y, byte z)
            {
                if (hasSingleOpaque) block = singleSolidId;
                WriteFace(block, face, x, y, z, ref faceIndex, null, singleSolidUVConcat);
            }

            // Pass 2: emit faces by enumerating bits with skips
            // X-axis
            for (int x = 0; x < maxX; x++)
            {
                if (!xSlabNonEmpty[x]) continue;
                int curOff = x * yzWC;
                int prevOff = (x - 1) * yzWC;
                int nextOff = (x + 1) * yzWC;
                bool skipLeft = x > 0 && identicalPairX[x - 1];
                bool skipRight = x < maxX - 1 && identicalPairX[x];
                for (int w = 0; w < yzWC; w++)
                {
                    ulong cur = xSlabs[curOff + w];
                    if (cur == 0) continue;
                    if (!skipLeft)
                    {
                        ulong leftBits = (x == 0) ? (cur & ~neighborLeft[w]) : (cur & ~xSlabs[prevOff + w]);
                        ulong bits = leftBits;
                        while (bits != 0)
                        {
                            int t = BitOperations.TrailingZeroCount(bits);
                            int yzIndex = (w << 6) + t; if (yzIndex >= yzPlaneBits) break;
                            int z = yzIndex / maxY; int y = yzIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                            EmitFaces(localBlocks[li], Faces.LEFT, (byte)x, (byte)y, (byte)z);
                            bits &= bits - 1;
                        }
                    }
                    if (!skipRight)
                    {
                        ulong rightBits = (x == maxX - 1) ? (cur & ~neighborRight[w]) : (cur & ~xSlabs[nextOff + w]);
                        ulong bits = rightBits;
                        while (bits != 0)
                        {
                            int t = BitOperations.TrailingZeroCount(bits);
                            int yzIndex = (w << 6) + t; if (yzIndex >= yzPlaneBits) break;
                            int z = yzIndex / maxY; int y = yzIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                            EmitFaces(localBlocks[li], Faces.RIGHT, (byte)x, (byte)y, (byte)z);
                            bits &= bits - 1;
                        }
                    }
                }
            }
            // Y-axis
            for (int y = 0; y < maxY; y++)
            {
                if (!ySlabNonEmpty[y]) continue;
                int curOff = y * xzWC;
                int prevOff = (y - 1) * xzWC;
                int nextOff = (y + 1) * xzWC;
                bool skipBottom = y > 0 && identicalPairY[y - 1];
                bool skipTop = y < maxY - 1 && identicalPairY[y];
                for (int w = 0; w < xzWC; w++)
                {
                    ulong cur = ySlabs[curOff + w];
                    if (cur == 0) continue;
                    if (!skipBottom)
                    {
                        ulong bottomBits = (y == 0) ? (cur & ~neighborBottom[w]) : (cur & ~ySlabs[prevOff + w]);
                        ulong bits = bottomBits;
                        while (bits != 0)
                        {
                            int t = BitOperations.TrailingZeroCount(bits);
                            int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                            int x = xzIndex / maxZ; int z = xzIndex % maxZ; int li = LocalIndex(x, y, z, maxY, maxZ);
                            EmitFaces(localBlocks[li], Faces.BOTTOM, (byte)x, (byte)y, (byte)z);
                            bits &= bits - 1;
                        }
                    }
                    if (!skipTop)
                    {
                        ulong topBits = (y == maxY - 1) ? (cur & ~neighborTop[w]) : (cur & ~ySlabs[nextOff + w]);
                        ulong bits = topBits;
                        while (bits != 0)
                        {
                            int t = BitOperations.TrailingZeroCount(bits);
                            int xzIndex = (w << 6) + t; if (xzIndex >= xzPlaneBits) break;
                            int x = xzIndex / maxZ; int z = xzIndex % maxZ; int li = LocalIndex(x, y, z, maxY, maxZ);
                            EmitFaces(localBlocks[li], Faces.TOP, (byte)x, (byte)y, (byte)z);
                            bits &= bits - 1;
                        }
                    }
                }
            }
            // Z-axis
            for (int z = 0; z < maxZ; z++)
            {
                if (!zSlabNonEmpty[z]) continue;
                int curOff = z * xyWC;
                int prevOff = (z - 1) * xyWC;
                int nextOff = (z + 1) * xyWC;
                bool skipBack = z > 0 && identicalPairZ[z - 1];
                bool skipFront = z < maxZ - 1 && identicalPairZ[z];
                for (int w = 0; w < xyWC; w++)
                {
                    ulong cur = zSlabs[curOff + w];
                    if (cur == 0) continue;
                    if (!skipBack)
                    {
                        ulong backBits = (z == 0) ? (cur & ~neighborBack[w]) : (cur & ~zSlabs[prevOff + w]);
                        ulong bits = backBits;
                        while (bits != 0)
                        {
                            int t = BitOperations.TrailingZeroCount(bits);
                            int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                            int x = xyIndex / maxY; int y = xyIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                            EmitFaces(localBlocks[li], Faces.BACK, (byte)x, (byte)y, (byte)z);
                            bits &= bits - 1;
                        }
                    }
                    if (!skipFront)
                    {
                        ulong frontBits = (z == maxZ - 1) ? (cur & ~neighborFront[w]) : (cur & ~zSlabs[nextOff + w]);
                        ulong bits = frontBits;
                        while (bits != 0)
                        {
                            int t = BitOperations.TrailingZeroCount(bits);
                            int xyIndex = (w << 6) + t; if (xyIndex >= xyPlaneBits) break;
                            int x = xyIndex / maxY; int y = xyIndex % maxY; int li = LocalIndex(x, y, z, maxY, maxZ);
                            EmitFaces(localBlocks[li], Faces.FRONT, (byte)x, (byte)y, (byte)z);
                            bits &= bits - 1;
                        }
                    }
                }
            }

            vertBytesUsed = totalVerts * 3;
            uvBytesUsed = totalVerts * 2;
            indicesUsed = totalFaces * 6;
        }

        private byte[] GetOrCreateUv(ushort block, Faces face)
        {
            int key = (block << 3) | (int)face;
            return uvByteCache.GetOrAdd(key, k =>
            {
                var list = terrainTextureAtlas.GetBlockUVs(block, face);
                var arr = new byte[8];
                for (int j = 0; j < list.Count && j < 4; j++)
                {
                    arr[j * 2] = list[j].x;
                    arr[j * 2 + 1] = list[j].y;
                }
                return arr;
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteFace(ushort block, Faces face, byte bx, byte by, byte bz, ref int faceIndex, byte[][] singleSolidFaceUV = null, byte[] singleSolidUVConcat = null)
        {
            int baseVertexIndex = faceIndex * 4;
            int vertexByteOffset = baseVertexIndex * 3;
            int uvByteOffset = baseVertexIndex * 2;
            int indexOffset = faceIndex * 6;

            var verts = RawFaceData.rawVertexData[face];
            for (int i = 0; i < 4; i++)
            {
                vertBuffer[vertexByteOffset + i * 3 + 0] = (byte)(verts[i].x + bx);
                vertBuffer[vertexByteOffset + i * 3 + 1] = (byte)(verts[i].y + by);
                vertBuffer[vertexByteOffset + i * 3 + 2] = (byte)(verts[i].z + bz);
            }

            if (block != emptyBlock)
            {
                if (singleSolidUVConcat != null)
                {
                    int off = ((int)face) * 8;
                    uvBuffer[uvByteOffset + 0] = singleSolidUVConcat[off + 0];
                    uvBuffer[uvByteOffset + 1] = singleSolidUVConcat[off + 1];
                    uvBuffer[uvByteOffset + 2] = singleSolidUVConcat[off + 2];
                    uvBuffer[uvByteOffset + 3] = singleSolidUVConcat[off + 3];
                    uvBuffer[uvByteOffset + 4] = singleSolidUVConcat[off + 4];
                    uvBuffer[uvByteOffset + 5] = singleSolidUVConcat[off + 5];
                    uvBuffer[uvByteOffset + 6] = singleSolidUVConcat[off + 6];
                    uvBuffer[uvByteOffset + 7] = singleSolidUVConcat[off + 7];
                }
                else
                {
                    byte[] uvBytes = singleSolidFaceUV != null ? singleSolidFaceUV[(int)face] : GetOrCreateUv(block, face);
                    uvBuffer[uvByteOffset + 0] = uvBytes[0];
                    uvBuffer[uvByteOffset + 1] = uvBytes[1];
                    uvBuffer[uvByteOffset + 2] = uvBytes[2];
                    uvBuffer[uvByteOffset + 3] = uvBytes[3];
                    uvBuffer[uvByteOffset + 4] = uvBytes[4];
                    uvBuffer[uvByteOffset + 5] = uvBytes[5];
                    uvBuffer[uvByteOffset + 6] = uvBytes[6];
                    uvBuffer[uvByteOffset + 7] = uvBytes[7];
                }
            }
            else
            {
                for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = 0;
            }

            if (useUShort)
            {
                indicesUShortBuffer[indexOffset + 0] = (ushort)(baseVertexIndex + 0);
                indicesUShortBuffer[indexOffset + 1] = (ushort)(baseVertexIndex + 1);
                indicesUShortBuffer[indexOffset + 2] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[indexOffset + 3] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[indexOffset + 4] = (ushort)(baseVertexIndex + 3);
                indicesUShortBuffer[indexOffset + 5] = (ushort)(baseVertexIndex + 0);
            }
            else
            {
                indicesUIntBuffer[indexOffset + 0] = (uint)(baseVertexIndex + 0);
                indicesUIntBuffer[indexOffset + 1] = (uint)(baseVertexIndex + 1);
                indicesUIntBuffer[indexOffset + 2] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[indexOffset + 3] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[indexOffset + 4] = (uint)(baseVertexIndex + 3);
                indicesUIntBuffer[indexOffset + 5] = (uint)(baseVertexIndex + 0);
            }

            faceIndex++;
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
using MVGE_GFX.Models;
using System;
using System.Buffers;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private static bool AllBitsSetSimple(ulong[] arr, int bitCount)
        {
            if (arr == null) return false;
            int wc = (bitCount + 63) >> 6; int rem = bitCount & 63; ulong lastMask = rem == 0 ? ulong.MaxValue : (1UL << rem) - 1UL;
            for (int i = 0; i < wc; i++)
            {
                ulong expected = (i == wc - 1) ? lastMask : ulong.MaxValue;
                if ((arr[i] & expected) != expected) return false;
            }
            return true;
        }

        private bool CheckFullyOccluded(int maxX, int maxY, int maxZ)
        {
            // Fast flag test (our faces + neighbor opposing faces)
            if (faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                return true;
            }

            // Verify all six of our boundary planes are fully solid
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    if (flatBlocks[FlatIndex(x, y, 0)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, y, maxZ - 1)] == emptyBlock) return false;
                }
            for (int z = 0; z < maxZ; z++)
                for (int y = 0; y < maxY; y++)
                {
                    if (flatBlocks[FlatIndex(0, y, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(maxX - 1, y, z)] == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    if (flatBlocks[FlatIndex(x, 0, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, maxY - 1, z)] == emptyBlock) return false;
                }

            // Need neighbor plane caches for full occlusion confirmation; if any missing -> cannot confirm
            int yzBits = maxY * maxZ;
            int xzBits = maxX * maxZ;
            int xyBits = maxX * maxY;
            if (!AllBitsSetSimple(prerenderData.NeighborPlaneNegX, yzBits)) return false;
            if (!AllBitsSetSimple(prerenderData.NeighborPlanePosX, yzBits)) return false;
            if (!AllBitsSetSimple(prerenderData.NeighborPlaneNegY, xzBits)) return false;
            if (!AllBitsSetSimple(prerenderData.NeighborPlanePosY, xzBits)) return false;
            if (!AllBitsSetSimple(prerenderData.NeighborPlaneNegZ, xyBits)) return false;
            if (!AllBitsSetSimple(prerenderData.NeighborPlanePosZ, xyBits)) return false;

            return true;
        }

        private void GenerateUniformFacesList()
        {
            bool emitLeft = !(faceNegX && nNegXPosX);
            bool emitRight = !(facePosX && nPosXNegX);
            bool emitBottom = !(faceNegY && nNegYPosY);
            bool emitTop = !(facePosY && nPosYNegY);
            bool emitBack = !(faceNegZ && nNegZPosZ);
            bool emitFront = !(facePosZ && nPosZNegZ);

            if (!(emitLeft || emitRight || emitBottom || emitTop || emitBack || emitFront))
            {
                chunkVertsList = new List<byte>(0);
                chunkUVsList = new List<byte>(0);
                chunkIndicesList = new List<uint>(0);
                indexFormat = IndexFormat.UInt;
                return;
            }

            int faces = 0;
            if (emitLeft) faces += maxY * maxZ;
            if (emitRight) faces += maxY * maxZ;
            if (emitBottom) faces += maxX * maxZ;
            if (emitTop) faces += maxX * maxZ;
            if (emitBack) faces += maxX * maxY;
            if (emitFront) faces += maxX * maxY;

            if (faces == 0)
            {
                chunkVertsList = new List<byte>(0);
                chunkUVsList = new List<byte>(0);
                chunkIndicesList = new List<uint>(0);
                indexFormat = IndexFormat.UInt;
                return;
            }

            int totalVerts = faces * 4;
            bool useUShortIndices = totalVerts <= 65535;
            indexFormat = useUShortIndices ? IndexFormat.UShort : IndexFormat.UInt;
            chunkVertsList = new List<byte>(totalVerts * 3);
            chunkUVsList = new List<byte>(totalVerts * 2);
            if (useUShortIndices) chunkIndicesUShortList = new List<ushort>(faces * 6); else chunkIndicesList = new List<uint>(faces * 6);

            int currentVertexBase = 0;
            var uvPerFaceCache = new System.Collections.Generic.Dictionary<Faces, List<ByteVector2>>(6);

            void EmitFace(Faces face, byte x, byte y, byte z)
            {
                var verts = RawFaceData.rawVertexData[face];
                foreach (var v in verts)
                {
                    chunkVertsList.Add((byte)(v.x + x));
                    chunkVertsList.Add((byte)(v.y + y));
                    chunkVertsList.Add((byte)(v.z + z));
                }
                if (!uvPerFaceCache.TryGetValue(face, out var uvs))
                {
                    uvs = terrainTextureAtlas.GetBlockUVs(allOneBlockId, face);
                    uvPerFaceCache[face] = uvs;
                }
                foreach (var uv in uvs)
                {
                    chunkUVsList.Add(uv.x); chunkUVsList.Add(uv.y);
                }
                if (useUShortIndices)
                {
                    chunkIndicesUShortList.Add((ushort)(currentVertexBase + 0));
                    chunkIndicesUShortList.Add((ushort)(currentVertexBase + 1));
                    chunkIndicesUShortList.Add((ushort)(currentVertexBase + 2));
                    chunkIndicesUShortList.Add((ushort)(currentVertexBase + 2));
                    chunkIndicesUShortList.Add((ushort)(currentVertexBase + 3));
                    chunkIndicesUShortList.Add((ushort)(currentVertexBase + 0));
                }
                else
                {
                    chunkIndicesList.Add((uint)(currentVertexBase + 0));
                    chunkIndicesList.Add((uint)(currentVertexBase + 1));
                    chunkIndicesList.Add((uint)(currentVertexBase + 2));
                    chunkIndicesList.Add((uint)(currentVertexBase + 2));
                    chunkIndicesList.Add((uint)(currentVertexBase + 3));
                    chunkIndicesList.Add((uint)(currentVertexBase + 0));
                }
                currentVertexBase += 4;
            }

            if (emitLeft)
            {
                byte x = 0;
                for (int z = 0; z < maxZ; z++)
                    for (int y = 0; y < maxY; y++)
                        EmitFace(Faces.LEFT, x, (byte)y, (byte)z);
            }
            if (emitRight)
            {
                byte x = (byte)(maxX - 1);
                for (int z = 0; z < maxZ; z++)
                    for (int y = 0; y < maxY; y++)
                        EmitFace(Faces.RIGHT, x, (byte)y, (byte)z);
            }
            if (emitBottom)
            {
                byte y = 0;
                for (int x = 0; x < maxX; x++)
                    for (int z = 0; z < maxZ; z++)
                        EmitFace(Faces.BOTTOM, (byte)x, y, (byte)z);
            }
            if (emitTop)
            {
                byte y = (byte)(maxY - 1);
                for (int x = 0; x < maxX; x++)
                    for (int z = 0; z < maxZ; z++)
                        EmitFace(Faces.TOP, (byte)x, y, (byte)z);
            }
            if (emitBack)
            {
                byte z = 0;
                for (int x = 0; x < maxX; x++)
                    for (int y = 0; y < maxY; y++)
                        EmitFace(Faces.BACK, (byte)x, (byte)y, z);
            }
            if (emitFront)
            {
                byte z = (byte)(maxZ - 1);
                for (int x = 0; x < maxX; x++)
                    for (int y = 0; y < maxY; y++)
                        EmitFace(Faces.FRONT, (byte)x, (byte)y, z);
            }
        }
    }
}

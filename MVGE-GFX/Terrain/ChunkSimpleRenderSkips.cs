using MVGE_GFX.Models;
using System;
using System.Buffers;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private bool CheckFullyOccluded(int maxX, int maxY, int maxZ)
        {
            if (faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                return true;
            }

            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    int z0 = 0; int z1 = maxZ - 1;
                    if (flatBlocks[FlatIndex(x, y, z0)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, y, z1)] == emptyBlock) return false;
                }
            for (int z = 0; z < maxZ; z++)
                for (int y = 0; y < maxY; y++)
                {
                    int x0 = 0; int x1 = maxX - 1;
                    if (flatBlocks[FlatIndex(x0, y, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x1, y, z)] == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    int y0 = 0; int y1 = maxY - 1;
                    if (flatBlocks[FlatIndex(x, y0, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, y1, z)] == emptyBlock) return false;
                }
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

        private void GenerateUniformFacesList()
        {
            // Determine which boundary planes are potentially visible (not mutually occluded by neighbor)
            bool emitLeft = !(faceNegX && nNegXPosX);
            bool emitRight = !(facePosX && nPosXNegX);
            bool emitBottom = !(faceNegY && nNegYPosY);
            bool emitTop = !(facePosY && nPosYNegY);
            bool emitBack = !(faceNegZ && nNegZPosZ);
            bool emitFront = !(facePosZ && nPosZNegZ);

            // Early out if nothing visible
            if (!(emitLeft || emitRight || emitBottom || emitTop || emitBack || emitFront))
            {
                chunkVertsList = new List<byte>(0);
                chunkUVsList = new List<byte>(0);
                chunkIndicesList = new List<uint>(0);
                indexFormat = IndexFormat.UInt;
                return;
            }

            // Count faces (no greedy merging, emit one quad per boundary voxel)
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
            // UVs for the uniform block
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

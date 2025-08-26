using MVGE_GFX.Models;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector2 = OpenTK.Mathematics.Vector2;
using MVGE_GFX.Textures;

namespace MVGE_GFX.Terrain
{
    public partial class DenseChunkRender
    {
        internal struct BuildResult
        {
            public bool UseUShort;
            public bool HasSingleOpaque;
            public byte[] VertBuffer;
            public byte[] UVBuffer;
            public uint[] IndicesUIntBuffer;
            public ushort[] IndicesUShortBuffer;
            public int VertBytesUsed;
            public int UVBytesUsed;
            public int IndicesUsed;
        }

        private static void EnsureUvLut(BlockTextureAtlas atlas)
        {
            if (uvLutSparse != null || uvLut != null) return;
            lock (uvInitLock)
            {
                if (uvLutSparse != null || uvLut != null) return;
                uvLutSparse = new Dictionary<ushort, byte[]>(BlockTextureAtlas.blockTypeUVCoordinates.Count);
                singleSolidUvCacheSparse = new Dictionary<ushort, byte[]>(BlockTextureAtlas.blockTypeUVCoordinates.Count);

                Vector2 missVec;
                if (!BlockTextureAtlas.textureCoordinates.TryGetValue("404", out missVec))
                    missVec = Vector2.Zero;
                byte missX = (byte)missVec.X; byte missY = (byte)missVec.Y;
                byte[] missingFace = new byte[8];
                missingFace[0] = (byte)(missX + 1); missingFace[1] = (byte)(missY + 1);
                missingFace[2] = missX; missingFace[3] = (byte)(missY + 1);
                missingFace[4] = missX; missingFace[5] = missY;
                missingFace[6] = (byte)(missX + 1); missingFace[7] = missY;

                foreach (var kvp in BlockTextureAtlas.blockTypeUVCoordinates)
                {
                    ushort blockId = kvp.Key;
                    var concat = new byte[48]; // 6 faces * 8 bytes
                    for (int f = 0; f < 6; f++)
                    {
                        var list = atlas.GetBlockUVs(blockId, (Faces)f);
                        int baseOffset = f * 8;
                        for (int i = 0; i < 4; i++)
                        {
                            concat[baseOffset + i * 2] = list[i].x;
                            concat[baseOffset + i * 2 + 1] = list[i].y;
                        }
                    }
                    uvLutSparse[blockId] = concat;
                    singleSolidUvCacheSparse[blockId] = concat;
                }
                uvLutBlockCount = uvLutSparse.Count;
            }
        }

        private static byte[] GetSingleSolidUVConcat(ushort blockId)
        {
            if (uvLutSparse != null)
            {
                return singleSolidUvCacheSparse.TryGetValue(blockId, out var arr) ? arr : null;
            }
            if (blockId >= uvLutBlockCount) return null;
            var cache = singleSolidUvCache[blockId];
            if (cache != null) return cache;
            var build = new byte[48];
            int baseBlock = blockId * 6;
            for (int f = 0; f < 6; f++)
            {
                int lutOffset = (baseBlock + f) * 8;
                Buffer.BlockCopy(uvLut, lutOffset, build, f * 8, 8);
            }
            singleSolidUvCache[blockId] = build;
            return build;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFaceMulti(
            ushort block,
            Faces face,
            byte bx,
            byte by,
            byte bz,
            ref int faceIndex,
            ushort emptyBlock,
            BlockTextureAtlas atlas,
            byte[] vertBuffer,
            byte[] uvBuffer,
            bool useUShort,
            ushort[] indicesUShortBuffer,
            uint[] indicesUIntBuffer)
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
                if (uvLutSparse != null)
                {
                    if (uvLutSparse.TryGetValue(block, out var concat))
                    {
                        int off = ((int)face) * 8;
                        for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = concat[off + i];
                    }
                    else
                    {
                        for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = 0;
                    }
                }
                else
                {
                    int lutOffset = ((block * 6) + (int)face) * 8;
                    for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = uvLut[lutOffset + i];
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFaceSingle(
            Faces face,
            byte bx,
            byte by,
            byte bz,
            ref int faceIndex,
            byte[] singleSolidUVConcat,
            byte[] vertBuffer,
            byte[] uvBuffer,
            bool useUShort,
            ushort[] indicesUShortBuffer,
            uint[] indicesUIntBuffer)
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
            int off = ((int)face) * 8;
            for (int i = 0; i < 8; i++) uvBuffer[uvByteOffset + i] = singleSolidUVConcat[off + i];

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LocalIndex(int x, int y, int z, int maxY, int maxZ) => (x * maxZ + z) * maxY + y;
    }
}

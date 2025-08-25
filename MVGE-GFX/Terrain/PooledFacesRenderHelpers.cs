using MVGE_GFX.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.Terrain
{
    public partial class PooledFacesRender
    {


        // Specialized face writers
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
                        // Unknown block id; fill zeros
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

using MVGE_GFX.Models;
using MVGE_INF.Managers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private static byte[] InitPopCount()
        {
            var arr = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                int v = i; int c = 0; while (v != 0) { v &= v - 1; c++; }
                arr[i] = (byte)c;
            }
            return arr;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FlatIndex(int x, int y, int z) => (x * maxZ + z) * maxY + y;

        private void ReturnFlat()
        {
            if (flatBlocks != null) ArrayPool<ushort>.Shared.Return(flatBlocks, false);
        }

        private void IntegrateFaceListEmit(ushort block, Faces face, ByteVector3 bp, ref int currentVertexBase, bool useUShortIndices)
        {
            var verts = RawFaceData.rawVertexData[face];
            // Append vertex positions
            foreach (var v in verts)
            {
                chunkVertsList.Add((byte)(v.x + bp.x));
                chunkVertsList.Add((byte)(v.y + bp.y));
                chunkVertsList.Add((byte)(v.z + bp.z));
            }

            // UVs
            var blockUVs = block != emptyBlock ? terrainTextureAtlas.GetBlockUVs(block, face) : EmptyUVList;
            foreach (var uv in blockUVs)
            {
                chunkUVsList.Add(uv.x);
                chunkUVsList.Add(uv.y);
            }
            // Indices (two triangles)
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

        // plane: 'X' => dims (maxY,maxZ) along Y,Z for constant X; 'Y' => (maxX,maxZ); 'Z' => (maxX,maxY)
        private void PrefetchNeighborPlaneList(ulong[] target, int baseWX, int baseWY, int baseWZ, int dimA, int dimB, char plane)
        {
            if (plane == 'X')
            {
                // dimA = maxY, dimB = maxZ (iterate z then y)
                for (int z = 0; z < dimB; z++)
                {
                    for (int y = 0; y < dimA; y++)
                    {
                        int idx = z * dimA + y; int w = idx >> 6; int b = idx & 63;
                        ushort val = getWorldBlock(baseWX, baseWY + y, baseWZ + z);
                        if (val != emptyBlock) target[w] |= 1UL << b;
                    }
                }
            }
            else if (plane == 'Y')
            {
                // dimA = maxX, dimB = maxZ (iterate x,z)
                for (int x = 0; x < dimA; x++)
                {
                    for (int z = 0; z < dimB; z++)
                    {
                        int idx = x * dimB + z; int w = idx >> 6; int b = idx & 63;
                        ushort val = getWorldBlock(baseWX + x, baseWY, baseWZ + z);
                        if (val != emptyBlock) target[w] |= 1UL << b;
                    }
                }
            }
            else // 'Z'
            {
                // dimA = maxX, dimB = maxY (iterate x,y)
                for (int x = 0; x < dimA; x++)
                {
                    for (int y = 0; y < dimB; y++)
                    {
                        int idx = x * dimB + y; int w = idx >> 6; int b = idx & 63;
                        ushort val = getWorldBlock(baseWX + x, baseWY + y, baseWZ);
                        if (val != emptyBlock) target[w] |= 1UL << b;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TestBit(ulong[] arr, int index)
        {
            int w = index >> 6; int b = index & 63; return (arr[w] & (1UL << b)) != 0UL;
        }
    }
}

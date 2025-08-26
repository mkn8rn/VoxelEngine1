using System;
using System.Buffers;
using MVGE_GFX.Models;
using MVGE_INF.Models.Terrain;
using OpenTK.Mathematics;
using MVGE_GFX.Textures;

namespace MVGE_GFX.Terrain
{
    // Sparse-focused renderer: O(nonEmpty) face discovery.
    internal sealed partial class SparseChunkRender
    {
        internal struct BuildResult
        {
            public bool UseUShort;
            public byte[] VertBuffer;
            public byte[] UVBuffer;
            public uint[] IndicesUIntBuffer;
            public ushort[] IndicesUShortBuffer;
            public int VertBytesUsed;
            public int UVBytesUsed;
            public int IndicesUsed;
        }

        private readonly Vector3 chunkWorldPosition;
        private readonly int maxX, maxY, maxZ;
        private readonly ushort emptyBlock;
        private readonly ushort[] flatBlocks; // x-major, z, y
        private readonly Func<int,int,int,ushort> getWorldBlock;
        private readonly BlockTextureAtlas atlas;

        // Neighbor face solidity flags (still useful to skip some boundary world calls)
        private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
        private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;

        public SparseChunkRender(
            Vector3 chunkWorldPosition,
            int maxX, int maxY, int maxZ,
            ushort emptyBlock,
            ushort[] flatBlocks,
            Func<int,int,int,ushort> worldGetter,
            BlockTextureAtlas atlas,
            bool faceNegX, bool facePosX,
            bool faceNegY, bool facePosY,
            bool faceNegZ, bool facePosZ,
            bool nNegXPosX, bool nPosXNegX,
            bool nNegYPosY, bool nPosYNegY,
            bool nNegZPosZ, bool nPosZNegZ)
        {
            this.chunkWorldPosition = chunkWorldPosition;
            this.maxX = maxX; this.maxY = maxY; this.maxZ = maxZ;
            this.emptyBlock = emptyBlock;
            this.flatBlocks = flatBlocks;
            getWorldBlock = worldGetter;
            this.atlas = atlas;
            this.faceNegX = faceNegX; this.facePosX = facePosX;
            this.faceNegY = faceNegY; this.facePosY = facePosY;
            this.faceNegZ = faceNegZ; this.facePosZ = facePosZ;
            this.nNegXPosX = nNegXPosX; this.nPosXNegX = nPosXNegX;
            this.nNegYPosY = nNegYPosY; this.nPosYNegY = nPosYNegY;
            this.nNegZPosZ = nNegZPosZ; this.nPosZNegZ = nPosZNegZ;
        }

        private static readonly byte FACE_LEFT   = 1 << 0;
        private static readonly byte FACE_RIGHT  = 1 << 1;
        private static readonly byte FACE_TOP    = 1 << 2;
        private static readonly byte FACE_BOTTOM = 1 << 3;
        private static readonly byte FACE_FRONT  = 1 << 4;
        private static readonly byte FACE_BACK   = 1 << 5;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void Decode(int li, int maxY, int maxZ, out int x, out int y, out int z)
        {
            y = li % maxY;
            int t = li / maxY;
            z = t % maxZ;
            x = t / maxZ;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool BitTest(ulong[] bits, int idx)
        {
            int w = idx >> 6;
            int b = idx & 63;
            return (bits[w] & (1UL << b)) != 0;
        }

        public BuildResult Build()
        {
            int voxelCount = maxX * maxY * maxZ;
            int wordCount = (voxelCount + 63) >> 6;

            // Pass 0: collect occupied indices
            ulong[] occupancy = ArrayPool<ulong>.Shared.Rent(wordCount);
            Array.Clear(occupancy, 0, wordCount);
            int[] indices = ArrayPool<int>.Shared.Rent(Math.Max(16, voxelCount / 8)); // rough initial; may resize
            ushort[] blocks = ArrayPool<ushort>.Shared.Rent(indices.Length);

            int count = 0;
            for (int li = 0; li < voxelCount; li++)
            {
                ushort id = flatBlocks[li];
                if (id == emptyBlock) continue;
                int w = li >> 6;
                int b = li & 63;
                occupancy[w] |= 1UL << b;
                if (count == indices.Length)
                {
                    // grow
                    int newLen = indices.Length * 2;
                    var newIdx = ArrayPool<int>.Shared.Rent(newLen);
                    var newBlk = ArrayPool<ushort>.Shared.Rent(newLen);
                    Array.Copy(indices, newIdx, count);
                    Array.Copy(blocks, newBlk, count);
                    ArrayPool<int>.Shared.Return(indices, false);
                    ArrayPool<ushort>.Shared.Return(blocks, false);
                    indices = newIdx;
                    blocks = newBlk;
                }
                indices[count] = li;
                blocks[count] = id;
                count++;
            }

            if (count == 0)
            {
                ArrayPool<ulong>.Shared.Return(occupancy, false);
                ArrayPool<int>.Shared.Return(indices, false);
                ArrayPool<ushort>.Shared.Return(blocks, false);
                return new BuildResult
                {
                    UseUShort = true,
                    VertBuffer = Array.Empty<byte>(),
                    UVBuffer = Array.Empty<byte>(),
                    IndicesUShortBuffer = Array.Empty<ushort>(),
                    IndicesUIntBuffer = null,
                    VertBytesUsed = 0,
                    UVBytesUsed = 0,
                    IndicesUsed = 0
                };
            }

            // Pass 1: face counting + mask build
            byte[] faceMasks = ArrayPool<byte>.Shared.Rent(count);
            int totalFaces = 0;

            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;

            for (int i = 0; i < count; i++)
            {
                int li = indices[i];
                Decode(li, maxY, maxZ, out int x, out int y, out int z);
                ushort block = blocks[i];

                byte mask = 0;

                // LEFT
                if (x == 0)
                {
                    if (!(faceNegX && nNegXPosX))
                    {
                        ushort nb = getWorldBlock(baseWX - 1, baseWY + y, baseWZ + z);
                        if (nb == emptyBlock) mask |= FACE_LEFT;
                    }
                }
                else
                {
                    int liN = li - (maxZ * maxY);
                    if (!BitTest(occupancy, liN)) mask |= FACE_LEFT;
                }

                // RIGHT
                if (x == maxX - 1)
                {
                    if (!(facePosX && nPosXNegX))
                    {
                        ushort nb = getWorldBlock(baseWX + maxX, baseWY + y, baseWZ + z);
                        if (nb == emptyBlock) mask |= FACE_RIGHT;
                    }
                }
                else
                {
                    int liN = li + (maxZ * maxY);
                    if (!BitTest(occupancy, liN)) mask |= FACE_RIGHT;
                }

                // BOTTOM
                if (y == 0)
                {
                    if (!(faceNegY && nNegYPosY))
                    {
                        ushort nb = getWorldBlock(baseWX + x, baseWY - 1, baseWZ + z);
                        if (nb == emptyBlock) mask |= FACE_BOTTOM;
                    }
                }
                else
                {
                    if (!BitTest(occupancy, li - 1)) mask |= FACE_BOTTOM;
                }

                // TOP
                if (y == maxY - 1)
                {
                    if (!(facePosY && nPosYNegY))
                    {
                        ushort nb = getWorldBlock(baseWX + x, baseWY + maxY, baseWZ + z);
                        if (nb == emptyBlock) mask |= FACE_TOP;
                    }
                }
                else
                {
                    if (!BitTest(occupancy, li + 1)) mask |= FACE_TOP;
                }

                // BACK (negative Z)
                if (z == 0)
                {
                    if (!(faceNegZ && nNegZPosZ))
                    {
                        ushort nb = getWorldBlock(baseWX + x, baseWY + y, baseWZ - 1);
                        if (nb == emptyBlock) mask |= FACE_BACK;
                    }
                }
                else
                {
                    int liN = li - (maxY);
                    if (!BitTest(occupancy, liN)) mask |= FACE_BACK;
                }

                // FRONT (positive Z)
                if (z == maxZ - 1)
                {
                    if (!(facePosZ && nPosZNegZ))
                    {
                        ushort nb = getWorldBlock(baseWX + x, baseWY + y, baseWZ + maxZ);
                        if (nb == emptyBlock) mask |= FACE_FRONT;
                    }
                }
                else
                {
                    int liN = li + (maxY);
                    if (!BitTest(occupancy, liN)) mask |= FACE_FRONT;
                }

                if (mask != 0)
                {
                    // popcount of up to 6 bits (manual small-table optional)
                    int faces =
                        ((mask & FACE_LEFT) != 0 ? 1 : 0) +
                        ((mask & FACE_RIGHT) != 0 ? 1 : 0) +
                        ((mask & FACE_TOP) != 0 ? 1 : 0) +
                        ((mask & FACE_BOTTOM) != 0 ? 1 : 0) +
                        ((mask & FACE_FRONT) != 0 ? 1 : 0) +
                        ((mask & FACE_BACK) != 0 ? 1 : 0);
                    totalFaces += faces;
                }
                faceMasks[i] = mask;
            }

            if (totalFaces == 0)
            {
                Cleanup();
                return new BuildResult
                {
                    UseUShort = true,
                    VertBuffer = Array.Empty<byte>(),
                    UVBuffer = Array.Empty<byte>(),
                    IndicesUShortBuffer = Array.Empty<ushort>(),
                    IndicesUIntBuffer = null,
                    VertBytesUsed = 0,
                    UVBytesUsed = 0,
                    IndicesUsed = 0
                };
            }

            int totalVerts = totalFaces * 4;
            bool useUShort = totalVerts <= 65535;

            byte[] vertBuf = ArrayPool<byte>.Shared.Rent(totalVerts * 3);
            byte[] uvBuf = ArrayPool<byte>.Shared.Rent(totalVerts * 2);
            ushort[] idxU16 = useUShort ? ArrayPool<ushort>.Shared.Rent(totalFaces * 6) : null;
            uint[] idxU32 = useUShort ? null : ArrayPool<uint>.Shared.Rent(totalFaces * 6);

            int faceIndex = 0;

            // Emit
            for (int i = 0; i < count; i++)
            {
                byte mask = faceMasks[i];
                if (mask == 0) continue;
                int li = indices[i];
                Decode(li, maxY, maxZ, out int x, out int y, out int z);
                ushort block = blocks[i];

                Emit(mask, block, (byte)x, (byte)y, (byte)z,
                     ref faceIndex,
                     vertBuf, uvBuf,
                     useUShort, idxU16, idxU32);
            }

            var result = new BuildResult
            {
                UseUShort = useUShort,
                VertBuffer = vertBuf,
                UVBuffer = uvBuf,
                IndicesUShortBuffer = idxU16,
                IndicesUIntBuffer = idxU32,
                VertBytesUsed = totalVerts * 3,
                UVBytesUsed = totalVerts * 2,
                IndicesUsed = totalFaces * 6
            };

            Cleanup();
            return result;

            void Cleanup()
            {
                ArrayPool<ulong>.Shared.Return(occupancy, false);
                ArrayPool<int>.Shared.Return(indices, false);
                ArrayPool<ushort>.Shared.Return(blocks, false);
                ArrayPool<byte>.Shared.Return(faceMasks, false);
            }
        }

        private void Emit(byte mask, ushort block,
                          byte x, byte y, byte z,
                          ref int faceIndex,
                          byte[] vertBuffer,
                          byte[] uvBuffer,
                          bool useUShort,
                          ushort[] indicesUShortBuffer,
                          uint[] indicesUIntBuffer)
        {
            // Emit each set face in fixed order to match existing orientation logic
            if ((mask & FACE_LEFT) != 0)  WriteFace(block, Faces.LEFT,   x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_RIGHT) != 0) WriteFace(block, Faces.RIGHT,  x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_TOP) != 0)   WriteFace(block, Faces.TOP,    x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_BOTTOM) != 0)WriteFace(block, Faces.BOTTOM, x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_FRONT) != 0) WriteFace(block, Faces.FRONT,  x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
            if ((mask & FACE_BACK) != 0)  WriteFace(block, Faces.BACK,   x, y, z, ref faceIndex, vertBuffer, uvBuffer, useUShort, indicesUShortBuffer, indicesUIntBuffer);
        }

        private void WriteFace(ushort block,
                               Faces face,
                               byte bx, byte by, byte bz,
                               ref int faceIndex,
                               byte[] vertBuffer,
                               byte[] uvBuffer,
                               bool useUShort,
                               ushort[] indicesUShortBuffer,
                               uint[] indicesUIntBuffer)
        {
            int baseVertexIndex = faceIndex * 4;
            int vOff = baseVertexIndex * 3;
            int uvOff = baseVertexIndex * 2;
            int iOff = faceIndex * 6;

            var verts = RawFaceData.rawVertexData[face];
            for (int i = 0; i < 4; i++)
            {
                vertBuffer[vOff + i * 3 + 0] = (byte)(verts[i].x + bx);
                vertBuffer[vOff + i * 3 + 1] = (byte)(verts[i].y + by);
                vertBuffer[vOff + i * 3 + 2] = (byte)(verts[i].z + bz);
            }

            var uvList = atlas.GetBlockUVs(block, face);
            for (int i = 0; i < 4; i++)
            {
                uvBuffer[uvOff + i * 2 + 0] = uvList[i].x;
                uvBuffer[uvOff + i * 2 + 1] = uvList[i].y;
            }

            if (useUShort)
            {
                indicesUShortBuffer[iOff + 0] = (ushort)(baseVertexIndex + 0);
                indicesUShortBuffer[iOff + 1] = (ushort)(baseVertexIndex + 1);
                indicesUShortBuffer[iOff + 2] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[iOff + 3] = (ushort)(baseVertexIndex + 2);
                indicesUShortBuffer[iOff + 4] = (ushort)(baseVertexIndex + 3);
                indicesUShortBuffer[iOff + 5] = (ushort)(baseVertexIndex + 0);
            }
            else
            {
                indicesUIntBuffer[iOff + 0] = (uint)(baseVertexIndex + 0);
                indicesUIntBuffer[iOff + 1] = (uint)(baseVertexIndex + 1);
                indicesUIntBuffer[iOff + 2] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[iOff + 3] = (uint)(baseVertexIndex + 2);
                indicesUIntBuffer[iOff + 4] = (uint)(baseVertexIndex + 3);
                indicesUIntBuffer[iOff + 5] = (uint)(baseVertexIndex + 0);
            }
            faceIndex++;
        }
    }
}

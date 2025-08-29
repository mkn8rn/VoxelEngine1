using MVGE_INF.Models.Generation;
using MVGE_GFX.Textures;
using MVGE_GFX.Models;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        private readonly ChunkPrerenderData data;
        private readonly BlockTextureAtlas atlas;
        private const ushort EMPTY = 0;

        public SectionRender(ChunkPrerenderData data, BlockTextureAtlas atlas)
        {
            this.data = data; this.atlas = atlas;
        }

        // Very simple fallback mesher for invalid or corrupt metadata
        public void Build(out byte[] verts, out byte[] uvs, out ushort[] idxU16, out uint[] idxU32,
                          out bool useUShort, out int vertBytes, out int uvBytes, out int indices)
        {
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;
            if (maxX == 0 || maxY == 0 || maxZ == 0 || data.SectionDescs == null)
            {
                verts = Array.Empty<byte>(); uvs = Array.Empty<byte>(); idxU16 = Array.Empty<ushort>(); idxU32 = Array.Empty<uint>();
                useUShort = true; vertBytes = uvBytes = indices = 0; return;
            }

            var vertList = new List<byte>(1024);
            var uvList = new List<byte>(1024);
            var idxListU32 = new List<uint>(2048); // start with UInt; we will down-convert later if fits

            uint vertBase = 0;

            for (int x = 0; x < maxX; x++)
            {
                for (int y = 0; y < maxY; y++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        ushort block = GetBlock(x, y, z);
                        if (block == EMPTY) continue;
                        // For each of 6 directions, emit face if neighbor empty / OOB
                        TryEmit(block, Faces.LEFT,  x, y, z, x - 1 < 0 || GetBlock(x - 1, y, z) == EMPTY, ref vertBase, vertList, uvList, idxListU32);
                        TryEmit(block, Faces.RIGHT, x, y, z, x + 1 >= maxX || GetBlock(x + 1, y, z) == EMPTY, ref vertBase, vertList, uvList, idxListU32);
                        TryEmit(block, Faces.BOTTOM,x, y, z, y - 1 < 0 || GetBlock(x, y - 1, z) == EMPTY, ref vertBase, vertList, uvList, idxListU32);
                        TryEmit(block, Faces.TOP,   x, y, z, y + 1 >= maxY || GetBlock(x, y + 1, z) == EMPTY, ref vertBase, vertList, uvList, idxListU32);
                        TryEmit(block, Faces.BACK,  x, y, z, z - 1 < 0 || GetBlock(x, y, z - 1) == EMPTY, ref vertBase, vertList, uvList, idxListU32);
                        TryEmit(block, Faces.FRONT, x, y, z, z + 1 >= maxZ || GetBlock(x, y, z + 1) == EMPTY, ref vertBase, vertList, uvList, idxListU32);
                    }
                }
            }

            int totalVerts = (int)(vertBase);
            useUShort = totalVerts <= 65535;

            if (useUShort)
            {
                idxU16 = new ushort[idxListU32.Count];
                for (int i = 0; i < idxListU32.Count; i++) idxU16[i] = (ushort)idxListU32[i];
                idxU32 = Array.Empty<uint>();
            }
            else
            {
                idxU32 = idxListU32.ToArray();
                idxU16 = Array.Empty<ushort>();
            }

            verts = vertList.ToArray();
            uvs = uvList.ToArray();
            vertBytes = verts.Length; uvBytes = uvs.Length; indices = idxListU32.Count; // both formats share same count
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryEmit(ushort block, Faces face, int x, int y, int z, bool visible,
                             ref uint vertBase, List<byte> vertList, List<byte> uvList, List<uint> idxListU32)
        {
            if (!visible) return;
            var faceVerts = RawFaceData.rawVertexData[face];
            for (int i = 0; i < 4; i++)
            {
                vertList.Add((byte)(faceVerts[i].x + x));
                vertList.Add((byte)(faceVerts[i].y + y));
                vertList.Add((byte)(faceVerts[i].z + z));
            }
            var uvFace = atlas.GetBlockUVs(block, face);
            for (int i = 0; i < 4; i++)
            {
                uvList.Add(uvFace[i].x); uvList.Add(uvFace[i].y);
            }
            // indices (two triangles) CCW
            idxListU32.Add(vertBase + 0);
            idxListU32.Add(vertBase + 1);
            idxListU32.Add(vertBase + 2);
            idxListU32.Add(vertBase + 2);
            idxListU32.Add(vertBase + 3);
            idxListU32.Add(vertBase + 0);
            vertBase += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort GetBlock(int x, int y, int z)
        {
            int S = data.sectionSize;
            int sx = x / S; int sy = y / S; int sz = z / S;
            if ((uint)sx >= (uint)data.sectionsX || (uint)sy >= (uint)data.sectionsY || (uint)sz >= (uint)data.sectionsZ) return 0;
            int localX = x - sx * S; int localY = y - sy * S; int localZ = z - sz * S;
            int index = ((sx * data.sectionsY) + sy) * data.sectionsZ + sz;
            ref var desc = ref data.SectionDescs[index];
            switch (desc.Kind)
            {
                case 0: // Empty
                    return 0;
                case 1: // Uniform
                    return desc.UniformBlockId;
                case 2: // Sparse
                    if (desc.SparseIndices == null) return 0;
                    int lid = ((localZ << 4) + localX) << 4 | localY; // (z*S + x)*16 + y
                    var idxArr = desc.SparseIndices; var blkArr = desc.SparseBlocks;
                    for (int i = 0; i < idxArr.Length; i++) if (idxArr[i] == lid) return blkArr[i];
                    return 0;
                case 3: // DenseExpanded
                    if (desc.ExpandedDense == null) return 0;
                    return desc.ExpandedDense[((localZ << 4) + localX) << 4 | localY];
                case 4: // Packed
                    if (desc.PackedBitData == null || desc.Palette == null) return 0;
                    return DecodePacked(ref desc, localX, localY, localZ);
                default:
                    return 0;
            }
        }

        private static ushort DecodePacked(ref SectionPrerenderDesc desc, int lx, int ly, int lz)
        {
            int S = 16; // fixed section size
            int li = ((lz * S + lx) << 4) + ly;
            int bpi = desc.BitsPerIndex; if (bpi <= 0) return 0;
            long bitPos = (long)li * bpi;
            int word = (int)(bitPos >> 5);
            int bitOffset = (int)(bitPos & 31);
            uint value = desc.PackedBitData[word] >> bitOffset;
            int rem = 32 - bitOffset;
            if (rem < bpi) value |= desc.PackedBitData[word + 1] << rem;
            int mask = (1 << bpi) - 1;
            int paletteIndex = (int)(value & mask);
            if (paletteIndex < 0 || paletteIndex >= desc.Palette.Count) return 0;
            return desc.Palette[paletteIndex];
        }
    }
}

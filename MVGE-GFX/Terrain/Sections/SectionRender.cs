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

        // Build uses per-section specialized paths and a fallback per-section brute scan.
        public void Build(out byte[] verts, out byte[] uvs, out ushort[] idxU16, out uint[] idxU32,
                          out bool useUShort, out int vertBytes, out int uvBytes, out int indices)
        {
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;
            if (maxX == 0 || maxY == 0 || maxZ == 0 || data.SectionDescs == null)
            {
                verts = Array.Empty<byte>(); uvs = Array.Empty<byte>(); idxU16 = Array.Empty<ushort>(); idxU32 = Array.Empty<uint>();
                useUShort = true; vertBytes = uvBytes = indices = 0; return;
            }

            var vertList = new List<byte>(2048);
            var uvList = new List<byte>(2048);
            var idxListU32 = new List<uint>(4096);
            uint vertBase = 0;

            int S = data.sectionSize;

            for (int sx = 0; sx < data.sectionsX; sx++)
            {
                for (int sy = 0; sy < data.sectionsY; sy++)
                {
                    for (int sz = 0; sz < data.sectionsZ; sz++)
                    {
                        int si = ((sx * data.sectionsY) + sy) * data.sectionsZ + sz;
                        ref var desc = ref data.SectionDescs[si];
                        switch (desc.Kind)
                        {
                            case 0: // Empty
                                // nothing
                                continue;
                            case 1: // Uniform
                                EmitUniformSection(ref desc, sx, sy, sz, vertList, uvList, idxListU32, ref vertBase);
                                continue;
                            default:
                                FallbackSectionScan(ref desc, sx, sy, sz, S, maxX, maxY, maxZ, vertList, uvList, idxListU32, ref vertBase);
                                break;
                        }
                    }
                }
            }

            int totalVerts = (int)vertBase;
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
            vertBytes = verts.Length; uvBytes = uvs.Length; indices = idxListU32.Count;
        }

        // Fallback brute-force scan in case of corrupt or invalid section data.
        private void FallbackSectionScan(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
                                         int maxX, int maxY, int maxZ,
                                         List<byte> vertList, List<byte> uvList, List<uint> idxListU32, ref uint vertBase)
        {
            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;
            int endX = Math.Min(baseX + S, maxX);
            int endY = Math.Min(baseY + S, maxY);
            int endZ = Math.Min(baseZ + S, maxZ);

            var nNegX = data.NeighborPlaneNegX; var nPosX = data.NeighborPlanePosX;
            var nNegY = data.NeighborPlaneNegY; var nPosY = data.NeighborPlanePosY;
            var nNegZ = data.NeighborPlaneNegZ; var nPosZ = data.NeighborPlanePosZ;

            for (int x = baseX; x < endX; x++)
            {
                for (int y = baseY; y < endY; y++)
                {
                    for (int z = baseZ; z < endZ; z++)
                    {
                        ushort block = GetBlock(x, y, z); if (block == EMPTY) continue;
                        // LEFT
                        if (x == 0)
                        {
                            if (!PlaneBit(nNegX, z * maxY + y)) TryEmit(block, Faces.LEFT, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        }
                        else if (GetBlock(x - 1, y, z) == EMPTY) TryEmit(block, Faces.LEFT, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        // RIGHT
                        if (x == maxX - 1)
                        {
                            if (!PlaneBit(nPosX, z * maxY + y)) TryEmit(block, Faces.RIGHT, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        }
                        else if (GetBlock(x + 1, y, z) == EMPTY) TryEmit(block, Faces.RIGHT, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        // BOTTOM
                        if (y == 0)
                        {
                            if (!PlaneBit(nNegY, x * maxZ + z)) TryEmit(block, Faces.BOTTOM, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        }
                        else if (GetBlock(x, y - 1, z) == EMPTY) TryEmit(block, Faces.BOTTOM, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        // TOP
                        if (y == maxY - 1)
                        {
                            if (!PlaneBit(nPosY, x * maxZ + z)) TryEmit(block, Faces.TOP, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        }
                        else if (GetBlock(x, y + 1, z) == EMPTY) TryEmit(block, Faces.TOP, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        // BACK
                        if (z == 0)
                        {
                            if (!PlaneBit(nNegZ, x * maxY + y)) TryEmit(block, Faces.BACK, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        }
                        else if (GetBlock(x, y, z - 1) == EMPTY) TryEmit(block, Faces.BACK, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        // FRONT
                        if (z == maxZ - 1)
                        {
                            if (!PlaneBit(nPosZ, x * maxY + y)) TryEmit(block, Faces.FRONT, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                        }
                        else if (GetBlock(x, y, z + 1) == EMPTY) TryEmit(block, Faces.FRONT, x, y, z, ref vertBase, vertList, uvList, idxListU32);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool PlaneBit(ulong[] plane, int index)
        {
            if (plane == null) return false; int w = index >> 6; int b = index & 63; if (w >= plane.Length) return false; return (plane[w] & (1UL << b)) != 0UL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryEmit(ushort block, Faces face, int x, int y, int z,
                             ref uint vertBase, List<byte> vertList, List<byte> uvList, List<uint> idxListU32)
        {
            var faceVerts = RawFaceData.rawVertexData[face];
            for (int i = 0; i < 4; i++) { vertList.Add((byte)(faceVerts[i].x + x)); vertList.Add((byte)(faceVerts[i].y + y)); vertList.Add((byte)(faceVerts[i].z + z)); }
            var uvFace = atlas.GetBlockUVs(block, face); for (int i = 0; i < 4; i++) { uvList.Add(uvFace[i].x); uvList.Add(uvFace[i].y); }
            idxListU32.Add(vertBase + 0); idxListU32.Add(vertBase + 1); idxListU32.Add(vertBase + 2); idxListU32.Add(vertBase + 2); idxListU32.Add(vertBase + 3); idxListU32.Add(vertBase + 0);
            vertBase += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort GetBlock(int x, int y, int z)
        {
            int S2 = data.sectionSize; int sx = x / S2; int sy = y / S2; int sz = z / S2;
            if ((uint)sx >= (uint)data.sectionsX || (uint)sy >= (uint)data.sectionsY || (uint)sz >= (uint)data.sectionsZ) return 0;
            int localX = x - sx * S2; int localY = y - sy * S2; int localZ = z - sz * S2;
            int index = ((sx * data.sectionsY) + sy) * data.sectionsZ + sz; ref var desc = ref data.SectionDescs[index];
            switch (desc.Kind)
            {
                case 0: return 0;
                case 1: return desc.UniformBlockId;
                case 2:
                    if (desc.SparseIndices == null) return 0; int lid = ((localZ << 4) + localX) << 4 | localY; var idxArr = desc.SparseIndices; var blkArr = desc.SparseBlocks; for (int i = 0; i < idxArr.Length; i++) if (idxArr[i] == lid) return blkArr[i]; return 0;
                case 3:
                    return desc.ExpandedDense == null ? (ushort)0 : desc.ExpandedDense[((localZ << 4) + localX) << 4 | localY];
                case 4:
                    if (desc.PackedBitData == null || desc.Palette == null) return 0; return DecodePacked(ref desc, localX, localY, localZ);
                default: return 0;
            }
        }

        private static ushort DecodePacked(ref SectionPrerenderDesc desc, int lx, int ly, int lz)
        {
            int S3 = 16; int li = ((lz * S3 + lx) << 4) + ly; int bpi = desc.BitsPerIndex; if (bpi <= 0) return 0; long bitPos = (long)li * bpi; int word = (int)(bitPos >> 5); int bitOffset = (int)(bitPos & 31); uint value = desc.PackedBitData[word] >> bitOffset; int rem = 32 - bitOffset; if (rem < bpi) value |= desc.PackedBitData[word + 1] << rem; int mask = (1 << bpi) - 1; int paletteIndex = (int)(value & mask); if (paletteIndex < 0 || paletteIndex >= desc.Palette.Count) return 0; return desc.Palette[paletteIndex];
        }
    }
}

using MVGE_INF.Models.Generation;
using MVGE_GFX.Textures;
using MVGE_GFX.Models;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_INF.Loaders;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        private readonly ChunkPrerenderData data;
        private readonly BlockTextureAtlas atlas;
        private const ushort EMPTY = 0;
        // Cache to avoid repeated atlas UV -> tile lookups in fallback / generic emission paths.
        private readonly TileIndexCache _fallbackTileCache;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Occludes(ushort id) => id != 0 && TerrainLoader.IsOpaque(id);

        public SectionRender(ChunkPrerenderData data, BlockTextureAtlas atlas)
        {
            this.data = data; this.atlas = atlas;
            _fallbackTileCache = new TileIndexCache();
        }

        // Builds: one instance per emitted face
        // uses per-section specialized paths and a fallback per-section brute scan.
        // Outputs:
        //  faceCount: number of faces (instances)
        //  offsets:  faceCount * 3 bytes (x,y,z) block coordinates
        //  tileIndices: faceCount uint tile index into atlas
        //  faceDirs: faceCount bytes (0..5) orientation (LEFT,RIGHT,BOTTOM,TOP,BACK,FRONT)
        public void Build(out int faceCount, out byte[] offsets, out uint[] tileIndices, out byte[] faceDirs)
        {
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;
            if (maxX == 0 || maxY == 0 || maxZ == 0 || data.SectionDescs == null)
            {
                faceCount = 0; offsets = Array.Empty<byte>(); tileIndices = Array.Empty<uint>(); faceDirs = Array.Empty<byte>(); return;
            }

            var offsetList = new List<byte>(2048);
            var tileIndexList = new List<uint>(1024);
            var faceDirList = new List<byte>(1024);

            int S = data.sectionSize;

            for (int sx = 0; sx < data.sectionsX; sx++)
            {
                for (int sy = 0; sy < data.sectionsY; sy++)
                {
                    for (int sz = 0; sz < data.sectionsZ; sz++)
                    {
                        int si = ((sx * data.sectionsY) + sy) * data.sectionsZ + sz;
                        ref var desc = ref data.SectionDescs[si];
                        if (desc.Kind == 0 || desc.OpaqueCount == 0) continue; // empty / no opaque content

                        bool specializedHandled = false;
                        switch (desc.Kind)
                        {
                            case 0: // Empty
                                specializedHandled = EmitEmptySectionInstances(); // no-op placeholder for potential future use
                                break;
                            case 1: // Uniform
                                specializedHandled = EmitUniformSectionInstances(ref desc, sx, sy, sz, S, offsetList, tileIndexList, faceDirList);
                                break;
                            case 3: // DenseExpanded
                                specializedHandled = EmitExpandedSectionInstances(ref desc, sx, sy, sz, S, offsetList, tileIndexList, faceDirList);
                                break;
                            case 4: // Packed (single-id)
                                specializedHandled = EmitSinglePackedSectionInstances(ref desc, sx, sy, sz, S, offsetList, tileIndexList, faceDirList);
                                break;
                            case 5: // MultiPacked (multi-id packed)
                                specializedHandled = EmitMultiPackedSectionInstances(ref desc, sx, sy, sz, S, offsetList, tileIndexList, faceDirList);
                                break;
                        }
                        if (!specializedHandled)
                        {
                            FallbackSectionScan(ref desc, sx, sy, sz, S, maxX, maxY, maxZ,
                                offsetList, tileIndexList, faceDirList);
                        }
                    }
                }
            }

            faceCount = faceDirList.Count;
            offsets = offsetList.ToArray();
            tileIndices = tileIndexList.ToArray();
            faceDirs = faceDirList.ToArray();
        }

        // Fallback brute-force scan emitting face instances only
        private void FallbackSectionScan(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
                                         int maxX, int maxY, int maxZ,
                                         List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;
            int endX = Math.Min(baseX + S, maxX);
            int endY = Math.Min(baseY + S, maxY);
            int endZ = Math.Min(baseZ + S, maxZ);

            // Clamp to bounds (bounds track any content; opaque emission still guarded by GetBlock+Occludes)
            if (desc.HasBounds)
            {
                int bMinX = baseX + desc.MinLX; int bMaxX = baseX + desc.MaxLX;
                int bMinY = baseY + desc.MinLY; int bMaxY = baseY + desc.MaxLY;
                int bMinZ = baseZ + desc.MinLZ; int bMaxZ = baseZ + desc.MaxLZ;
                if (bMinX > baseX) baseX = bMinX; if (bMaxX + 1 < endX) endX = bMaxX + 1;
                if (bMinY > baseY) baseY = bMinY; if (bMaxY + 1 < endY) endY = bMaxY + 1;
                if (bMinZ > baseZ) baseZ = bMinZ; if (bMaxZ + 1 < endZ) endZ = bMaxZ + 1;
            }

            var nNegX = data.NeighborPlaneNegX; var nPosX = data.NeighborPlanePosX;
            var nNegY = data.NeighborPlaneNegY; var nPosY = data.NeighborPlanePosY;
            var nNegZ = data.NeighborPlaneNegZ; var nPosZ = data.NeighborPlanePosZ;

            for (int x = baseX; x < endX; x++)
            {
                for (int y = baseY; y < endY; y++)
                {
                    for (int z = baseZ; z < endZ; z++)
                    {
                        ushort block = GetBlock(x, y, z);
                        if (!Occludes(block)) continue; // only opaque block faces emitted in fallback

                        // LEFT (-X)
                        if ((x == 0 && !PlaneBit(nNegX, z * maxY + y)) || (x > 0 && !Occludes(GetBlock(x - 1, y, z))))
                            EmitFaceInstance(block, 0, x, y, z, offsetList, tileIndexList, faceDirList);
                        // RIGHT (+X)
                        if ((x == maxX - 1 && !PlaneBit(nPosX, z * maxY + y)) || (x < maxX - 1 && !Occludes(GetBlock(x + 1, y, z))))
                            EmitFaceInstance(block, 1, x, y, z, offsetList, tileIndexList, faceDirList);
                        // BOTTOM (-Y)
                        if ((y == 0 && !PlaneBit(nNegY, x * maxZ + z)) || (y > 0 && !Occludes(GetBlock(x, y - 1, z))))
                            EmitFaceInstance(block, 2, x, y, z, offsetList, tileIndexList, faceDirList);
                        // TOP (+Y)
                        if ((y == maxY - 1 && !PlaneBit(nPosY, x * maxZ + z)) || (y < maxY - 1 && !Occludes(GetBlock(x, y + 1, z))))
                            EmitFaceInstance(block, 3, x, y, z, offsetList, tileIndexList, faceDirList);
                        // BACK (-Z)
                        if ((z == 0 && !PlaneBit(nNegZ, x * maxY + y)) || (z > 0 && !Occludes(GetBlock(x, y, z - 1))))
                            EmitFaceInstance(block, 4, x, y, z, offsetList, tileIndexList, faceDirList);
                        // FRONT (+Z)
                        if ((z == maxZ - 1 && !PlaneBit(nPosZ, x * maxY + y)) || (z < maxZ - 1 && !Occludes(GetBlock(x, y, z + 1))))
                            EmitFaceInstance(block, 5, x, y, z, offsetList, tileIndexList, faceDirList);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitFaceInstance(ushort block, byte faceDir, int x, int y, int z,
                                      List<byte> offsetList, List<uint> tileIndexList, List<byte> faceDirList)
        {
            // Use shared tile index cache + helper to compute atlas tile index once and emit instance.
            uint tileIndex = _fallbackTileCache.Get(atlas, block, faceDir);
            EmitOneInstance(x, y, z, tileIndex, faceDir, offsetList, tileIndexList, faceDirList);
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
                case 0: return 0;                           // Empty
                case 1: return desc.UniformBlockId;          // Uniform
                case 3:                                     // DenseExpanded
                    return desc.ExpandedDense == null ? (ushort)0 : desc.ExpandedDense[((localZ << 4) + localX) << 4 | localY];
                case 4:                                     // Packed (single-id)
                case 5:                                     // MultiPacked (multi-id)
                    if (desc.PackedBitData == null || desc.Palette == null) return 0;
                    return DecodePacked(ref desc, localX, localY, localZ);
                default: return 0;
            }
        }

        private static ushort DecodePacked(ref SectionPrerenderDesc desc, int lx, int ly, int lz)
        {
            int S3 = 16; int li = ((lz * S3 + lx) << 4) + ly; int bpi = desc.BitsPerIndex; if (bpi <= 0) return 0; long bitPos = (long)li * bpi; int word = (int)(bitPos >> 5); int bitOffset = (int)(bitPos & 31); uint value = desc.PackedBitData[word] >> bitOffset; int rem = 32 - bitOffset; if (rem < bpi) value |= desc.PackedBitData[word + 1] << rem; int mask = (1 << bpi) - 1; int paletteIndex = (int)(value & mask); if (paletteIndex < 0 || paletteIndex >= desc.Palette.Count) return 0; return desc.Palette[paletteIndex];
        }
    }
}

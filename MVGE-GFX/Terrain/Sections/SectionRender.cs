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

        // Builds: one instance per emitted face for both opaque and (future) transparent passes.
        // Uses per-section specialized paths and a fallback per-section brute scan for opaque faces.
        // Transparent faces are emitted ONLY for sections that actually fall through to the fallback path.
        // Outputs:
        //  opaqueFaceCount: number of opaque faces (instances)
        //  opaqueOffsets:   opaqueFaceCount * 3 bytes (x,y,z) block coordinates
        //  opaqueTileIndices: opaqueFaceCount uint tile index into atlas
        //  opaqueFaceDirs:  opaqueFaceCount bytes (0..5) orientation (LEFT,RIGHT,BOTTOM,TOP,BACK,FRONT)
        //  transparentFaceCount: number of transparent faces (instances)
        //  transparentOffsets / transparentTileIndices / transparentFaceDirs
        public void Build(
            out int opaqueFaceCount,
            out byte[] opaqueOffsets,
            out uint[] opaqueTileIndices,
            out byte[] opaqueFaceDirs,
            out int transparentFaceCount,
            out byte[] transparentOffsets,
            out uint[] transparentTileIndices,
            out byte[] transparentFaceDirs)
        {
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;
            if (maxX == 0 || maxY == 0 || maxZ == 0 || data.SectionDescs == null)
            {
                opaqueFaceCount = 0; opaqueOffsets = Array.Empty<byte>(); opaqueTileIndices = Array.Empty<uint>(); opaqueFaceDirs = Array.Empty<byte>();
                transparentFaceCount = 0; transparentOffsets = Array.Empty<byte>(); transparentTileIndices = Array.Empty<uint>(); transparentFaceDirs = Array.Empty<byte>();
                return;
            }

            // Opaque accumulation lists
            var opaqueOffsetList = new List<byte>(2048);
            var opaqueTileIndexList = new List<uint>(1024);
            var opaqueFaceDirList = new List<byte>(1024);

            // Transparent accumulation lists (future use – capacity reserved conservatively when TransparentCount>0).
            var transparentOffsetList = new List<byte>();
            var transparentTileIndexList = new List<uint>();
            var transparentFaceDirList = new List<byte>();

            int S = data.sectionSize;

            for (int sx = 0; sx < data.sectionsX; sx++)
            {
                for (int sy = 0; sy < data.sectionsY; sy++)
                {
                    for (int sz = 0; sz < data.sectionsZ; sz++)
                    {
                        int si = ((sx * data.sectionsY) + sy) * data.sectionsZ + sz;
                        ref var desc = ref data.SectionDescs[si];

                        bool fallbackOnly = false; // only set true for debug purposes
                        bool specializedHandled = false;
                        if (fallbackOnly)
                        {
                            FallbackSectionScan(ref desc, sx, sy, sz, S, maxX, maxY, maxZ,
                                opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList,
                                transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        } else
                        {
                            switch (desc.Kind)
                            {
                                case 0: // Empty
                                    specializedHandled = EmitEmptySectionInstances(); // no-op placeholder
                                    break;
                                case 1: // Uniform
                                    specializedHandled = EmitUniformSectionInstances(ref desc, sx, sy, sz, S, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                                    break;
                            }
                            if (!specializedHandled)
                            {
                                FallbackSectionScan(ref desc, sx, sy, sz, S, maxX, maxY, maxZ,
                                    opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList,
                                    transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                            }
                        }
                    }
                }
            }

            // Assign opaque outputs
            opaqueFaceCount = opaqueFaceDirList.Count;
            opaqueOffsets = opaqueOffsetList.ToArray();
            opaqueTileIndices = opaqueTileIndexList.ToArray();
            opaqueFaceDirs = opaqueFaceDirList.ToArray();

            // Assign transparent outputs (currently emitted only by fallback path for transparent rules)
            transparentFaceCount = transparentFaceDirList.Count;
            transparentOffsets = transparentOffsetList.ToArray();
            transparentTileIndices = transparentTileIndexList.ToArray();
            transparentFaceDirs = transparentFaceDirList.ToArray();
        }

        // Fallback brute-force scan emitting face instances for opaque AND (new) transparent blocks.
        // Transparent emission rules (current commit placeholder):
        //   * A transparent block emits a face only when adjacent cell is air (0) or a *different* transparent block id.
        //   * Faces against opaque neighbors are culled (opaque occludes).
        //   * Faces shared between identical transparent ids are culled.
        private void FallbackSectionScan(ref SectionPrerenderDesc desc, int sx, int sy, int sz, int S,
                                         int maxX, int maxY, int maxZ,
                                         List<byte> opaqueOffsetList, List<uint> opaqueTileIndexList, List<byte> opaqueFaceDirList,
                                         List<byte> transparentOffsetList, List<uint> transparentTileIndexList, List<byte> transparentFaceDirList)
        {
            int baseX = sx * S; int baseY = sy * S; int baseZ = sz * S;
            int endX = Math.Min(baseX + S, maxX);
            int endY = Math.Min(baseY + S, maxY);
            int endZ = Math.Min(baseZ + S, maxZ);

            // Preserve original section extents for the transparent pass.
            int tBaseX = baseX, tBaseY = baseY, tBaseZ = baseZ;
            int tEndX = endX, tEndY = endY, tEndZ = endZ;

            // Clamp to bounds (intended to track any content; opaque emission still guarded by GetBlock+Occludes)
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

            // ---------------- OPAQUE PASS ----------------
            for (int x = baseX; x < endX; x++)
            {
                for (int y = baseY; y < endY; y++)
                {
                    for (int z = baseZ; z < endZ; z++)
                    {
                        ushort block = GetBlock(x, y, z);
                        if (!Occludes(block)) continue;

                        // LEFT (-X)
                        if ((x == 0 && !PlaneBit(nNegX, z * maxY + y)) || (x > 0 && !Occludes(GetBlock(x - 1, y, z))))
                            EmitFaceInstance(block, 0, x, y, z, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                        // RIGHT (+X)
                        if ((x == maxX - 1 && !PlaneBit(nPosX, z * maxY + y)) || (x < maxX - 1 && !Occludes(GetBlock(x + 1, y, z))))
                            EmitFaceInstance(block, 1, x, y, z, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                        // BOTTOM (-Y)
                        if ((y == 0 && !PlaneBit(nNegY, x * maxZ + z)) || (y > 0 && !Occludes(GetBlock(x, y - 1, z))))
                            EmitFaceInstance(block, 2, x, y, z, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                        // TOP (+Y)
                        if ((y == maxY - 1 && !PlaneBit(nPosY, x * maxZ + z)) || (y < maxY - 1 && !Occludes(GetBlock(x, y + 1, z))))
                            EmitFaceInstance(block, 3, x, y, z, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                        // BACK (-Z)
                        if ((z == 0 && !PlaneBit(nNegZ, x * maxY + y)) || (z > 0 && !Occludes(GetBlock(x, y, z - 1))))
                            EmitFaceInstance(block, 4, x, y, z, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                        // FRONT (+Z)
                        if ((z == maxZ - 1 && !PlaneBit(nPosZ, x * maxY + y)) || (z < maxZ - 1 && !Occludes(GetBlock(x, y, z + 1))))
                            EmitFaceInstance(block, 5, x, y, z, opaqueOffsetList, opaqueTileIndexList, opaqueFaceDirList);
                    }
                }
            }

            // ---------------- TRANSPARENT PASS ----------------
            // Use full section extents for transparent scan so transparent voxels outside opaque bounds are not skipped.
            baseX = tBaseX; baseY = tBaseY; baseZ = tBaseZ;
            endX = tEndX; endY = tEndY; endZ = tEndZ;

            // Neighbor transparent planes (ids) for boundary suppression
            var tNegX = data.NeighborTransparentPlaneNegX; var tPosX = data.NeighborTransparentPlanePosX;
            var tNegY = data.NeighborTransparentPlaneNegY; var tPosY = data.NeighborTransparentPlanePosY;
            var tNegZ = data.NeighborTransparentPlaneNegZ; var tPosZ = data.NeighborTransparentPlanePosZ;

            // ---------------- TRANSPARENT PASS ----------------
            for (int x = baseX; x < endX; x++)
            {
                for (int y = baseY; y < endY; y++)
                {
                    for (int z = baseZ; z < endZ; z++)
                    {
                        ushort block = GetBlock(x, y, z);
                        if (block == 0 || TerrainLoader.IsOpaque(block)) continue; // only non-air transparent blocks

                        // Helper local function for transparency visibility rule.
                        bool TransparentFaceVisible(int nx, int ny, int nz)
                        {
                            if (nx < 0)
                            {
                                int idx = z * maxY + y;
                                if (PlaneBit(nNegX, idx)) return false; // opaque neighbor
                                if (tNegX != null && (uint)idx < (uint)tNegX.Length && tNegX[idx] == block) return false; // same transparent id
                                return true;
                            }
                            if (nx >= maxX)
                            {
                                int idx = z * maxY + y;
                                if (PlaneBit(nPosX, idx)) return false;
                                if (tPosX != null && (uint)idx < (uint)tPosX.Length && tPosX[idx] == block) return false;
                                return true;
                            }
                            if (ny < 0)
                            {
                                int idx = x * maxZ + z;
                                if (PlaneBit(nNegY, idx)) return false;
                                if (tNegY != null && (uint)idx < (uint)tNegY.Length && tNegY[idx] == block) return false;
                                return true;
                            }
                            if (ny >= maxY)
                            {
                                int idx = x * maxZ + z;
                                if (PlaneBit(nPosY, idx)) return false;
                                if (tPosY != null && (uint)idx < (uint)tPosY.Length && tPosY[idx] == block) return false;
                                return true;
                            }
                            if (nz < 0)
                            {
                                int idx = x * maxY + y;
                                if (PlaneBit(nNegZ, idx)) return false;
                                if (tNegZ != null && (uint)idx < (uint)tNegZ.Length && tNegZ[idx] == block) return false;
                                return true;
                            }
                            if (nz >= maxZ)
                            {
                                int idx = x * maxY + y;
                                if (PlaneBit(nPosZ, idx)) return false;
                                if (tPosZ != null && (uint)idx < (uint)tPosZ.Length && tPosZ[idx] == block) return false;
                                return true;
                            }
                            // inside chunk: reuse local neighbor logic
                            ushort nb = GetBlock(nx, ny, nz);
                            if (nb == 0) return true; // air
                            bool nbTransparent = !TerrainLoader.IsOpaque(nb);
                            if (!nbTransparent) return false; // opaque neighbor hides
                            if (nb == block) return false; // same transparent id (culled)
                            return true; // different transparent id -> visible seam
                        }

                        // -X
                        if (TransparentFaceVisible(x - 1, y, z))
                            EmitFaceInstance(block, 0, x, y, z, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // +X
                        if (TransparentFaceVisible(x + 1, y, z))
                            EmitFaceInstance(block, 1, x, y, z, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // -Y
                        if (TransparentFaceVisible(x, y - 1, z))
                            EmitFaceInstance(block, 2, x, y, z, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // +Y
                        if (TransparentFaceVisible(x, y + 1, z))
                            EmitFaceInstance(block, 3, x, y, z, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // -Z
                        if (TransparentFaceVisible(x, y, z - 1))
                            EmitFaceInstance(block, 4, x, y, z, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
                        // +Z
                        if (TransparentFaceVisible(x, y, z + 1))
                            EmitFaceInstance(block, 5, x, y, z, transparentOffsetList, transparentTileIndexList, transparentFaceDirList);
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

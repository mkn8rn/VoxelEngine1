using System;
using System.Collections.Generic;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Generation.Models;
using MVGE_INF.Managers;
using MVGE_Tools.Noise;
using MVGE_GEN.Utils;
using MVGE_INF.Models.Generation;
using MVGE_INF.Loaders;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        // Static cache: block id -> base block type (built on first use)
        private static Dictionary<ushort, BaseBlockType> _blockIdToBaseType;
        private static readonly object _baseTypeInitLock = new();
        private static BaseBlockType GetBaseTypeFast(ushort id)
        {
            if (_blockIdToBaseType == null)
            {
                lock (_baseTypeInitLock)
                {
                    if (_blockIdToBaseType == null)
                    {
                        var dict = new Dictionary<ushort, BaseBlockType>(TerrainLoader.allBlockTypeObjects.Count);
                        foreach (var bt in TerrainLoader.allBlockTypeObjects)
                        {
                            if (!dict.ContainsKey(bt.ID))
                                dict[bt.ID] = bt.BaseType;
                        }
                        _blockIdToBaseType = dict;
                    }
                }
            }
            if (_blockIdToBaseType.TryGetValue(id, out var baseType)) return baseType;
            if (Enum.IsDefined(typeof(BaseBlockType), (byte)id)) return (BaseBlockType)(byte)id; // fallback if simple enum id
            return BaseBlockType.Empty;
        }

        private bool RuleTargetsBlock(SimpleReplacementRule rule, ushort blockId, BaseBlockType baseType)
        {
            if (rule.blocks_to_replace != null)
            {
                for (int i = 0; i < rule.blocks_to_replace.Count; i++)
                {
                    if (rule.blocks_to_replace[i].ID == blockId) return true;
                }
            }
            if (rule.base_blocks_to_replace != null)
            {
                for (int i = 0; i < rule.base_blocks_to_replace.Count; i++)
                {
                    if (rule.base_blocks_to_replace[i] == baseType) return true;
                }
            }
            return false;
        }

        private static bool SectionFullyInside(int sectionY0, int sectionY1, int? minY, int? maxY)
        {
            if (minY.HasValue && sectionY0 < minY.Value) return false;
            if (maxY.HasValue && sectionY1 > maxY.Value) return false;
            return true;
        }
        private static bool SectionOutside(int sectionY0, int sectionY1, int? minY, int? maxY)
        {
            if (minY.HasValue && sectionY1 < minY.Value) return true;
            if (maxY.HasValue && sectionY0 > maxY.Value) return true;
            return false;
        }

        private void ApplySimpleReplacementRules(int chunkBaseY, int topOfChunk)
        {
            var rules = biome.simpleReplacements;
            if (rules == null || rules.Count == 0) return;

            // Early fast-path skip for uniform chunks (AllStoneChunk / AllSoilChunk) if no rule targets the single block id or base type across any overlapping Y.
            if (AllStoneChunk || AllSoilChunk)
            {
                ushort uniformId = (ushort)(AllStoneChunk ? BaseBlockType.Stone : BaseBlockType.Soil);
                var baseType = GetBaseTypeFast(uniformId);
                bool anyAffects = false;
                foreach (var r in rules)
                {
                    if (r.microbiomeId.HasValue) continue;
                    if (SectionOutside(chunkBaseY, topOfChunk, r.absoluteMinYlevel, r.absoluteMaxYlevel)) continue;
                    if (RuleTargetsBlock(r, uniformId, baseType)) { anyAffects = true; break; }
                }
                if (!anyAffects) return;
            }

            int S = ChunkSection.SECTION_SIZE;
            // Iterate sections (x,y,z)
            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    int sectionY0 = chunkBaseY + sy * S;
                    int sectionY1 = sectionY0 + S - 1;
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null || sec.IsAllAir || sec.Palette == null || sec.Palette.Count == 0) continue;

                        bool paletteMutated = false;
                        bool voxelLevelMutated = false;

                        bool uniformNonAir = sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR && sec.NonAirCount == sec.VoxelCount && sec.VoxelCount != 0;
                        if (uniformNonAir)
                        {
                            ushort blockId = sec.Palette[1];
                            var baseType = GetBaseTypeFast(blockId);
                            ushort currentId = blockId;
                            bool changed = false;
                            foreach (var r in rules)
                            {
                                if (r.microbiomeId.HasValue) continue;
                                if (SectionOutside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel)) continue;
                                bool fullCover = SectionFullyInside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel);
                                if (!fullCover) continue;
                                if (RuleTargetsBlock(r, currentId, baseType))
                                {
                                    currentId = r.block_type.ID;
                                    baseType = GetBaseTypeFast(currentId);
                                    changed = true;
                                }
                            }
                            if (changed)
                            {
                                paletteMutated = true;
                                sec.Palette[1] = currentId;
                                sec.PaletteLookup[currentId] = 1;
                                if (currentId != blockId && sec.PaletteLookup.ContainsKey(blockId) && blockId != ChunkSection.AIR)
                                    sec.PaletteLookup.Remove(blockId);
                            }

                            bool needsPartial = false;
                            foreach (var r in rules)
                            {
                                if (r.microbiomeId.HasValue) continue;
                                if (SectionOutside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel)) continue;
                                bool fullCover = SectionFullyInside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel);
                                if (!fullCover && RuleTargetsBlock(r, currentId, GetBaseTypeFast(currentId))) { needsPartial = true; break; }
                            }
                            if (!needsPartial)
                            {
                                if (paletteMutated) SectionUtils.InvalidateIncrementalClassification(sec, preserveUniformIfStillValid: true);
                                continue;
                            }
                        }

                        var palette = sec.Palette;
                        var paletteBaseTypes = new BaseBlockType[palette.Count];
                        for (int pi = 0; pi < palette.Count; pi++) paletteBaseTypes[pi] = GetBaseTypeFast(palette[pi]);

                        bool anyPaletteFullChange = false;
                        for (int ri = 0; ri < rules.Count; ri++)
                        {
                            var r = rules[ri];
                            if (r.microbiomeId.HasValue) continue;
                            if (SectionOutside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel)) continue;
                            bool fullCover = SectionFullyInside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel);
                            if (!fullCover) continue;
                            ushort replacement = r.block_type.ID;
                            for (int pi = 1; pi < palette.Count; pi++)
                            {
                                ushort id = palette[pi];
                                if (RuleTargetsBlock(r, id, paletteBaseTypes[pi]) && id != replacement)
                                {
                                    palette[pi] = replacement;
                                    paletteBaseTypes[pi] = GetBaseTypeFast(replacement);
                                    anyPaletteFullChange = true;
                                }
                            }
                        }
                        if (anyPaletteFullChange)
                        {
                            paletteMutated = true;
                            sec.PaletteLookup.Clear();
                            for (int pi = 0; pi < palette.Count; pi++) sec.PaletteLookup[palette[pi]] = pi;
                        }

                        bool hasPartialRule = false;
                        foreach (var r in rules)
                        {
                            if (r.microbiomeId.HasValue) continue;
                            if (SectionOutside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel)) continue;
                            bool fullCover = SectionFullyInside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel);
                            if (!fullCover) { hasPartialRule = true; break; }
                        }
                        if (!hasPartialRule)
                        {
                            if (paletteMutated) SectionUtils.InvalidateIncrementalClassification(sec, preserveUniformIfStillValid: true);
                            continue;
                        }

                        int plane = S * S;
                        int bpi2 = sec.BitsPerIndex;
                        if (bpi2 == 0 || sec.BitData == null) { if (paletteMutated) SectionUtils.InvalidateIncrementalClassification(sec, true); continue; }
                        uint[] data = sec.BitData;

                        foreach (var r in rules)
                        {
                            if (r.microbiomeId.HasValue) continue;
                            if (SectionOutside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel)) continue;
                            bool fullCover = SectionFullyInside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel);
                            if (fullCover) continue;

                            int yMin = r.absoluteMinYlevel.HasValue ? Math.Max(sectionY0, r.absoluteMinYlevel.Value) : sectionY0;
                            int yMax = r.absoluteMaxYlevel.HasValue ? Math.Min(sectionY1, r.absoluteMaxYlevel.Value) : sectionY1;
                            if (yMax < yMin) continue;
                            int lyStart = yMin - sectionY0;
                            int lyEnd = yMax - sectionY0;

                            List<int> targetPalette = new();
                            for (int pi = 1; pi < palette.Count; pi++) if (RuleTargetsBlock(r, palette[pi], paletteBaseTypes[pi])) targetPalette.Add(pi);
                            if (targetPalette.Count == 0) continue;

                            if (!sec.PaletteLookup.TryGetValue(r.block_type.ID, out int replIndex))
                            {
                                replIndex = palette.Count;
                                palette.Add(r.block_type.ID);
                                paletteBaseTypes = new BaseBlockType[palette.Count];
                                for (int pi = 0; pi < palette.Count; pi++) paletteBaseTypes[pi] = GetBaseTypeFast(palette[pi]);
                                sec.PaletteLookup[r.block_type.ID] = replIndex;
                                if (replIndex >= (1 << bpi2))
                                {
                                    int oldBits = bpi2;
                                    int paletteCountMinusOne = palette.Count - 1;
                                    int needed = paletteCountMinusOne <= 0 ? 1 : (int)Math.Log2(paletteCountMinusOne) + 1;
                                    if (needed > bpi2)
                                    {
                                        bpi2 = needed;
                                        long totalBits = (long)sec.VoxelCount * bpi2;
                                        int uintCount = (int)((totalBits + 31) / 32);
                                        uint[] newData = new uint[uintCount];
                                        for (int vi = 0; vi < sec.VoxelCount; vi++)
                                        {
                                            long oldBitPos = (long)vi * oldBits;
                                            int oldDataIdx = (int)(oldBitPos >> 5);
                                            int oldOffset = (int)(oldBitPos & 31);
                                            uint val = data[oldDataIdx] >> oldOffset;
                                            int oldRemain = 32 - oldOffset;
                                            int oldMask = (1 << oldBits) - 1;
                                            if (oldRemain < oldBits) val |= data[oldDataIdx + 1] << oldRemain;
                                            int palIdx = (int)(val & (uint)oldMask);
                                            long newBitPos = (long)vi * bpi2;
                                            int newDataIdx = (int)(newBitPos >> 5);
                                            int newOffset = (int)(newBitPos & 31);
                                            int newMask = (1 << bpi2) - 1;
                                            newData[newDataIdx] &= (uint)~(newMask << newOffset);
                                            newData[newDataIdx] |= (uint)palIdx << newOffset;
                                            int newRemain = 32 - newOffset;
                                            if (newRemain < bpi2)
                                            {
                                                int bitsInNext = bpi2 - newRemain;
                                                uint nextMask = (uint)((1 << bitsInNext) - 1);
                                                newData[newDataIdx + 1] &= ~nextMask;
                                                newData[newDataIdx + 1] |= (uint)palIdx >> newRemain;
                                            }
                                        }
                                        data = newData;
                                        sec.BitData = newData;
                                        sec.BitsPerIndex = bpi2;
                                    }
                                }
                                data = sec.BitData;
                            }

                            var targetSet = new HashSet<int>(targetPalette);
                            int mask = (1 << bpi2) - 1;
                            for (int ly = lyStart; ly <= lyEnd; ly++)
                            {
                                int yBase = ly * plane;
                                for (int lz = 0; lz < S; lz++)
                                {
                                    int zBase = yBase + lz * S;
                                    for (int lx = 0; lx < S; lx++)
                                    {
                                        int linear = zBase + lx;
                                        long bitPos = (long)linear * bpi2;
                                        int dataIndex = (int)(bitPos >> 5);
                                        int bitOffset = (int)(bitPos & 31);
                                        uint value = data[dataIndex] >> bitOffset;
                                        int remaining = 32 - bitOffset;
                                        if (remaining < bpi2) value |= data[dataIndex + 1] << remaining;
                                        int palIdx = (int)(value & (uint)mask);
                                        if (!targetSet.Contains(palIdx) || palIdx == replIndex) continue;
                                        data[dataIndex] &= (uint)~(mask << bitOffset);
                                        data[dataIndex] |= (uint)replIndex << bitOffset;
                                        if (remaining < bpi2)
                                        {
                                            int bitsInNext = bpi2 - remaining;
                                            uint nextMask = (uint)((1 << bitsInNext) - 1);
                                            data[dataIndex + 1] &= ~nextMask;
                                            data[dataIndex + 1] |= (uint)replIndex >> remaining;
                                        }
                                        voxelLevelMutated = true;
                                    }
                                }
                            }
                        }

                        if (paletteMutated || voxelLevelMutated)
                        {
                            SectionUtils.InvalidateIncrementalClassification(sec, preserveUniformIfStillValid: paletteMutated && !voxelLevelMutated);
                        }
                    }
                }
            }
        }

        private void DetectAllStoneOrSoil(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            // Combined detection pass for uniform stone and uniform soil.
            // Stone conditions per column (x,z):
            // 1. columnHeight >= topOfChunk (terrain covers chunk vertically)
            // 2. Stone band + computed stone depth covers [chunkBaseY, topOfChunk]
            //    => chunkBaseY >= stoneBandStartWorld AND topOfChunk <= finalStoneTopWorld.
            // Soil conditions per column (x,z):
            // 1. columnHeight >= topOfChunk
            // 2. Compute stone layer (as normal) to obtain finalStoneTopWorld
            // 3. Determine soilStartWorld (just above stone top or biome band start if no stone), clamp to biome soil bounds; compute soilEndWorld with biome depth
            // 4. Entire chunk vertical span [chunkBaseY, topOfChunk] lies fully within [soilStartWorld, soilEndWorld]
            // 5. Chunk sits strictly above any stone (chunkBaseY > finalStoneTopWorld)
            // If EVERY column satisfies either set, we flag the corresponding uniform chunk type.
            int maxX = dimX;
            int maxZ = dimZ;
            bool possibleStone = true;

            int stoneMinY = biome.stoneMinYLevel;
            int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel;
            int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth;
            int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth;
            int stoneMaxDepthSpec = biome.stoneMaxDepth;

            bool possibleSoil = (topOfChunk >= soilMinY) && (chunkBaseY <= soilMaxY);

            for (int x = 0; x < maxX && (possibleStone || possibleSoil); x++)
            {
                for (int z = 0; z < maxZ && (possibleStone || possibleSoil); z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    if (columnHeight < topOfChunk)
                    {
                        possibleStone = false;
                        possibleSoil = false;
                        break;
                    }

                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight);
                    int available = stoneBandEndWorld - stoneBandStartWorld + 1;
                    int finalStoneTopWorld = stoneBandStartWorld - 1;
                    if (stoneBandEndWorld >= stoneBandStartWorld && available > 0)
                    {
                        int soilMinReserve = Math.Clamp(soilMinDepthSpec, 0, available);
                        int rawStoneDepth = available - soilMinReserve;
                        int stoneDepth = Math.Min(stoneMaxDepthSpec, Math.Max(stoneMinDepthSpec, rawStoneDepth));
                        if (stoneDepth > available) stoneDepth = available;
                        if (stoneDepth > 0)
                            finalStoneTopWorld = stoneBandStartWorld + stoneDepth - 1;
                        else
                            finalStoneTopWorld = stoneBandStartWorld - 1;
                    }
                    else
                    {
                        available = 0;
                    }

                    if (possibleStone)
                    {
                        if (available <= 0)
                        {
                            possibleStone = false;
                        }
                        else if (chunkBaseY < stoneBandStartWorld || topOfChunk > finalStoneTopWorld)
                        {
                            possibleStone = false;
                        }
                    }

                    if (possibleSoil)
                    {
                        int soilStartWorld = finalStoneTopWorld + 1;
                        if (finalStoneTopWorld < stoneBandStartWorld)
                            soilStartWorld = stoneBandStartWorld;
                        if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
                        if (soilStartWorld > soilMaxY) { possibleSoil = false; continue; }
                        int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);
                        if (soilBandCapWorld < soilStartWorld) { possibleSoil = false; continue; }
                        int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                        if (soilAvailable <= 0) { possibleSoil = false; continue; }
                        int soilDepth = Math.Min(soilMaxDepthSpec, soilAvailable);
                        int soilEndWorld = soilStartWorld + soilDepth - 1;
                        if (chunkBaseY < soilStartWorld || topOfChunk > soilEndWorld || chunkBaseY <= finalStoneTopWorld)
                            possibleSoil = false;
                    }
                }
            }

            if (possibleStone)
            {
                AllStoneChunk = true;
                IsEmpty = false;
                CreateUniformSections((ushort)BaseBlockType.Stone);
                FaceSolidNegX = FaceSolidPosX = FaceSolidNegY = FaceSolidPosY = FaceSolidNegZ = FaceSolidPosZ = true;
                return;
            }
            if (possibleSoil)
            {
                AllSoilChunk = true;
                IsEmpty = false;
                CreateUniformSections((ushort)BaseBlockType.Soil);
                FaceSolidNegX = FaceSolidPosX = FaceSolidNegY = FaceSolidPosY = FaceSolidNegZ = FaceSolidPosZ = true;
            }
        }

        private void CreateUniformSections(ushort blockId)
        {
            int S = ChunkSection.SECTION_SIZE;
            int voxelsPerSection = S * S * S;
            int bitsPerIndex = 1;
            int indices = voxelsPerSection;
            int uintCount = (indices * bitsPerIndex + 31) >> 5;

            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = new ChunkSection
                        {
                            IsAllAir = false,
                            Palette = new List<ushort> { ChunkSection.AIR, blockId },
                            PaletteLookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 }, { blockId, 1 } },
                            BitsPerIndex = bitsPerIndex,
                            VoxelCount = voxelsPerSection,
                            NonAirCount = voxelsPerSection,
                            BitData = System.Buffers.ArrayPool<uint>.Shared.Rent(uintCount),
                            Kind = ChunkSection.RepresentationKind.Packed // will later classify to Uniform
                        };
                        // Fill all bits to 1 for palette index 1 (since BitsPerIndex==1). Need to set only required uintCount entries.
                        for (int i = 0; i < uintCount; i++) sec.BitData[i] = 0xFFFFFFFFu;
                        sections[sx, sy, sz] = sec;
                    }
                }
            }
        }

        public void GenerateInitialChunkData()
        {
            // ----- BASIC DIMENSIONS & HEIGHTMAP -----
            // Establish local voxel dimensions for this chunk and obtain (or reuse) the heightmap.
            // The heightmap supplies the surface (inclusive) world Y for every (x,z) column at chunk resolution.
            int maxX = dimX;              // chunk X dimension in voxels
            int maxY = dimY;              // chunk Y dimension in voxels
            int maxZ = dimZ;              // chunk Z dimension in voxels
            // Reuse precomputed heightmap (shared for vertical stack) or generate new
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);
            int chunkBaseY = (int)position.Y;              // world Y at bottom of chunk
            int topOfChunk = chunkBaseY + maxY - 1;        // world Y at top of chunk
            const int LocalBurialMargin = 2;               // small margin so a column counts as exposing chunk even if surface slightly above

            // ----- BURIAL / ALL-AIR CLASSIFICATION PASS -----
            // We compute two things:
            //  1. allBuried (later stored into candidateFullyBuried): true if the top of this chunk lies strictly below
            //     (surfaceHeight - LocalBurialMargin) for EVERY (x,z) column; meaning chunk volume is deeply underground.
            //  2. maxSurface: highest sampled surface across all columns, used for a fast all-air early return when
            //     the entire chunk sits above terrain.
            bool allBuried = true;
            int maxSurface = int.MinValue;
            for (int x = 0; x < maxX && allBuried; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int surface = (int)heightmap[x, z];
                    if (surface > maxSurface) maxSurface = surface; // track highest surface so far
                    if (topOfChunk >= surface - LocalBurialMargin)
                    {
                        // One column disproves burial; we break early but must still finish maxSurface collection below.
                        allBuried = false;
                        break;
                    }
                }
            }
            // If burial was disproved by early exit we must finish scanning remaining columns for maxSurface.
            if (!allBuried)
            {
                for (int x = 0; x < maxX; x++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        int surface = (int)heightmap[x, z];
                        if (surface > maxSurface) maxSurface = surface;
                    }
                }
            }
            if (allBuried) candidateFullyBuried = true; // mark potential fully-buried chunk (naming differs from older FullyBuried flag)

            // ----- ALL-AIR FAST PATH -----
            // If the chunk base Y is higher than every surface sample, the entire volume is air and we can exit early
            // without allocating/initializing section data. This drastically reduces work for empty sky chunks.
            if (chunkBaseY > maxSurface)
            {
                AllAirChunk = true;
                IsEmpty = true;
                precomputedHeightmap = null; // release shared reference for GC / reuse cycle
                return;
            }

            // ----- UNIFORM CONTENT DETECTION (STONE / SOIL) -----
            // Detect if ENTIRE chunk volume can be represented as exclusively stone or exclusively soil.
            // If successful, we allocate compact uniform sections and skip per-column population below.
            DetectAllStoneOrSoil(heightmap, chunkBaseY, topOfChunk);

            // ----- GENERAL COLUMN MATERIAL FILL (STONE + SOIL) -----
            // If not a uniform stone or soil chunk we fall back to a per-column layered fill honoring biome bands
            // and min/max depth constraints. Stone is written first (lower layer) followed by soil overlay.
            if (!AllStoneChunk && !AllSoilChunk)
            {
                // Absolute biome Y limits for stone & soil generation bands.
                int stoneMinY = biome.stoneMinYLevel;
                int stoneMaxY = biome.stoneMaxYLevel;
                int soilMinY = biome.soilMinYLevel;
                int soilMaxY = biome.soilMaxYLevel;
                for (int x = 0; x < maxX; x++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        int columnHeight = (int)heightmap[x, z]; // inclusive surface height for this column
                        if (columnHeight < chunkBaseY) continue; // Entire column lies below the chunk -> remains air

                        // --- Stone layer calculation ---
                        // Determine overlap of biome stone band with column; compute stone depth after reserving
                        // minimal soil thickness near the surface (soilMinDepth). Apply biome min/max stone depths.
                        int stoneBandStartWorld = Math.Max(stoneMinY, 0);          // lower clamp (never below 0)
                        int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight); // stone cannot extend above surface
                        if (stoneBandEndWorld < stoneBandStartWorld) continue;      // no stone band intersection
                        int available = stoneBandEndWorld - stoneBandStartWorld + 1; // vertical span available for (stone+reserved soil)
                        if (available <= 0) continue;
                        int soilMinReserve = Math.Clamp(biome.soilMinDepth, 0, available); // guaranteed soil reservation
                        int stoneMinDepth = biome.stoneMinDepth;
                        int stoneMaxDepth = biome.stoneMaxDepth; // restored variable
                        int rawStoneDepth = available - soilMinReserve;              // tentative stone depth before clamping
                        int stoneDepth = Math.Min(stoneMaxDepth, Math.Max(stoneMinDepth, rawStoneDepth));
                        if (stoneDepth > available) stoneDepth = available;          // final safety clamp
                        int finalStoneBottomWorld = stoneBandStartWorld;
                        int finalStoneTopWorld = finalStoneBottomWorld + stoneDepth - 1; // inclusive top of placed stone

                        // Write stone voxels within chunk-local coordinate space, splitting at section boundaries.
                        int localStoneStart = finalStoneBottomWorld - chunkBaseY;
                        int localStoneEnd = finalStoneTopWorld - chunkBaseY;
                        if (localStoneEnd >= 0 && localStoneStart < maxY)
                        {
                            if (localStoneStart < 0) localStoneStart = 0;             // clip below chunk
                            if (localStoneEnd >= maxY) localStoneEnd = maxY - 1;      // clip above chunk
                            if (localStoneStart <= localStoneEnd)
                            {
                                int syStart = localStoneStart >> SECTION_SHIFT;       // first section index (Y)
                                int syEnd = localStoneEnd >> SECTION_SHIFT;           // last section index (Y)
                                int ox = x & SECTION_MASK;                            // intra-section X coordinate
                                int oz = z & SECTION_MASK;                            // intra-section Z coordinate
                                int sx = x >> SECTION_SHIFT;                          // section grid X index
                                int sz = z >> SECTION_SHIFT;                          // section grid Z index
                                for (int sy = syStart; sy <= syEnd; sy++)
                                {
                                    var sec = sections[sx, sy, sz];
                                    if (sec == null) { sec = new ChunkSection(); sections[sx, sy, sz] = sec; }
                                    int yStartInSection = (sy == syStart) ? (localStoneStart & SECTION_MASK) : 0;
                                    int yEndInSection = (sy == syEnd) ? (localStoneEnd & SECTION_MASK) : (ChunkSection.SECTION_SIZE - 1);
                                    SectionUtils.FillColumnRangeInitial(sec, ox, oz, yStartInSection, yEndInSection, (ushort)BaseBlockType.Stone);
                                }
                            }
                        }

                        // --- Soil layer calculation ---
                        // Soil begins immediately above the stone top (or at stone band start if no stone placed) and
                        // is limited by the biome soil band and configured maximum depth.
                        int soilStartWorld = finalStoneTopWorld + 1;
                        if (stoneDepth == 0) soilStartWorld = stoneBandStartWorld;  // no stone: soil may occupy lower band
                        if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;   // clamp into soil biome band
                        if (soilStartWorld > soilMaxY) continue;                    // lies entirely above soil band
                        int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);    // cannot exceed surface or biome max
                        if (soilBandCapWorld < soilStartWorld) continue;            // insufficient room
                        int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                        if (soilAvailable <= 0) continue;
                        int soilMaxDepth = biome.soilMaxDepth;
                        int soilDepth = Math.Min(soilMaxDepth, soilAvailable);
                        int soilEndWorld = soilStartWorld + soilDepth - 1;          // inclusive top of soil

                        // Write soil voxels similar to stone pass.
                        int localSoilStart = soilStartWorld - chunkBaseY;
                        int localSoilEnd = soilEndWorld - chunkBaseY;
                        if (localSoilEnd >= 0 && localSoilStart < maxY)
                        {
                            if (localSoilStart < 0) localSoilStart = 0;
                            if (localSoilEnd >= maxY) localSoilEnd = maxY - 1;
                            if (localSoilStart <= localSoilEnd)
                            {
                                int syStart = localSoilStart >> SECTION_SHIFT;
                                int syEnd = localSoilEnd >> SECTION_SHIFT;
                                int ox = x & SECTION_MASK;
                                int oz = z & SECTION_MASK;
                                int sx = x >> SECTION_SHIFT;
                                int sz = z >> SECTION_SHIFT;
                                for (int sy = syStart; sy <= syEnd; sy++)
                                {
                                    var sec = sections[sx, sy, sz];
                                    if (sec == null) { sec = new ChunkSection(); sections[sx, sy, sz] = sec; }
                                    int yStartInSection = (sy == syStart) ? (localSoilStart & SECTION_MASK) : 0;
                                    int yEndInSection = (sy == syEnd) ? (localSoilEnd & SECTION_MASK) : (ChunkSection.SECTION_SIZE - 1);
                                    SectionUtils.FillColumnRangeInitial(sec, ox, oz, yStartInSection, yEndInSection, (ushort)BaseBlockType.Soil);
                                }
                            }
                        }
                    }
                }
            }
            // ----- SECOND PASS: SIMPLE REPLACEMENT RULES -----
            // Apply ordered biome replacement rules (e.g., convert some stone to ore) using palette/section optimizations.
            ApplySimpleReplacementRules(chunkBaseY, topOfChunk);

            // ----- POST-REPLACEMENT UNIFORMITY CHECK -----
            // If the chunk started as AllStoneChunk or AllSoilChunk it may still be fully uniform after replacement rules
            // (either unchanged or transformed into a different single block id). We record this as a generic uniform block fast-path.
            if (AllStoneChunk || AllSoilChunk)
            {
                ushort uniformId = 0;
                bool stillUniform = true;
                // Scan sections until a differing/non-uniform section found.
                for (int sx = 0; sx < sectionsX && stillUniform; sx++)
                {
                    for (int sy = 0; sy < sectionsY && stillUniform; sy++)
                    {
                        for (int sz = 0; sz < sectionsZ && stillUniform; sz++)
                        {
                            var sec = sections[sx, sy, sz];
                            if (sec == null || sec.IsAllAir)
                            {
                                // A null/air section would contradict earlier AllStoneChunk/AllSoilChunk classification, treat as non-uniform safety.
                                stillUniform = false; break;
                            }
                            if (!(sec.Palette != null && sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR && sec.NonAirCount == sec.VoxelCount && sec.VoxelCount != 0))
                            {
                                stillUniform = false; break;
                            }
                            ushort id = sec.Palette[1];
                            if (uniformId == 0)
                            {
                                uniformId = id; // first section's block id
                            }
                            else if (uniformId != id)
                            {
                                stillUniform = false; break;
                            }
                        }
                    }
                }
                if (stillUniform && uniformId != 0)
                {
                    AllOneBlockChunk = true;
                    AllOneBlockBlockId = uniformId;
                }
            }

            // ----- CLASSIFY SECTIONS INTO MULTI-FORM STORAGE -----
            for (int sx = 0; sx < sectionsX; sx++)
            {
                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var sec = sections[sx, sy, sz];
                        if (sec == null) continue;
                        // Populate VoxelCount if not set
                        if (sec.VoxelCount == 0) sec.VoxelCount = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE;
                        if (SectionUtils.EnableFastSectionClassification)
                            SectionUtils.FastFinalizeSection(sec);
                        else
                            SectionUtils.ClassifyRepresentation(sec);
                    }
                }
            }

            // Release heightmap reference now that generation for this chunk is complete.
            precomputedHeightmap = null;

            // Build initial boundary plane caches after generation & replacements
            BuildAllBoundaryPlanesInitial();
        }
    }
}
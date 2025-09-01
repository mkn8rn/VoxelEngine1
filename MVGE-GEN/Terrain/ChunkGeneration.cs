using System;
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
        private void ApplySimpleReplacementRules(int chunkBaseY, int topOfChunk)
        {
            var rules = biome.simpleReplacements;
            if (rules == null || rules.Count == 0) return;

            // Early chunk-level uniform short-circuit moved to helper for maintainability.
            if (TryChunkLevelUniformReplacement(chunkBaseY, topOfChunk)) return; // helper applied full-chunk mapping

            if (biome.compiledSimpleReplacementRules.Length > 0 && biome.sectionYRuleBuckets.Length == sectionsY)
            {
                ApplySimpleReplacementRules(chunkBaseY);
                return;
            }
        }

        // --------------------------------------------------------------------------------------------
        // TryChunkLevelUniformReplacement:
        // Early chunk-level uniform short-circuit: if the entire chunk was generated as all stone or all soil
        // and every rule that targets that base type either fully covers the chunk vertically (or none exist),
        // producing a single final replacement id for the whole vertical span, we can apply the mapping once
        // to every uniform section and skip per-section rule processing entirely.
        // Conditions for safe shortcut:
        //   * AllStoneChunk or AllSoilChunk already established (all sections created uniform with that id)
        //   * No partially-overlapping (vertical slice) rule targeting the base type exists (would create non-uniform)
        //   * Folding the ordered full-cover rules yields a single final id (could be unchanged) for the base type
        // This avoids: iterating buckets per section + per-section finalize later.
        // --------------------------------------------------------------------------------------------
        private bool TryChunkLevelUniformReplacement(int chunkBaseY, int topOfChunk)
        {
            if (!(AllStoneChunk || AllSoilChunk)) return false;
            if (biome.compiledSimpleReplacementRules.Length == 0) return false;

            var compiledAll = biome.compiledSimpleReplacementRules;
            // Chunk vertical span
            int chunkY0 = chunkBaseY;
            int chunkY1 = topOfChunk;
            BaseBlockType baseType = AllStoneChunk ? BaseBlockType.Stone : BaseBlockType.Soil;
            ushort originalId = (ushort)(AllStoneChunk ? BaseBlockType.Stone : BaseBlockType.Soil);
            ushort currentId = originalId;
            bool abort = false; // abort shortcut if any partial range rule applies
            bool anyTargetingRule = false;
            // Iterate rules in their precompiled priority order
            for (int i = 0; i < compiledAll.Length; i++)
            {
                var cr = compiledAll[i];
                // Check if rule targets this base type (base type bit or specific ids list containing original/current id)
                bool targets = ((cr.BaseTypeBitMask >> (int)baseType) & 1u) != 0u;
                if (!targets && cr.SpecificIdsSorted.Length > 0)
                {
                    // We only care if it lists either the original uniform id or any subsequent mapped id
                    // Because after replacement currentId can change; keep it dynamic.
                    if (Array.BinarySearch(cr.SpecificIdsSorted, currentId) >= 0)
                        targets = true;
                }
                if (!targets) continue;
                // Rule targets the uniform id / base type. If it doesn't intersect vertically skip.
                if (cr.MaxY < chunkY0 || cr.MinY > chunkY1) continue;
                anyTargetingRule = true;
                // If it intersects but does not fully cover entire chunk span -> cannot shortcut.
                if (!(cr.MinY <= chunkY0 && cr.MaxY >= chunkY1)) { abort = true; break; }
                // Full cover: apply mapping (in priority order later rules override earlier ones)
                if (cr.Matches(currentId, baseType))
                {
                    currentId = cr.ReplacementId;
                }
            }
            if (abort) return false; // need per-section processing

            // No partial vertical slicing rules for this base type => safe uniform mapping
            // (even if no rule matched we still mark uniform fast path to skip per-section pass)
            // Update each uniform section's id only if changed.
            if (currentId != originalId || anyTargetingRule)
            {
                for (int sx = 0; sx < sectionsX; sx++)
                    for (int sy = 0; sy < sectionsY; sy++)
                        for (int sz = 0; sz < sectionsZ; sz++)
                        {
                            var sec = sections[sx, sy, sz];
                            if (sec == null) continue;
                            if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                            {
                                sec.UniformBlockId = currentId;
                                sec.IdMapDirty = false; // occupancy unchanged, keep metadata
                                sec.StructuralDirty = false;
                                sec.MetadataBuilt = true;
                            }
                        }
                AllOneBlockChunk = true;
                AllOneBlockBlockId = currentId;
            }
            return true; // shortcut executed
        }

        private void ApplySimpleReplacementRules(int chunkBaseY)
        {
            // Batched strategy: for each section Y fetch bucket indices, build a folded mapping once per section, apply
            int sectionSize = ChunkSection.SECTION_SIZE;
            var compiled = biome.compiledSimpleReplacementRules;
            var buckets = biome.sectionYRuleBuckets;
            for (int sy=0; sy<sectionsY; sy++)
            {
                var bucket = buckets[sy];
                if (bucket == null || bucket.Length == 0) continue;
                int sectionWorldY0 = chunkBaseY + sy * sectionSize;
                int sectionWorldY1 = sectionWorldY0 + sectionSize - 1;
                for (int sx=0; sx<sectionsX; sx++)
                {
                    for (int sz=0; sz<sectionsZ; sz++)
                    {
                        var sec = sections[sx,sy,sz]; if (sec==null) continue;
                        // Build folding arrays from current distinct ids if scratch exists; else attempt light fast-path
                        SectionUtils.SectionApplySimpleReplacementRules(sec, sectionWorldY0, sectionWorldY1, compiled, bucket, GetBaseTypeFast);
                    }
                }
            }
        }

        private void CreateUniformSections(ushort blockId)
        {
            int S = ChunkSection.SECTION_SIZE;
            int voxelsPerSection = S * S * S;
            for (int sx = 0; sx < sectionsX; sx++)
            for (int sy = 0; sy < sectionsY; sy++)
            for (int sz = 0; sz < sectionsZ; sz++)
            {
                var sec = new ChunkSection
                {
                    IsAllAir = false,
                    Kind = ChunkSection.RepresentationKind.Uniform,
                    UniformBlockId = blockId,
                    NonAirCount = voxelsPerSection,
                    VoxelCount = voxelsPerSection,
                    CompletelyFull = true,
                    MetadataBuilt = true,
                    HasBounds = true,
                    MinLX = 0, MinLY = 0, MinLZ = 0, MaxLX = 15, MaxLY = 15, MaxLZ = 15
                };
                sections[sx, sy, sz] = sec;
            }
        }

        private void DetectAllStoneOrSoil(float[,] heightmap, int chunkBaseY, int topOfChunk)
        {
            int maxX = dimX; int maxZ = dimZ;
            bool possibleStone = true;
            int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
            int soilMinY = biome.soilMinYLevel; int soilMaxY = biome.soilMaxYLevel;
            int soilMinDepthSpec = biome.soilMinDepth; int soilMaxDepthSpec = biome.soilMaxDepth;
            int stoneMinDepthSpec = biome.stoneMinDepth; int stoneMaxDepthSpec = biome.stoneMaxDepth;
            bool possibleSoil = (topOfChunk >= soilMinY) && (chunkBaseY <= soilMaxY);
            for (int x = 0; x < maxX && (possibleStone || possibleSoil); x++)
            {
                for (int z = 0; z < maxZ && (possibleStone || possibleSoil); z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    if (columnHeight < topOfChunk){ possibleStone=false; possibleSoil=false; break; }
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight);
                    int available = stoneBandEndWorld - stoneBandStartWorld + 1;
                    int finalStoneTopWorld = stoneBandStartWorld - 1;
                    int stoneDepth = 0;
                    if (stoneBandEndWorld >= stoneBandStartWorld && available > 0)
                    {
                        int soilMinReserve = Math.Clamp(soilMinDepthSpec, 0, available);
                        int rawStoneDepth = available - soilMinReserve;
                        stoneDepth = Math.Min(stoneMaxDepthSpec, Math.Max(stoneMinDepthSpec, rawStoneDepth));
                        if (stoneDepth > available) stoneDepth = available;
                        if (stoneDepth > 0) finalStoneTopWorld = stoneBandStartWorld + stoneDepth - 1;
                    }
                    else available = 0;
                    if (possibleStone)
                    {
                        if (available <= 0 || chunkBaseY < stoneBandStartWorld || topOfChunk > finalStoneTopWorld) possibleStone = false;
                    }
                    if (possibleSoil)
                    {
                        int soilStartWorld = finalStoneTopWorld + 1;
                        if (finalStoneTopWorld < stoneBandStartWorld) soilStartWorld = stoneBandStartWorld;
                        if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
                        if (soilStartWorld > soilMaxY){ possibleSoil=false; continue; }
                        int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);
                        if (soilBandCapWorld < soilStartWorld){ possibleSoil=false; continue; }
                        int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                        if (soilAvailable <= 0){ possibleSoil=false; continue; }
                        int soilDepth = Math.Min(soilMaxDepthSpec, soilAvailable);
                        int soilEndWorld = soilStartWorld + soilDepth - 1;
                        if (chunkBaseY < soilStartWorld || topOfChunk > soilEndWorld || chunkBaseY <= finalStoneTopWorld) possibleSoil = false;
                    }
                }
            }
            if (possibleStone)
            {
                AllStoneChunk = true; IsEmpty=false; CreateUniformSections((ushort)BaseBlockType.Stone);
                FaceSolidNegX=FaceSolidPosX=FaceSolidNegY=FaceSolidPosY=FaceSolidNegZ=FaceSolidPosZ=true; return;
            }
            if (possibleSoil)
            {
                AllSoilChunk = true; IsEmpty=false; CreateUniformSections((ushort)BaseBlockType.Soil);
                FaceSolidNegX=FaceSolidPosX=FaceSolidNegY=FaceSolidPosY=FaceSolidNegZ=FaceSolidPosZ=true; return;
            }
        }

        public void GenerateInitialChunkData()
        {
            int maxX = dimX; int maxY = dimY; int maxZ = dimZ;
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);
            int chunkBaseY = (int)position.Y; int topOfChunk = chunkBaseY + maxY - 1;
            const int LocalBurialMargin = 2;
            bool allBuried = true; int maxSurface = int.MinValue;
            for (int x=0; x<maxX && allBuried; x++)
            {
                for (int z=0; z<maxZ; z++)
                {
                    int surface = (int)heightmap[x,z]; if (surface>maxSurface) maxSurface=surface; if (topOfChunk >= surface - LocalBurialMargin){ allBuried=false; break; }
                }
            }
            if (!allBuried)
            {
                for (int x=0; x<maxX; x++)
                for (int z=0; z<maxZ; z++) { int surface=(int)heightmap[x,z]; if (surface>maxSurface) maxSurface=surface; }
            }
            if (allBuried) candidateFullyBuried = true;
            if (chunkBaseY > maxSurface){ AllAirChunk=true; IsEmpty=true; precomputedHeightmap=null; return; }
            DetectAllStoneOrSoil(heightmap, chunkBaseY, topOfChunk);
            if (!AllStoneChunk && !AllSoilChunk)
            {
                int stoneMinY = biome.stoneMinYLevel; int stoneMaxY = biome.stoneMaxYLevel;
                int soilMinY = biome.soilMinYLevel; int soilMaxY = biome.soilMaxYLevel;
                for (int x=0; x<maxX; x++)
                {
                    for (int z=0; z<maxZ; z++)
                    {
                        int columnHeight = (int)heightmap[x,z]; if (columnHeight < chunkBaseY) continue;
                        int stoneBandStartWorld = Math.Max(stoneMinY,0); int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight);
                        int available = stoneBandEndWorld - stoneBandStartWorld + 1; int finalStoneTopWorld = stoneBandStartWorld -1; int stoneDepth=0;
                        if (available > 0)
                        {
                            int soilMinReserve = Math.Clamp(biome.soilMinDepth,0,available); int rawStoneDepth = available - soilMinReserve;
                            stoneDepth = Math.Min(biome.stoneMaxDepth, Math.Max(biome.stoneMinDepth, rawStoneDepth)); if (stoneDepth>available) stoneDepth=available; if (stoneDepth>0) finalStoneTopWorld = stoneBandStartWorld + stoneDepth -1;
                        }
                        int localStoneStart = (stoneBandStartWorld) - chunkBaseY; int localStoneEnd = finalStoneTopWorld - chunkBaseY;
                        if (stoneDepth>0 && localStoneEnd >=0 && localStoneStart < maxY)
                        {
                            if (localStoneStart <0) localStoneStart=0; if (localStoneEnd >= maxY) localStoneEnd = maxY-1;
                            int syStart = localStoneStart >> SECTION_SHIFT; int syEnd = localStoneEnd >> SECTION_SHIFT;
                            int ox = x & SECTION_MASK; int oz = z & SECTION_MASK; int sx = x >> SECTION_SHIFT; int sz = z >> SECTION_SHIFT;
                            for (int sy=syStart; sy<=syEnd; sy++)
                            {
                                var sec = sections[sx,sy,sz]; if (sec==null){ sec=new ChunkSection(); sections[sx,sy,sz]=sec; }
                                int yStartInSection = (sy==syStart)? (localStoneStart & SECTION_MASK):0;
                                int yEndInSection = (sy==syEnd)? (localStoneEnd & SECTION_MASK): (ChunkSection.SECTION_SIZE-1);
                                SectionUtils.AddRun(sec, ox, oz, yStartInSection, yEndInSection, (ushort)BaseBlockType.Stone);
                            }
                        }
                        int soilStartWorld = (stoneDepth>0? (finalStoneTopWorld+1): stoneBandStartWorld); if (soilStartWorld < soilMinY) soilStartWorld = soilMinY; if (soilStartWorld > soilMaxY) continue;
                        int soilBandCapWorld = Math.Min(soilMaxY, columnHeight); if (soilBandCapWorld < soilStartWorld) continue;
                        int soilAvailable = soilBandCapWorld - soilStartWorld +1; if (soilAvailable <=0) continue;
                        int soilDepth = Math.Min(biome.soilMaxDepth, soilAvailable); int soilEndWorld = soilStartWorld + soilDepth -1;
                        int localSoilStart = soilStartWorld - chunkBaseY; int localSoilEnd = soilEndWorld - chunkBaseY;
                        if (soilDepth>0 && localSoilEnd >=0 && localSoilStart < maxY)
                        {
                            if (localSoilStart <0) localSoilStart=0; if (localSoilEnd >= maxY) localSoilEnd = maxY-1;
                            int syStart = localSoilStart >> SECTION_SHIFT; int syEnd = localSoilEnd >> SECTION_SHIFT;
                            int ox = x & SECTION_MASK; int oz = z & SECTION_MASK; int sx = x >> SECTION_SHIFT; int sz = z >> SECTION_SHIFT;
                            for (int sy=syStart; sy<=syEnd; sy++)
                            {
                                var sec = sections[sx,sy,sz]; if (sec==null){ sec=new ChunkSection(); sections[sx,sy,sz]=sec; }
                                int yStartInSection = (sy==syStart)? (localSoilStart & SECTION_MASK):0;
                                int yEndInSection = (sy==syEnd)? (localSoilEnd & SECTION_MASK): (ChunkSection.SECTION_SIZE-1);
                                SectionUtils.AddRun(sec, ox, oz, yStartInSection, yEndInSection, (ushort)BaseBlockType.Soil);
                            }
                        }
                    }
                }
            }
            ApplySimpleReplacementRules(chunkBaseY, topOfChunk);

            // Chunk-level aggregate short-circuit:
            // After applying replacements, if every populated section is a Uniform section with the same non-air id
            // we can mark AllOneBlockChunk and skip per-section finalization (metadata already valid for uniforms
            // except for pending IdMapDirty which we clear).
            if (!AllAirChunk)
            {
                bool allUniformSame = true;
                ushort uniformId = 0;
                for (int sx=0; sx<sectionsX && allUniformSame; sx++)
                {
                    for (int sy=0; sy<sectionsY && allUniformSame; sy++)
                    {
                        for (int sz=0; sz<sectionsZ && allUniformSame; sz++)
                        {
                            var sec = sections[sx,sy,sz]; if (sec==null) continue; // treat null as empty air
                            if (sec.Kind != ChunkSection.RepresentationKind.Uniform || sec.UniformBlockId == ChunkSection.AIR)
                            {
                                allUniformSame = false; break;
                            }
                            if (uniformId == 0) uniformId = sec.UniformBlockId; else if (sec.UniformBlockId != uniformId) { allUniformSame = false; break; }
                        }
                    }
                }
                if (allUniformSame && uniformId != 0)
                {
                    AllOneBlockChunk = true;
                    AllOneBlockBlockId = uniformId;
                    // Clear dirty flags; no need to finalize each section.
                    for (int sx=0; sx<sectionsX; sx++)
                        for (int sy=0; sy<sectionsY; sy++)
                            for (int sz=0; sz<sectionsZ; sz++)
                            {
                                var sec = sections[sx,sy,sz]; if (sec==null) continue;
                                if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
                                {
                                    sec.IdMapDirty = false; sec.StructuralDirty = false; sec.MetadataBuilt = true; // metadata for uniform stays valid
                                }
                            }
                }
            }

            if (!AllOneBlockChunk)
            {
                for (int sx=0; sx<sectionsX; sx++)
                for (int sy=0; sy<sectionsY; sy++)
                for (int sz=0; sz<sectionsZ; sz++)
                { var sec = sections[sx,sy,sz]; if (sec==null) continue; SectionUtils.FinalizeSection(sec); }
            }

            precomputedHeightmap = null;
            BuildAllBoundaryPlanesInitial();

            // Confirm burial only if all six faces ended up solid
            if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
            {
                SetFullyBuried();
            }
        }

        // Cheaply set per-face solidity flags using cached boundary plane bitsets
        private static bool PlaneIsFull(ulong[] plane, int wordCount, ulong fullWord, ulong lastMask)
        {
            if (plane == null || wordCount == 0) return false;
            // ensure plane length is sufficient
            if (plane.Length < wordCount) return false;
            // all full words must be 0xFFFFFFFFFFFFFFFF
            for (int i = 0; i < wordCount - 1; i++)
            {
                if (plane[i] != fullWord) return false;
            }
            // last word uses a partial mask
            if (plane[wordCount - 1] != lastMask) return false;
            return true;
        }
        private void SetFaceSolidFromPlanes()
        {
            // YZ plane (Neg/Pos X): size = dimY * dimZ
            int yzBits = dimY * dimZ;
            int yzWC = (yzBits + 63) >> 6;
            ulong fullWord = ~0UL;
            int remYZ = yzBits & 63;
            ulong lastYZ = remYZ == 0 ? fullWord : ((1UL << remYZ) - 1);
            FaceSolidNegX = PlaneIsFull(PlaneNegX, yzWC, fullWord, lastYZ);
            FaceSolidPosX = PlaneIsFull(PlanePosX, yzWC, fullWord, lastYZ);

            // XZ plane (Neg/Pos Y): size = dimX * dimZ
            int xzBits = dimX * dimZ;
            int xzWC = (xzBits + 63) >> 6;
            int remXZ = xzBits & 63;
            ulong lastXZ = remXZ == 0 ? fullWord : ((1UL << remXZ) - 1);
            FaceSolidNegY = PlaneIsFull(PlaneNegY, xzWC, fullWord, lastXZ);
            FaceSolidPosY = PlaneIsFull(PlanePosY, xzWC, fullWord, lastXZ);

            // XY plane (Neg/Pos Z): size = dimX * dimY
            int xyBits = dimX * dimY;
            int xyWC = (xyBits + 63) >> 6;
            int remXY = xyBits & 63;
            ulong lastXY = remXY == 0 ? fullWord : ((1UL << remXY) - 1);
            FaceSolidNegZ = PlaneIsFull(PlaneNegZ, xyWC, fullWord, lastXY);
            FaceSolidPosZ = PlaneIsFull(PlanePosZ, xyWC, fullWord, lastXY);
        }

        private void BuildAllBoundaryPlanesInitial()
        {
            EnsurePlaneArrays();
            Array.Clear(PlaneNegX);
            Array.Clear(PlanePosX);
            Array.Clear(PlaneNegY);
            Array.Clear(PlanePosY);
            Array.Clear(PlaneNegZ);
            Array.Clear(PlanePosZ);

            const int S = ChunkSection.SECTION_SIZE;

            // Helpers: set one bit in a plane
            static void SetPlaneBit(ulong[] plane, int index)
            {
                if (plane == null) return;
                int w = index >> 6;
                int b = index & 63;
                plane[w] |= 1UL << b;
            }

            // --- -X / +X (YZ planes) ---
            if (sectionsX > 0)
            {
                int sxNeg = 0;
                int sxPos = sectionsX - 1;

                for (int sy = 0; sy < sectionsY; sy++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        // Neg X: section local face at x=0 contributes to global plane x==0
                        var secNeg = sections[sxNeg, sy, sz];
                        if (secNeg != null)
                        {
                            // Uniform sections intentionally leave Face*Bits null but imply fully solid face
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localZ = 0; localZ < S; localZ++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlaneNegX, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegXBits != null)
                            {
                                var bits = secNeg.FaceNegXBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localZ = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlaneNegX, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }

                        }

                        // Pos X: section local face at x=15 contributes to global plane x==dimX-1
                        var secPos = sections[sxPos, sy, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localZ = 0; localZ < S; localZ++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlanePosX, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosXBits != null)
                            {
                                var bits = secPos.FacePosXBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localZ = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalZ = sz * S + localZ;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalZ * dimY + globalY;
                                        SetPlaneBit(PlanePosX, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // --- -Y / +Y (XZ planes) ---
            if (sectionsY > 0)
            {
                int syNeg = 0;
                int syPos = sectionsY - 1;

                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sz = 0; sz < sectionsZ; sz++)
                    {
                        var secNeg = sections[sx, syNeg, sz];
                        if (secNeg != null)
                        {
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlaneNegY, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegYBits != null)
                            {
                                var bits = secNeg.FaceNegYBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localZ = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlaneNegY, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }

                        var secPos = sections[sx, syPos, sz];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localZ = 0; localZ < S; localZ++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlanePosY, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosYBits != null)
                            {
                                var bits = secPos.FacePosYBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localZ = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalZ = sz * S + localZ;
                                        int globalIdx = globalX * dimZ + globalZ;
                                        SetPlaneBit(PlanePosY, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // --- -Z / +Z (XY planes) ---
            if (sectionsZ > 0)
            {
                int szNeg = 0;
                int szPos = sectionsZ - 1;

                for (int sx = 0; sx < sectionsX; sx++)
                {
                    for (int sy = 0; sy < sectionsY; sy++)
                    {
                        var secNeg = sections[sx, sy, szNeg];
                        if (secNeg != null)
                        {
                            if (secNeg.Kind == ChunkSection.RepresentationKind.Uniform && secNeg.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlaneNegZ, globalIdx);
                                    }
                            }
                            else if (secNeg?.FaceNegZBits != null)
                            {
                                var bits = secNeg.FaceNegZBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlaneNegZ, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }

                        var secPos = sections[sx, sy, szPos];
                        if (secPos != null)
                        {
                            if (secPos.Kind == ChunkSection.RepresentationKind.Uniform && secPos.UniformBlockId != ChunkSection.AIR)
                            {
                                for (int localX = 0; localX < S; localX++)
                                    for (int localY = 0; localY < S; localY++)
                                    {
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlanePosZ, globalIdx);
                                    }
                            }
                            else if (secPos?.FacePosZBits != null)
                            {
                                var bits = secPos.FacePosZBits;
                                for (int wi = 0; wi < bits.Length; wi++)
                                {
                                    ulong word = bits[wi];
                                    while (word != 0)
                                    {
                                        int tz = System.Numerics.BitOperations.TrailingZeroCount(word);
                                        int localIndex = wi * 64 + tz;
                                        if (localIndex >= 256) break;
                                        int localX = localIndex / S;
                                        int localY = localIndex % S;
                                        int globalX = sx * S + localX;
                                        int globalY = sy * S + localY;
                                        int globalIdx = globalX * dimY + globalY;
                                        SetPlaneBit(PlanePosZ, globalIdx);
                                        word &= word - 1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Derive per-face solidity flags from planes
            SetFaceSolidFromPlanes();
        }
    }
}
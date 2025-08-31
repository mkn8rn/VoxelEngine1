using System;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Generation.Models;
using MVGE_INF.Managers;
using MVGE_Tools.Noise;
using MVGE_GEN.Utils;
using MVGE_INF.Models.Generation;
using MVGE_INF.Loaders;
using MVGE_GEN.Models;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        private void ApplySimpleReplacementRules(int chunkBaseY, int topOfChunk)
        {
            var rules = biome.simpleReplacements;
            if (rules == null || rules.Count == 0) return;

            int S2 = ChunkSection.SECTION_SIZE;
            for (int sx=0; sx<sectionsX; sx++)
            {
                for (int sy=0; sy<sectionsY; sy++)
                {
                    int sectionY0 = chunkBaseY + sy * S2;
                    int sectionY1 = sectionY0 + S2 - 1;
                    for (int sz=0; sz<sectionsZ; sz++)
                    {
                        var sec = sections[sx,sy,sz]; if (sec==null) continue;
                        // Iterate rules
                        foreach (var r in rules)
                        {
                            if (r.microbiomeId.HasValue) continue; // not handled here
                            if (SectionOutside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel)) continue;
                            bool fullCover = SectionFullyInside(sectionY0, sectionY1, r.absoluteMinYlevel, r.absoluteMaxYlevel);
                            int yMin = r.absoluteMinYlevel.HasValue ? Math.Max(sectionY0, r.absoluteMinYlevel.Value) : sectionY0;
                            int yMax = r.absoluteMaxYlevel.HasValue ? Math.Min(sectionY1, r.absoluteMaxYlevel.Value) : sectionY1;
                            if (yMax < yMin) continue;
                            int lyStart = yMin - sectionY0; int lyEnd = yMax - sectionY0;

                            // Quick prefilter: if the section has no build scratch and is packed/sparse,
                            // check palette/sparse blocks cheaply to avoid converting to scratch unnecessarily.
                            if (sec.BuildScratch == null)
                            {
                                bool maybeTarget = true; // default allow
                                if (sec.Kind == ChunkSection.RepresentationKind.Packed && sec.Palette != null)
                                {
                                    maybeTarget = false;
                                    foreach (var pid in sec.Palette)
                                    {
                                        if (pid == ChunkSection.AIR) continue;
                                        var baseType = GetBaseTypeFast(pid);
                                        if (RuleTargetsBlock(r, pid, baseType))
                                        {
                                            maybeTarget = true;
                                            break;
                                        }
                                    }
                                }
                                else if (sec.Kind == ChunkSection.RepresentationKind.Sparse && sec.SparseBlocks != null)
                                {
                                    maybeTarget = false;
                                    foreach (var pid in sec.SparseBlocks)
                                    {
                                        if (pid == ChunkSection.AIR) continue;
                                        var baseType = GetBaseTypeFast(pid);
                                        if (RuleTargetsBlock(r, pid, baseType))
                                        {
                                            maybeTarget = true;
                                            break;
                                        }
                                    }
                                }
                                if (!maybeTarget) continue; // skip rule for this section — no palette/sparse id targets
                            }

                            // Predicate wrap - now call replacement (unchanged)
                            SectionUtils.ApplyReplacement(
                                sec,
                                lyStart, lyEnd,
                                fullCover,
                                GetBaseTypeFast,
                                (bid, bt) => RuleTargetsBlock(r, bid, bt),
                                r.block_type.ID);
                        }
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
            for (int sx=0; sx<sectionsX; sx++)
            for (int sy=0; sy<sectionsY; sy++)
            for (int sz=0; sz<sectionsZ; sz++)
            { var sec = sections[sx,sy,sz]; if (sec==null) continue; SectionUtils.FinalizeSection(sec); }
            precomputedHeightmap = null; BuildAllBoundaryPlanesInitial();
        }
    }
}
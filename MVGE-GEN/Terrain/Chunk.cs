using MVGE_GEN.Utils;
using MVGE_GFX;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_Tools.Noise;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Buffers;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Generation.Models;

namespace MVGE_GEN.Terrain
{
    public class Chunk
    {
        public Vector3 position { get; set; }
        public ChunkRender chunkRender;
        public ChunkData chunkData;
        public string saveDirectory;
        public long generationSeed;

        public ChunkSection[,,] sections;
        public int sectionsX;
        public int sectionsY;
        public int sectionsZ;

        public float[,] precomputedHeightmap;
        public static readonly Dictionary<long, OpenSimplexNoise> noiseCache = new();

        public const int SECTION_SHIFT = 4;
        public const int SECTION_MASK = 0xF;

        private readonly int dimX;
        private readonly int dimY;
        private readonly int dimZ;
        private const ushort EMPTY = (ushort)BaseBlockType.Empty;

        private Biome biome; // biome used for this chunk

        // Occupancy flags
        public bool IsEmpty { get; private set; }
        public bool HasAnyBoundarySolid { get; private set; }
        // New: heightmap burial classification (chunk wholly below terrain surface minus margin)
        public bool FullyBuried { get; private set; }
        private const int BURIAL_MARGIN = 2; // configurable later via settings/flags

        // per-face full solidity flags (all boundary voxels on that face are non-empty)
        // Naming: NegX = x==0 face ("left"), PosX = x==dimX-1 ("right"), etc.
        public bool FaceSolidNegX { get; private set; }
        public bool FaceSolidPosX { get; private set; }
        public bool FaceSolidNegY { get; private set; }
        public bool FaceSolidPosY { get; private set; }
        public bool FaceSolidNegZ { get; private set; }
        public bool FaceSolidPosZ { get; private set; }

        // neighbor-based burial (all six neighboring opposing faces are solid AND our faces solid)
        public bool BuriedByNeighbors { get; internal set; }

        public Chunk(Vector3 chunkPosition, long seed, string chunkDataDirectory, float[,] precomputedHeightmap = null)
        {
            position = chunkPosition;
            saveDirectory = chunkDataDirectory;
            generationSeed = seed;
            this.precomputedHeightmap = precomputedHeightmap;

            dimX = GameManager.settings.chunkMaxX;
            dimY = GameManager.settings.chunkMaxY;
            dimZ = GameManager.settings.chunkMaxZ;

            chunkData = new ChunkData
            {
                x = position.X,
                y = position.Y,
                z = position.Z,
                temperature = 0,
                humidity = 0
            };

            // Select biome deterministically (fast path when only one loaded)
            biome = BiomeManager.SelectBiomeForChunk(seed, (int)position.X, (int)position.Z);

            InitializeSectionGrid();
            InitializeChunkData();

            // After generation compute per-face solidity once (all writes were generation-only bulk writes)
            ComputeAllFaceSolidity();
        }

        public void InitializeSectionGrid()
        {
            int S = ChunkSection.SECTION_SIZE;
            if (dimX % S != 0 || dimY % S != 0 || dimZ % S != 0)
            {
                throw new InvalidOperationException(
                    "Chunk dimensions must be multiples of section size: " + ChunkSection.SECTION_SIZE);
            }
            sectionsX = dimX / S;
            sectionsY = dimY / S;
            sectionsZ = dimZ / S;
            sections = new ChunkSection[sectionsX, sectionsY, sectionsZ];
        }

        public void Render(ShaderProgram shader) => chunkRender?.Render(shader);
        public void InitializeChunkData() => GenerateInitialChunkData();

        public void GenerateInitialChunkData()
        {
            int maxX = dimX;
            int maxY = dimY;
            int maxZ = dimZ;
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);

            int chunkBaseY = (int)position.Y;
            int topOfChunk = chunkBaseY + maxY - 1;

            // Precompute burial classification BEFORE we null out the heightmap reference.
            // A chunk is FullyBuried if for every (x,z) column the top of the chunk is strictly below
            // (surfaceHeight - BURIAL_MARGIN).
            bool allBuried = true;
            for (int x = 0; x < maxX && allBuried; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int surface = (int)heightmap[x, z];
                    if (topOfChunk >= surface - BURIAL_MARGIN)
                    {
                        allBuried = false;
                        break;
                    }
                }
            }
            FullyBuried = allBuried;

            // Biome absolute bounds
            int stoneMinY = biome.stone_min_ylevel;
            int stoneMaxY = biome.stone_max_ylevel;
            int soilMinY = biome.soil_min_ylevel;
            int soilMaxY = biome.soil_max_ylevel;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int columnHeight = (int)heightmap[x, z]; // inclusive highest world Y occupied by terrain

                    // STONE CALCULATION
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = Math.Min(stoneMaxY, columnHeight); // cannot exceed actual terrain top
                    if (stoneBandEndWorld < stoneBandStartWorld)
                        continue; // nothing to place at all (also implies no soil since terrain lower than bands)

                    int available = stoneBandEndWorld - stoneBandStartWorld + 1; // total vertical cells available for stone+reserved soil
                    if (available <= 0)
                        continue;

                    // Reserve minimum soil depth (clamped to available)
                    int soilMinReserve = Math.Clamp(biome.soil_min_depth, 0, available);

                    // Desired stone depths
                    int stoneMinDepth = biome.stone_min_depth;
                    int stoneMaxDepth = biome.stone_max_depth;

                    // Raw candidate per spec: stoneDepth = min(stoneMaxDepth, max(stoneMinDepth, available - soilMinReserve))
                    int rawStoneDepth = available - soilMinReserve;
                    int stoneDepth = Math.Min(stoneMaxDepth, Math.Max(stoneMinDepth, rawStoneDepth));
                    if (stoneDepth > available) stoneDepth = available; // safety clamp if spec overflows

                    // Recompute actual reserved soil left after assigning stone
                    int remainingForSoil = available - stoneDepth; // may be < soilMinReserve if clamped

                    int finalStoneBottomWorld = stoneBandStartWorld;
                    int finalStoneTopWorld = finalStoneBottomWorld + stoneDepth - 1; // inclusive

                    // Place stone (clamp to chunk vertical span) using bulk column fill
                    int localStoneStart = finalStoneBottomWorld - chunkBaseY;
                    int localStoneEnd = finalStoneTopWorld - chunkBaseY;
                    if (localStoneEnd >= 0 && localStoneStart < maxY)
                    {
                        if (localStoneStart < 0) localStoneStart = 0;
                        if (localStoneEnd >= maxY) localStoneEnd = maxY - 1;
                        if (localStoneStart <= localStoneEnd)
                        {
                            int syStart = localStoneStart >> SECTION_SHIFT;
                            int syEnd = localStoneEnd >> SECTION_SHIFT;
                            int ox = x & SECTION_MASK;
                            int oz = z & SECTION_MASK;
                            int sx = x >> SECTION_SHIFT;
                            int sz = z >> SECTION_SHIFT;
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

                    // SOIL CALCULATION
                    // Soil starts directly above stone; if no stone depth (stoneDepth==0) it starts at stoneBandStartWorld.
                    int soilStartWorld = finalStoneTopWorld + 1;
                    if (stoneDepth == 0) soilStartWorld = stoneBandStartWorld; // no stone placed, soil may start at band start

                    // Enforce absolute biome soil band bounds
                    if (soilStartWorld < soilMinY) soilStartWorld = soilMinY;
                    if (soilStartWorld > soilMaxY) continue; // out of soil band already

                    int soilBandCapWorld = Math.Min(soilMaxY, columnHeight);
                    if (soilBandCapWorld < soilStartWorld) continue; // no vertical space for soil

                    // Remaining vertical space up to cap
                    int soilAvailable = soilBandCapWorld - soilStartWorld + 1;
                    if (soilAvailable <= 0) continue;

                    int soilMaxDepth = biome.soil_max_depth;
                    int soilDepth = Math.Min(soilMaxDepth, soilAvailable);

                    int soilEndWorld = soilStartWorld + soilDepth - 1;

                    // Place soil using bulk column fill
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

            precomputedHeightmap = null; // release reference
        }

        public ushort GenerateInitialBlockData(int lx, int ly, int lz, int columnHeight)
        {
            int currentHeight = (int)(position.Y + ly);
            ushort type = EMPTY;
            if (currentHeight <= columnHeight || currentHeight == 0)
                type = (ushort)BaseBlockType.Stone;
            int soilModifier = 100 - currentHeight / 2;
            if (type == EMPTY && currentHeight < columnHeight + soilModifier)
                type = (ushort)BaseBlockType.Soil;
            return type;
        }

        public float[,] GenerateHeightMap(long seed) => GenerateHeightMap(seed, (int)position.X, (int)position.Z);

        public static float[,] GenerateHeightMap(long seed, int chunkBaseX, int chunkBaseZ)
        {
            if (!noiseCache.TryGetValue(seed, out var noise))
            {
                noise = new OpenSimplexNoise(seed);
                noiseCache[seed] = noise;
            }

            int maxX = GameManager.settings.chunkMaxX;
            int maxZ = GameManager.settings.chunkMaxZ;
            float[,] heightmap = new float[maxX, maxZ];
            float scale = 0.005f;
            float minHeight = 1f;
            float maxHeight = 1000f;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    float noiseValue = (float)noise.Evaluate((x + chunkBaseX) * scale, (z + chunkBaseZ) * scale);
                    float normalizedValue = noiseValue * 0.5f + 0.5f;
                    heightmap[x, z] = normalizedValue * (maxHeight - minHeight) + minHeight;
                }
            }
            return heightmap;
        }

        public ChunkSection GetOrCreateSection(int sx, int sy, int sz)
        {
            var sec = sections[sx, sy, sz];
            if (sec == null)
            {
                sec = new ChunkSection();
                sections[sx, sy, sz] = sec;
            }
            return sec;
        }

        public void LocalToSection(int lx, int ly, int lz,
            out int sx, out int sy, out int sz,
            out int ox, out int oy, out int oz)
        {
            sx = lx >> SECTION_SHIFT;
            sy = ly >> SECTION_SHIFT;
            sz = lz >> SECTION_SHIFT;
            ox = lx & SECTION_MASK;
            oy = ly & SECTION_MASK;
            oz = lz & SECTION_MASK;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetBlockLocal(int lx, int ly, int lz)
        {
            if ((uint)lx >= (uint)dimX || (uint)ly >= (uint)dimY || (uint)lz >= (uint)dimZ)
                return EMPTY;

            int sx = lx >> SECTION_SHIFT;
            int sy = ly >> SECTION_SHIFT;
            int sz = lz >> SECTION_SHIFT;
            var sec = sections[sx, sy, sz];
            if (sec == null) return EMPTY;
            int ox = lx & SECTION_MASK;
            int oy = ly & SECTION_MASK;
            int oz = lz & SECTION_MASK;
            return SectionUtils.GetBlock(sec, ox, oy, oz);
        }

        public void SetBlockLocal(int lx, int ly, int lz, ushort blockId)
        {
            if ((uint)lx >= (uint)dimX || (uint)ly >= (uint)dimY || (uint)lz >= (uint)dimZ)
                return;

            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = GetOrCreateSection(sx, sy, sz);
            SectionUtils.SetBlock(sec, ox, oy, oz, blockId);

            // Update face solidity if we touched a boundary cell (cheap plane scan only for affected faces)
            if (lx == 0) FaceSolidNegX = ScanFaceSolidNegX();
            if (lx == dimX - 1) FaceSolidPosX = ScanFaceSolidPosX();
            if (lz == 0) FaceSolidNegZ = ScanFaceSolidNegZ();
            if (lz == dimZ - 1) FaceSolidPosZ = ScanFaceSolidPosZ();
            if (ly == 0) FaceSolidNegY = ScanFaceSolidNegY();
            if (ly == dimY - 1) FaceSolidPosY = ScanFaceSolidPosY();
        }

        private int FlattenSectionsInto(ushort[] dest)
        {
            int strideX = dimZ * dimY; // (x * dimZ + z) * dimY + y
            int strideZ = dimY;
            int sectionSize = ChunkSection.SECTION_SIZE;

            int nonAirTotal = 0;
            HasAnyBoundarySolid = false;

            for (int sx = 0; sx < sectionsX; sx++)
            {
                int baseX = sx * sectionSize; if (baseX >= dimX) break;
                for (int sz = 0; sz < sectionsZ; sz++)
                {
                    int baseZ = sz * sectionSize; if (baseZ >= dimZ) break;
                    for (int sy = 0; sy < sectionsY; sy++)
                    {
                        int baseY = sy * sectionSize; if (baseY >= dimY) break;
                        var sec = sections[sx, sy, sz];
                        if (sec == null || sec.IsAllAir) continue;

                        int maxLocalX = Math.Min(sectionSize, dimX - baseX);
                        int maxLocalZ = Math.Min(sectionSize, dimZ - baseZ);
                        int maxLocalY = Math.Min(sectionSize, dimY - baseY);

                        // Uniform non-air fast path
                        if (sec.Palette != null &&
                            sec.Palette.Count == 2 &&
                            sec.Palette[0] == ChunkSection.AIR &&
                            sec.NonAirCount == sec.VoxelCount &&
                            sec.VoxelCount != 0)
                        {
                            ushort solid = sec.Palette[1];
                            int voxels = maxLocalX * maxLocalZ * maxLocalY;
                            nonAirTotal += voxels;
                            // Boundary contact check
                            if (!HasAnyBoundarySolid && (baseX == 0 || baseY == 0 || baseZ == 0 || baseX + maxLocalX == dimX || baseY + maxLocalY == dimY || baseZ + maxLocalZ == dimZ))
                                HasAnyBoundarySolid = true;
                            for (int lx = 0; lx < maxLocalX; lx++)
                            {
                                int gx = baseX + lx; int destXBase = gx * strideX;
                                for (int lz = 0; lz < maxLocalZ; lz++)
                                {
                                    int gz = baseZ + lz; int destZBase = destXBase + gz * strideZ + baseY;
                                    dest.AsSpan(destZBase, maxLocalY).Fill(solid);
                                }
                            }
                            continue;
                        }

                        // General path
                        int sectionPlane = sectionSize * sectionSize; // 256
                        int bitsPer = sec.BitsPerIndex;
                        uint[] bitData = sec.BitData;
                        var palette = sec.Palette;
                        if (bitsPer == 0 || bitData == null || palette == null) continue;

                        int mask = (1 << bitsPer) - 1;

                        for (int lx = 0; lx < maxLocalX; lx++)
                        {
                            int gx = baseX + lx; int destXBase = gx * strideX;
                            bool boundaryX = gx == 0 || gx == dimX - 1;
                            for (int lz = 0; lz < maxLocalZ; lz++)
                            {
                                int gz = baseZ + lz; int destZBase = destXBase + gz * strideZ;
                                bool boundaryXZ = boundaryX || gz == 0 || gz == dimZ - 1;
                                int baseXZ = lz * sectionSize + lx; // add ly*256 inside y loop
                                for (int ly = 0; ly < maxLocalY; ly++)
                                {
                                    int gy = baseY + ly;
                                    int linear = (ly * sectionPlane) + baseXZ; // (y*256)+(z*16)+x
                                    long bitPos = (long)linear * bitsPer;
                                    int dataIndex = (int)(bitPos >> 5);
                                    int bitOffset = (int)(bitPos & 31);
                                    uint value = bitData[dataIndex] >> bitOffset;
                                    int remaining = 32 - bitOffset;
                                    if (remaining < bitsPer)
                                    {
                                        value |= bitData[dataIndex + 1] << remaining;
                                    }
                                    int paletteIndex = (int)(value & (uint)mask);
                                    ushort id = palette[paletteIndex];
                                    dest[destZBase + gy] = id;
                                    if (id != ChunkSection.AIR)
                                    {
                                        nonAirTotal++;
                                        if (!HasAnyBoundarySolid && (boundaryXZ || gy == 0 || gy == dimY - 1))
                                            HasAnyBoundarySolid = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            IsEmpty = nonAirTotal == 0;
            return nonAirTotal;
        }

        public void BuildRender(Func<int, int, int, ushort> worldBlockGetter)
        {
            chunkRender?.ScheduleDelete();

            // Early skip retained (heightmap burial) – kept separate from new neighbor-face occlusion system.
            if (FullyBuried || BuriedByNeighbors)
            {
                return; // leave chunkRender null
            }

            int voxelCount = dimX * dimY * dimZ;
            ushort[] flat = ArrayPool<ushort>.Shared.Rent(voxelCount);
            for (int i = 0; i < flat.Length; i++) flat[i] = EMPTY; // initialize (air)
            int nonAir = FlattenSectionsInto(flat);

            if (nonAir == 0)
            {
                ArrayPool<ushort>.Shared.Return(flat, false);
                return; // chunkRender stays null; Render() will no-op
            }

            chunkRender = new ChunkRender(
                chunkData,
                worldBlockGetter,
                flat,
                dimX,
                dimY,
                dimZ);
        }

        // Per-face solidity helpers
        private void ComputeAllFaceSolidity()
        {
            FaceSolidNegX = ScanFaceSolidNegX();
            FaceSolidPosX = ScanFaceSolidPosX();
            FaceSolidNegY = ScanFaceSolidNegY();
            FaceSolidPosY = ScanFaceSolidPosY();
            FaceSolidNegZ = ScanFaceSolidNegZ();
            FaceSolidPosZ = ScanFaceSolidPosZ();
        }

        private bool ScanFaceSolidNegX()
        {
            for (int y = 0; y < dimY; y++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(0, y, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidPosX()
        {
            int x = dimX - 1;
            for (int y = 0; y < dimY; y++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(x, y, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidNegY()
        {
            for (int x = 0; x < dimX; x++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(x, 0, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidPosY()
        {
            int y = dimY - 1;
            for (int x = 0; x < dimX; x++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(x, y, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidNegZ()
        {
            for (int x = 0; x < dimX; x++)
                for (int y = 0; y < dimY; y++)
                    if (GetBlockLocal(x, y, 0) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidPosZ()
        {
            int z = dimZ - 1;
            for (int x = 0; x < dimX; x++)
                for (int y = 0; y < dimY; y++)
                    if (GetBlockLocal(x, y, z) == EMPTY) return false;
            return true;
        }
    }
}

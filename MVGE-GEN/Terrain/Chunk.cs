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
            int chunkTopY = chunkBaseY + maxY - 1;

            // Clamp biome Y levels into this chunk's vertical span for calculation convenience
            int stoneMinY = biome.stone_min_ylevel;
            int stoneMaxY = biome.stone_max_ylevel;
            int soilMinY = biome.soil_min_ylevel;
            int soilMaxY = biome.soil_max_ylevel;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int columnHeight = (int)heightmap[x, z];

                    // Determine stone band actual world y range for this column
                    int stoneBandStartWorld = Math.Max(stoneMinY, 0);
                    int stoneBandEndWorld = stoneMaxY; // inclusive upper cap from biome definition
                    if (stoneBandEndWorld < stoneBandStartWorld) continue; // invalid config

                    int finalStoneTopWorld = stoneBandStartWorld - 1; // initialize below start so we know if set
                    int finalStoneBottomWorld = stoneBandStartWorld; // default

                    // The top of natural terrain is columnHeight; stone cannot exceed that.
                    int stoneTopWorld = Math.Min(columnHeight, stoneBandEndWorld);
                    int stoneBottomWorld = stoneBandStartWorld;

                    // Apply depth constraints (min/max depth counts thickness of stone layer)
                    int stoneDesiredMinDepth = biome.stone_min_depth;
                    int stoneDesiredMaxDepth = biome.stone_max_depth;

                    int availableStoneDepth = stoneTopWorld - stoneBottomWorld + 1;
                    if (availableStoneDepth > 0)
                    {
                        int stoneDepth = Math.Max(stoneDesiredMinDepth, Math.Min(stoneDesiredMaxDepth, availableStoneDepth));
                        finalStoneTopWorld = stoneTopWorld;
                        finalStoneBottomWorld = finalStoneTopWorld - stoneDepth + 1;
                        if (finalStoneBottomWorld < stoneBottomWorld)
                            finalStoneBottomWorld = stoneBottomWorld;
                        stoneDepth = finalStoneTopWorld - finalStoneBottomWorld + 1;

                    // Convert to local chunk Y and write stone blocks
                        int localStoneStart = finalStoneBottomWorld - chunkBaseY;
                        int localStoneEnd = finalStoneTopWorld - chunkBaseY;

                        if (localStoneEnd >= 0 && localStoneStart < maxY)
                        {
                            if (localStoneStart < 0) localStoneStart = 0;
                            if (localStoneEnd >= maxY) localStoneEnd = maxY - 1;
                            if (localStoneStart <= localStoneEnd)
                            {
                            // Ensure sections exist
                                int sx = x >> SECTION_SHIFT;
                                int sz = z >> SECTION_SHIFT;
                                for (int ly = localStoneStart; ly <= localStoneEnd; ly++)
                                {
                                    int sy = ly >> SECTION_SHIFT;
                                    if (sections[sx, sy, sz] == null)
                                        sections[sx, sy, sz] = new ChunkSection();
                                    SetBlockLocal(x, ly, z, (ushort)BaseBlockType.Stone);
                                }
                            }
                        }
                    }

                    // Soil layer: absolute y limits but depth counts only soil placed
                    int soilBandStartWorld = Math.Max(soilMinY, 0);
                    int soilBandEndWorld = soilMaxY;
                    if (soilBandEndWorld < soilBandStartWorld) continue;

                    int soilDesiredMinDepth = biome.soil_min_depth;
                    int soilDesiredMaxDepth = biome.soil_max_depth;

                    int soilPlacementTopWorld = Math.Min(columnHeight, soilBandEndWorld);
                    if (soilPlacementTopWorld < soilBandStartWorld) continue; // no soil possible

                    int soilCursorWorld = finalStoneTopWorld + 1; // first potential soil block right above stone (or just above startWorld-1 if no stone)
                    if (soilCursorWorld < soilBandStartWorld) soilCursorWorld = soilBandStartWorld;

                    int soilPlaced = 0;
                    while (soilCursorWorld <= soilPlacementTopWorld && soilPlaced < soilDesiredMaxDepth)
                    {
                        int localY = soilCursorWorld - chunkBaseY;
                        if (localY >= 0 && localY < maxY)
                        {
                            int sx = x >> SECTION_SHIFT;
                            int sz = z >> SECTION_SHIFT;
                            int sy = localY >> SECTION_SHIFT;
                            if (sections[sx, sy, sz] == null)
                                sections[sx, sy, sz] = new ChunkSection();
                            SetBlockLocal(x, localY, z, (ushort)BaseBlockType.Soil);
                        }
                        soilPlaced++;
                        soilCursorWorld++;
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
        }

        private void FlattenSectionsInto(ushort[] dest)
        {
            int strideX = dimZ * dimY; // (x * dimZ + z) * dimY + y
            int strideZ = dimY;
            int sectionSize = ChunkSection.SECTION_SIZE;

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

                        // Uniform non-air fast path: palette [AIR, singleSolid] and fully filled.
                        if (sec.Palette != null &&
                            sec.Palette.Count == 2 &&
                            sec.Palette[0] == ChunkSection.AIR &&
                            sec.NonAirCount == sec.VoxelCount &&
                            sec.VoxelCount != 0)
                        {
                            ushort solid = sec.Palette[1];
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

                        // General path: decode on the fly from BitData.
                        int sectionPlane = sectionSize * sectionSize; // 256
                        int bitsPer = sec.BitsPerIndex;
                        uint[] bitData = sec.BitData;
                        var palette = sec.Palette;
                        if (bitsPer == 0 || bitData == null || palette == null) continue;

                        int mask = (1 << bitsPer) - 1;

                        for (int lx = 0; lx < maxLocalX; lx++)
                        {
                            int gx = baseX + lx; int destXBase = gx * strideX;
                            for (int lz = 0; lz < maxLocalZ; lz++)
                            {
                                int gz = baseZ + lz; int destZBase = destXBase + gz * strideZ;
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
                                    dest[destZBase + gy] = palette[paletteIndex];
                                }
                            }
                        }
                    }
                }
            }
        }

        public void BuildRender(Func<int, int, int, ushort> worldBlockGetter)
        {
            chunkRender?.ScheduleDelete();

            int voxelCount = dimX * dimY * dimZ;
            ushort[] flat = ArrayPool<ushort>.Shared.Rent(voxelCount);
            for (int i = 0; i < flat.Length; i++) flat[i] = EMPTY; // initialize (air)
            FlattenSectionsInto(flat);

            chunkRender = new ChunkRender(
                chunkData,
                worldBlockGetter,
                flat,
                dimX,
                dimY,
                dimZ);
        }
    }
}

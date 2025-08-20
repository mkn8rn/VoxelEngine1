using MVGE_GEN.Utils;
using MVGE_GFX;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using MVGE_Tools.Noise;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GEN.Terrain
{
    // moved TryGetBlockFastDelegate to MVGE_INF.Models.Terrain for shared use
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

        // Heightmap only needed during initial generation; release after use
        public float[,] precomputedHeightmap;

        public static readonly Dictionary<long, OpenSimplexNoise> noiseCache = new();

        public const int SECTION_SHIFT = 4;
        public const int SECTION_MASK = 0xF;

        public Chunk(Vector3 chunkPosition, long seed, string chunkDataDirectory, float[,] precomputedHeightmap = null)
        {
            position = chunkPosition;
            saveDirectory = chunkDataDirectory;
            generationSeed = seed;
            this.precomputedHeightmap = precomputedHeightmap;

            chunkData = new ChunkData
            {
                x = position.X,
                y = position.Y,
                z = position.Z,
                temperature = 0,
                humidity = 0
            };

            InitializeSectionGrid();
            InitializeChunkData();
        }

        public void InitializeSectionGrid()
        {
            int S = ChunkSection.SECTION_SIZE;
            if (GameManager.settings.chunkMaxX % S != 0 ||
                GameManager.settings.chunkMaxY % S != 0 ||
                GameManager.settings.chunkMaxZ % S != 0)
            {
                throw new InvalidOperationException("Chunk dimensions must be multiples of section size: " + ChunkSection.SECTION_SIZE.ToString());
            }
            sectionsX = GameManager.settings.chunkMaxX / S;
            sectionsY = GameManager.settings.chunkMaxY / S;
            sectionsZ = GameManager.settings.chunkMaxZ / S;
            sections = new ChunkSection[sectionsX, sectionsY, sectionsZ];
        }

        public void Render(ShaderProgram shader) => chunkRender?.Render(shader);

        public void InitializeChunkData() => GenerateInitialChunkData();

        public void GenerateInitialChunkData()
        {
            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;
            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);

            int chunkBaseY = (int)position.Y;
            int chunkTopY = chunkBaseY + maxY - 1;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    if (columnHeight < chunkBaseY)
                    {
                        int soilCapExclusive = (int)Math.Floor(2.0 / 3.0 * (columnHeight + 100));
                        if (soilCapExclusive <= chunkBaseY) continue;
                    }

                    int stoneWorldTop = Math.Min(columnHeight, chunkTopY);
                    int stoneLocalTop = stoneWorldTop - chunkBaseY;
                    bool hasStone = columnHeight >= chunkBaseY && chunkBaseY <= stoneWorldTop;

                    int soilCapExclusiveWorldY = (int)Math.Floor(2.0 / 3.0 * (columnHeight + 100));
                    int soilLocalStart = columnHeight + 1 - chunkBaseY;
                    int soilLocalEnd = soilCapExclusiveWorldY - 1 - chunkBaseY;
                    bool hasSoil = soilLocalStart <= soilLocalEnd && soilLocalStart < maxY && soilLocalEnd >= 0;
                    if (hasSoil)
                    {
                        if (soilLocalStart < 0) soilLocalStart = 0;
                        if (soilLocalEnd >= maxY) soilLocalEnd = maxY - 1;
                    }

                    int maxNonAirLocalY = -1;
                    if (hasSoil) maxNonAirLocalY = Math.Max(maxNonAirLocalY, soilLocalEnd);
                    if (hasStone) maxNonAirLocalY = Math.Max(maxNonAirLocalY, stoneLocalTop);
                    if (maxNonAirLocalY < 0) continue;

                    int sx = x >> SECTION_SHIFT;
                    int sz = z >> SECTION_SHIFT;
                    for (int sy = 0; sy <= maxNonAirLocalY >> SECTION_SHIFT; sy++)
                    {
                        if (sections[sx, sy, sz] == null)
                        {
                            sections[sx, sy, sz] = new ChunkSection();
                        }
                    }

                    if (hasStone)
                    {
                        for (int ly = 0; ly <= stoneLocalTop && ly < maxY; ly++)
                        {
                            SetBlockLocal(x, ly, z, (ushort)BaseBlockType.Stone);
                        }
                    }

                    if (hasSoil)
                    {
                        for (int ly = soilLocalStart; ly <= soilLocalEnd; ly++)
                        {
                            if (ly < 0 || ly >= maxY) continue;
                            SetBlockLocal(x, ly, z, (ushort)BaseBlockType.Soil);
                        }
                    }
                }
            }

            // release heightmap reference
            precomputedHeightmap = null;
        }

        public ushort GenerateInitialBlockData(int lx, int ly, int lz, int columnHeight)
        {
            int currentHeight = (int)(position.Y + ly);
            ushort type = (ushort)BaseBlockType.Empty;
            if (currentHeight <= columnHeight || currentHeight == 0) type = (ushort)BaseBlockType.Stone;
            int soilModifier = 100 - currentHeight / 2;
            if (type == (ushort)BaseBlockType.Empty && currentHeight < columnHeight + soilModifier) type = (ushort)BaseBlockType.Soil;
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
            float maxHeight = 200f;
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

        public void LocalToSection(int lx, int ly, int lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz)
        {
            sx = lx >> SECTION_SHIFT; sy = ly >> SECTION_SHIFT; sz = lz >> SECTION_SHIFT;
            ox = lx & SECTION_MASK; oy = ly & SECTION_MASK; oz = lz & SECTION_MASK;
        }

        public ushort GetBlockLocal(int lx, int ly, int lz)
        {
            if (lx < 0 || ly < 0 || lz < 0 || lx >= GameManager.settings.chunkMaxX || ly >= GameManager.settings.chunkMaxY || lz >= GameManager.settings.chunkMaxZ)
                return (ushort)BaseBlockType.Empty;
            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = sections[sx, sy, sz];
            return SectionUtils.GetBlock(sec, ox, oy, oz);
        }

        public void SetBlockLocal(int lx, int ly, int lz, ushort blockId)
        {
            if (lx < 0 || ly < 0 || lz < 0 || lx >= GameManager.settings.chunkMaxX || ly >= GameManager.settings.chunkMaxY || lz >= GameManager.settings.chunkMaxZ)
                return;
            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = GetOrCreateSection(sx, sy, sz);
            SectionUtils.SetBlock(sec, ox, oy, oz, blockId);
        }

        public void BuildRender(Func<int, int, int, ushort> worldBlockGetter, GetBlockFastDelegate fastGetter)
        {
            chunkRender?.ScheduleDelete();
            chunkRender = new ChunkRender(
                chunkData,
                worldBlockGetter,
                fastGetter,
                (lx, ly, lz) => GetBlockLocal(lx, ly, lz));
        }
    }
}

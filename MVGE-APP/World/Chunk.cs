using MVGE_GFX;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using MVGE_Tools.Noise;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

namespace World
{
    internal class Chunk
    {
        public Vector3 position { get; set; }

        private ChunkRender chunkRender;
        private ChunkData chunkData;
        private string saveDirectory;
        private long generationSeed;

        private ChunkSection[,,] sections;
        private int sectionsX;
        private int sectionsY;
        private int sectionsZ;

        // Optional precomputed heightmap (shared across vertical stack)
        private readonly float[,] precomputedHeightmap;

        // Static noise cache per seed to avoid re-instantiation cost
        private static readonly Dictionary<long, OpenSimplexNoise> noiseCache = new();

        private const int SECTION_SHIFT = 4;
        private const int SECTION_MASK = 0xF;

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

        private void InitializeSectionGrid()
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
            int S = ChunkSection.SECTION_SIZE;

            float[,] heightmap = precomputedHeightmap ?? GenerateHeightMap(generationSeed);

            int chunkBaseY = (int)position.Y;
            int chunkTopY = chunkBaseY + maxY - 1;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    int columnHeight = (int)heightmap[x, z]; // absolute world height

                    // If entire chunk is above soil/stone cap, skip
                    if (columnHeight < chunkBaseY)
                    {
                        // Potentially soil could extend slightly above columnHeight; compute soil cap to be sure
                        int soilCapExclusive = (int)Math.Floor((2.0 / 3.0) * (columnHeight + 100));
                        if (soilCapExclusive <= chunkBaseY) continue; // nothing in this vertical slice
                    }

                    int stoneWorldTop = Math.Min(columnHeight, chunkTopY);
                    int stoneLocalTop = stoneWorldTop - chunkBaseY; // inclusive

                    bool hasStone = columnHeight >= chunkBaseY && chunkBaseY <= stoneWorldTop;

                    // Soil region derived from inequality currentHeight < (2/3)*(columnHeight + 100)
                    int soilCapExclusiveWorldY = (int)Math.Floor((2.0 / 3.0) * (columnHeight + 100));
                    int soilLocalStart = (columnHeight + 1) - chunkBaseY; // first layer above stone
                    int soilLocalEnd = soilCapExclusiveWorldY - 1 - chunkBaseY; // inclusive

                    bool hasSoil = soilLocalStart <= soilLocalEnd && soilLocalStart < maxY && soilLocalEnd >= 0;
                    if (hasSoil)
                    {
                        if (soilLocalStart < 0) soilLocalStart = 0;
                        if (soilLocalEnd >= maxY) soilLocalEnd = maxY - 1;
                    }

                    // Determine highest local y with a non-air block
                    int maxNonAirLocalY = -1;
                    if (hasSoil) maxNonAirLocalY = Math.Max(maxNonAirLocalY, soilLocalEnd);
                    if (hasStone) maxNonAirLocalY = Math.Max(maxNonAirLocalY, stoneLocalTop);
                    if (maxNonAirLocalY < 0) continue; // nothing to place

                    // Pre-create needed sections for this (x,z) column
                    int sx = x >> SECTION_SHIFT;
                    int sz = z >> SECTION_SHIFT;
                    for (int sy = 0; sy <= (maxNonAirLocalY >> SECTION_SHIFT); sy++)
                    {
                        if (sections[sx, sy, sz] == null)
                        {
                            sections[sx, sy, sz] = new ChunkSection();
                        }
                    }

                    // Place stone
                    if (hasStone)
                    {
                        for (int ly = 0; ly <= stoneLocalTop && ly < maxY; ly++)
                        {
                            SetBlockLocal(x, ly, z, (ushort)BaseBlockType.Stone);
                        }
                    }

                    // Place soil
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
        }

        public ushort GenerateInitialBlockData(int lx, int ly, int lz, int columnHeight)
        {
            int currentHeight = (int)(position.Y + ly);
            ushort type = (ushort)BaseBlockType.Empty;

            if (currentHeight <= columnHeight || currentHeight == 0)
            {
                type = (ushort)BaseBlockType.Stone;
            }

            int soilModifier = 100 - currentHeight / 2;
            if (type == (ushort)BaseBlockType.Empty &&
                currentHeight < columnHeight + soilModifier)
            {
                type = (ushort)BaseBlockType.Soil;
            }
            return type;
        }

        // Instance helper defers to static cached version
        public float[,] GenerateHeightMap(long seed)
            => GenerateHeightMap(seed, (int)position.X, (int)position.Z);

        // Static version used so world can reuse heightmaps across vertical chunk stacks
        internal static float[,] GenerateHeightMap(long seed, int chunkBaseX, int chunkBaseZ)
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
                    float normalizedValue = (noiseValue * 0.5f) + 0.5f; // (n+1)/2
                    heightmap[x, z] = normalizedValue * (maxHeight - minHeight) + minHeight;
                }
            }
            return heightmap;
        }

        private ChunkSection GetOrCreateSection(int sx, int sy, int sz)
        {
            var sec = sections[sx, sy, sz];
            if (sec == null)
            {
                sec = new ChunkSection();
                sections[sx, sy, sz] = sec;
            }
            return sec;
        }

        private void LocalToSection(int lx, int ly, int lz,
            out int sx, out int sy, out int sz,
            out int ox, out int oy, out int oz)
        {
            sx = lx >> SECTION_SHIFT; sy = ly >> SECTION_SHIFT; sz = lz >> SECTION_SHIFT;
            ox = lx & SECTION_MASK; oy = ly & SECTION_MASK; oz = lz & SECTION_MASK;
        }

        internal ushort GetBlockLocal(int lx, int ly, int lz)
        {
            if (lx < 0 || ly < 0 || lz < 0 ||
                lx >= GameManager.settings.chunkMaxX ||
                ly >= GameManager.settings.chunkMaxY ||
                lz >= GameManager.settings.chunkMaxZ)
                return (ushort)BaseBlockType.Empty;

            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = sections[sx, sy, sz];
            return SectionRender.GetBlock(sec, ox, oy, oz);
        }

        internal void SetBlockLocal(int lx, int ly, int lz, ushort blockId)
        {
            if (lx < 0 || ly < 0 || lz < 0 ||
                lx >= GameManager.settings.chunkMaxX ||
                ly >= GameManager.settings.chunkMaxY ||
                lz >= GameManager.settings.chunkMaxZ)
                return;

            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = GetOrCreateSection(sx, sy, sz);
            SectionRender.SetBlock(sec, ox, oy, oz, blockId);
        }

        internal void BuildRender(Func<int, int, int, ushort> worldBlockGetter)
        {
            chunkRender?.ScheduleDelete();
            chunkRender = new ChunkRender(
                chunkData,
                worldBlockGetter,
                (lx, ly, lz) => GetBlockLocal(lx, ly, lz));
        }
    }
}

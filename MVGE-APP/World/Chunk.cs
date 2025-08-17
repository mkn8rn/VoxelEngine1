using MVGE_GFX;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using MVGE_Tools.Noise;
using OpenTK.Mathematics;
using System;

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

        public Chunk(Vector3 chunkPosition, long seed, string chunkDataDirectory)
        {
            position = chunkPosition;
            saveDirectory = chunkDataDirectory;
            generationSeed = seed;

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
            if (TerrainDataManager.CHUNK_MAX_X % S != 0 ||
                TerrainDataManager.CHUNK_MAX_Y % S != 0 ||
                TerrainDataManager.CHUNK_MAX_Z % S != 0)
            {
                throw new InvalidOperationException("Chunk dimensions must be multiples of section size: " + ChunkSection.SECTION_SIZE.ToString());
            }
            sectionsX = TerrainDataManager.CHUNK_MAX_X / S;
            sectionsY = TerrainDataManager.CHUNK_MAX_Y / S;
            sectionsZ = TerrainDataManager.CHUNK_MAX_Z / S;
            sections = new ChunkSection[sectionsX, sectionsY, sectionsZ];
        }

        public void Render(ShaderProgram shader) => chunkRender?.Render(shader);

        public void InitializeChunkData() => GenerateInitialChunkData();

        public void GenerateInitialChunkData()
        {
            float[,] heightmap = GenerateHeightMap(generationSeed);
            for (int x = 0; x < TerrainDataManager.CHUNK_MAX_X; x++)
            {
                for (int z = 0; z < TerrainDataManager.CHUNK_MAX_Z; z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    for (int y = 0; y < TerrainDataManager.CHUNK_MAX_Y; y++)
                    {
                        ushort blockId = GenerateInitialBlockData(x, y, z, columnHeight);
                        if (blockId != (ushort)BaseBlockType.Empty)
                        {
                            SetBlockLocal(x, y, z, blockId);
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

        public float[,] GenerateHeightMap(long seed)
        {
            float[,] heightmap = new float[TerrainDataManager.CHUNK_MAX_X, TerrainDataManager.CHUNK_MAX_Z];
            var noise = new OpenSimplexNoise(seed);
            float scale = 0.005f;
            float minHeight = 1f;
            float maxHeight = 200f;

            for (int x = 0; x < TerrainDataManager.CHUNK_MAX_X; x++)
            {
                for (int z = 0; z < TerrainDataManager.CHUNK_MAX_Z; z++)
                {
                    float noiseValue = (float)noise.Evaluate((x + position.X) * scale, (z + position.Z) * scale);
                    float normalizedValue = (noiseValue + 1f) * 0.5f;
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
            int S = ChunkSection.SECTION_SIZE;
            sx = lx / S; sy = ly / S; sz = lz / S;
            ox = lx % S; oy = ly % S; oz = lz % S;
        }

        internal ushort GetBlockLocal(int lx, int ly, int lz)
        {
            if (lx < 0 || ly < 0 || lz < 0 ||
                lx >= TerrainDataManager.CHUNK_MAX_X ||
                ly >= TerrainDataManager.CHUNK_MAX_Y ||
                lz >= TerrainDataManager.CHUNK_MAX_Z)
                return (ushort)BaseBlockType.Empty;

            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = sections[sx, sy, sz];
            return SectionRender.GetBlock(sec, ox, oy, oz);
        }

        internal void SetBlockLocal(int lx, int ly, int lz, ushort blockId)
        {
            if (lx < 0 || ly < 0 || lz < 0 ||
                lx >= TerrainDataManager.CHUNK_MAX_X ||
                ly >= TerrainDataManager.CHUNK_MAX_Y ||
                lz >= TerrainDataManager.CHUNK_MAX_Z)
                return;

            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = GetOrCreateSection(sx, sy, sz);
            SectionRender.SetBlock(sec, ox, oy, oz, blockId);
        }

        internal void BuildRender(Func<int, int, int, ushort> worldBlockGetter)
        {
            chunkRender?.Delete();
            chunkRender = new ChunkRender(
                chunkData,
                worldBlockGetter,
                (lx, ly, lz) => GetBlockLocal(lx, ly, lz));
        }
    }
}

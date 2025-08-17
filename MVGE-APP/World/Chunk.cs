using MVGE_GFX;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using MVGE_Tools.Noise;
using Newtonsoft.Json;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Threading.Tasks;

namespace World
{
    internal class Chunk
    {
        public Vector3 position { get; set; }
        private ChunkRender chunkRender;
        private ChunkData chunkData;
        private string saveDirectory;
        private long generationSeed;

        public Chunk(Vector3 chunkPosition, long seed, string chunkDataDirectory)
        {
            position = chunkPosition;
            saveDirectory = chunkDataDirectory;
            generationSeed = seed;

            chunkData = new ChunkData();

            InitializeChunkData();
        }

        public void Render(ShaderProgram shader)
        {
            chunkRender?.Render(shader);
        }

        public void InitializeChunkData()
        {
            string path = Path.Combine(saveDirectory, $"Chunk_{position.X}_{position.Y}_{position.Z}.txt");
            if (!File.Exists(path))
            {
                GenerateInitialChunkData();
            }
            else
            {
                LoadChunkData();
            }
        }

        public void GenerateInitialChunkData()
        {
            chunkData.x = position.X;
            chunkData.y = position.Y;
            chunkData.z = position.Z;
            chunkData.blocks = new ushort[TerrainDataManager.CHUNK_MAX_X, TerrainDataManager.CHUNK_MAX_Y, TerrainDataManager.CHUNK_MAX_Z];
            chunkData.temperature = 0;
            chunkData.humidity = 0;

            float[,] heightmap = GenerateHeightMap(generationSeed);

            for (int x = 0; x < TerrainDataManager.CHUNK_MAX_X; x++)
            {
                for (int z = 0; z < TerrainDataManager.CHUNK_MAX_Z; z++)
                {
                    int columnHeight = (int)heightmap[x, z];
                    for (int y = 0; y < TerrainDataManager.CHUNK_MAX_Y; y++)
                    {
                        ushort blockData = GenerateInitialBlockData(x, y, z, columnHeight);
                        chunkData.blocks[x, y, z] = blockData;
                    }
                }
            }

            SaveChunkData(chunkData);
        }

        public ushort GenerateInitialBlockData(int x, int y, int z, int columnHeight)
        {
            int currentHeight = (int)(position.Y + y);
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

        public void SaveChunkData(ChunkData chunkData)
        {
            string json = JsonConvert.SerializeObject(chunkData);
            string fileName = $"Chunk_{position.X}_{position.Y}_{position.Z}.txt";
            string filePath = Path.Combine(saveDirectory, fileName);

            using FileStream fileStream = new FileStream(filePath, FileMode.Create);
            using GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Compress);
            using StreamWriter writer = new StreamWriter(gzipStream);
            writer.Write(json);
        }

        public void LoadChunkData()
        {
            string fileName = $"Chunk_{position.X}_{position.Y}_{position.Z}.txt";
            string filePath = Path.Combine(saveDirectory, fileName);

            using FileStream fileStream = new FileStream(filePath, FileMode.Open);
            using GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using StreamReader reader = new StreamReader(gzipStream);
            string json = reader.ReadToEnd();
            chunkData = JsonConvert.DeserializeObject<ChunkData>(json);
            // Defer mesh build.
        }

        public float[,] GenerateHeightMap(long seed)
        {
            float[,] heightmap = new float[TerrainDataManager.CHUNK_MAX_X, TerrainDataManager.CHUNK_MAX_Z];
            OpenSimplexNoise noise = new OpenSimplexNoise(seed);
            float scale = 0.005f;

            float minHeight = 1f;
            float maxHeight = 200f;

            for (int x = 0; x < TerrainDataManager.CHUNK_MAX_X; x++)
            {
                for (int z = 0; z < TerrainDataManager.CHUNK_MAX_Z; z++)
                {
                    float noiseValue = (float)noise.Evaluate((x + position.X) * scale, (z + position.Z) * scale);
                    float normalizedValue = (noiseValue + 1) / 2;
                    heightmap[x, z] = normalizedValue * (maxHeight - minHeight) + minHeight;
                }
            }

            return heightmap;
        }

        internal void BuildRender(Func<int, int, int, ushort> worldBlockGetter)
        {
            chunkRender?.Delete();
            chunkRender = new ChunkRender(chunkData, worldBlockGetter);
        }

        internal ushort GetBlockLocal(int lx, int ly, int lz)
        {
            if (chunkData.blocks == null) return (ushort)BaseBlockType.Empty;
            if (lx < 0 || ly < 0 || lz < 0 ||
                lx >= TerrainDataManager.CHUNK_MAX_X ||
                ly >= TerrainDataManager.CHUNK_MAX_Y ||
                lz >= TerrainDataManager.CHUNK_MAX_Z)
            {
                return (ushort)BaseBlockType.Empty;
            }
            return chunkData.blocks[lx, ly, lz];
        }
    }
}

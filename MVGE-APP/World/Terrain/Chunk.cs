using MVGE.Tools;
using MVGE_GFX;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_INF.Models.Terrain;
using Newtonsoft.Json;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE.World.Terrain
{
    internal class Chunk
    {
        public Vector3 position { get; set; }
        private MeshRender chunkRender;
        private ChunkData chunkData;
        private string saveDirectory;
        private Int64 generationSeed;

        public Chunk(Vector3 chunkPosition, Int64 seed, string chunkDataDirectory)
        {
            this.position = chunkPosition;
            this.saveDirectory = chunkDataDirectory;
            this.generationSeed = seed;

            chunkData = new ChunkData();

            InitializeChunkData();
        }

        public void Render(ShaderProgram shader)
        {
            chunkRender.Render(shader);
        }

        public void InitializeChunkData()
        {
            if (!File.Exists(Path.Combine(saveDirectory, $"Chunk_{position.X}_{position.Y}_{position.Z}.txt")))
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
            chunkData.blocks = new ushort[TerrainDataLoader.CHUNK_SIZE, TerrainDataLoader.CHUNK_MAX_HEIGHT, TerrainDataLoader.CHUNK_SIZE];
            chunkData.temperature = 0;
            chunkData.humidity = 0;

            float[,] heightmap = GenerateHeightMap(this.generationSeed, TerrainDataLoader.CHUNK_SIZE);

            for (int x = 0; x < TerrainDataLoader.CHUNK_SIZE; x++)
            {
                for (int z = 0; z < TerrainDataLoader.CHUNK_SIZE; z++)
                {
                    int columnHeight = (int)(heightmap[x, z]);
                    for (int y = 0; y < TerrainDataLoader.CHUNK_MAX_HEIGHT; y++)
                    {
                        ushort blockData = GenerateInitialBlockData(x, y, z, columnHeight);
                        chunkData.blocks[x, y, z] = blockData;
                    }
                }
            }

            SaveChunkData(chunkData);
            chunkRender = new MeshRender(chunkData);
        }

        public ushort GenerateInitialBlockData(int x, int y, int z, int columnHeight)
        {
            ushort blockData;
            int currentHeight = (int)(position.Y + y);

            ushort type = (ushort)BaseBlockType.Empty;

            if (currentHeight <= columnHeight ||
               currentHeight == 0)
            {
                type = (ushort)BaseBlockType.Stone;
            }

            int soilModifier = 100 - (currentHeight / 2);

            if (type == (ushort)BaseBlockType.Empty &&
                currentHeight < columnHeight + soilModifier)
            {
                type = (ushort)BaseBlockType.Soil;
            }

            blockData = type;

            return blockData;
        }

        public void SaveChunkData(MVGE_INF.Models.Terrain.ChunkData chunkData)
        {
            string json = JsonConvert.SerializeObject(chunkData);
            string fileName = $"Chunk_{position.X}_{position.Y}_{position.Z}.txt";
            string filePath = Path.Combine(saveDirectory, fileName);

            using (FileStream fileStream = new FileStream(filePath, FileMode.Create))
            using (GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
            using (StreamWriter writer = new StreamWriter(gzipStream))
            {
                writer.Write(json);
            }
        }

        public void LoadChunkData()
        {
            string fileName = $"Chunk_{position.X}_{position.Y}_{position.Z}.txt";
            string filePath = Path.Combine(saveDirectory, fileName);

            using (FileStream fileStream = new FileStream(filePath, FileMode.Open))
            using (GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            using (StreamReader reader = new StreamReader(gzipStream))
            {
                string json = reader.ReadToEnd();
                chunkData = JsonConvert.DeserializeObject<MVGE_INF.Models.Terrain.ChunkData>(json);
            }

            chunkRender = new MeshRender(chunkData);
        }

        public float[,] GenerateHeightMap(Int64 seed, int chunkSize)
        {
            float[,] heightmap = new float[chunkSize, chunkSize];
            OpenSimplexNoise noise = new OpenSimplexNoise(seed);
            float scale = 0.005f;

            float minHeight = 1f;
            float maxHeight = 200f;

            for (int x = 0; x < chunkSize; x++)
            {
                for (int z = 0; z < chunkSize; z++)
                {
                    float noiseValue = (float)noise.Evaluate((x + position.X) * scale, (z + position.Z) * scale);

                    // Normalize the noise value to [0, 1]
                    float normalizedValue = (noiseValue + 1) / 2;

                    // Scale and shift the normalized value to the desired range [minHeight, maxHeight]
                    heightmap[x, z] = normalizedValue * (maxHeight - minHeight) + minHeight;
                }
            }

            return heightmap;
        }

        public void PrintHeightMap(float[,] heightmap)
        {
            float minHeight = float.MaxValue;
            float maxHeight = float.MinValue;

            // Find the smallest and largest height values
            for (int x = 0; x < TerrainDataLoader.CHUNK_SIZE; x++)
            {
                for (int z = 0; z < TerrainDataLoader.CHUNK_SIZE; z++)
                {
                    float height = heightmap[x, z];
                    if (height < minHeight)
                    {
                        minHeight = height;
                    }
                    if (height > maxHeight)
                    {
                        maxHeight = height;
                    }
                }
            }

            Bitmap bmp = new Bitmap(TerrainDataLoader.CHUNK_SIZE, TerrainDataLoader.CHUNK_SIZE);

            // Draw the heightmap with varying shades of red based on the height values
            for (int x = 0; x < TerrainDataLoader.CHUNK_SIZE; x++)
            {
                for (int z = 0; z < TerrainDataLoader.CHUNK_SIZE; z++)
                {
                    float height = heightmap[x, z];
                    int red = (int)((height - minHeight) / (maxHeight - minHeight) * 255);
                    bmp.SetPixel(x, z, Color.FromArgb(red, 0, 0));
                }
            }

            string fileName = $"Heightmap_{position.X}_{position.Z}.png";
            string filePath = Path.Combine(saveDirectory, fileName);
            bmp.Save(filePath);
        }
    }
}

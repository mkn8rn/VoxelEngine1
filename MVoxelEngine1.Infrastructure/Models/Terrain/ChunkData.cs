using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Terrain
{
    public struct ChunkData
    {
        public float x;
        public float y;
        public float z;
        public byte temperature;
        public byte humidity;
        // public ushort[,,] blocks;
        // new BlockData[TerrainDataLoader.CHUNK_SIZE, TerrainDataLoader.CHUNK_MAX_HEIGHT, TerrainDataLoader.CHUNK_SIZE]
    }
}

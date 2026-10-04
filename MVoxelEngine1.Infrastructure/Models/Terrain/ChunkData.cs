using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVoxelEngine1.Infrastructure.Models.Terrain
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public record struct ChunkData
    {
        public float x { readonly get; set; }
        public float y { readonly get; set; }
        public float z { readonly get; set; }
        public byte temperature { readonly get; set; }
        public byte humidity { readonly get; set; }
        // public ushort[,,] blocks;
        // new BlockData[TerrainDataLoader.CHUNK_SIZE, TerrainDataLoader.CHUNK_MAX_HEIGHT, TerrainDataLoader.CHUNK_SIZE]
    }
}

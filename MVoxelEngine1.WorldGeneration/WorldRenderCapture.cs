using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.WorldGeneration.Terrain;

namespace MVoxelEngine1.WorldGeneration
{
    public sealed class WorldRenderChunk
    {
        private readonly Chunk chunk;

        internal WorldRenderChunk(
            int chunkX,
            int chunkY,
            int chunkZ,
            Chunk chunk,
            ChunkRenderUploadData? uploadData,
            bool isOpenGlUploaded)
        {
            ChunkX = chunkX;
            ChunkY = chunkY;
            ChunkZ = chunkZ;
            this.chunk = chunk;
            UploadData = uploadData;
            IsOpenGlUploaded = isOpenGlUploaded;
        }

        public int ChunkX { get; }

        public int ChunkY { get; }

        public int ChunkZ { get; }

        public float WorldOriginX => chunk.position.X;

        public float WorldOriginY => chunk.position.Y;

        public float WorldOriginZ => chunk.position.Z;

        public ChunkRenderUploadData? UploadData { get; }

        public bool IsOpenGlUploaded { get; }

        public ushort GetBlockLocal(int x, int y, int z) => chunk.GetBlockLocal(x, y, z);
    }

    public partial class World
    {
        public IReadOnlyList<WorldRenderChunk> CaptureActiveRenderChunks()
        {
            ThrowIfReferenceMeshBuildFailed();
            using IDisposable stateScope = AcquireRenderStateReadScope();
            var chunks = new List<WorldRenderChunk>(activeChunks.Count);
            foreach (var pair in activeChunks)
            {
                (int cx, int cy, int cz) key = pair.Key;
                if (faceGenerationMode == FaceGenerationMode.Reference &&
                    dirtyChunks.ContainsKey(key))
                {
                    continue;
                }

                Chunk chunk = pair.Value;
                ChunkRender? renderer = chunk.chunkRender;
                chunks.Add(new WorldRenderChunk(
                    key.cx,
                    key.cy,
                    key.cz,
                    chunk,
                    renderer?.UploadData,
                    renderer?.IsOpenGlUploaded ?? false));
            }

            return chunks;
        }
    }
}

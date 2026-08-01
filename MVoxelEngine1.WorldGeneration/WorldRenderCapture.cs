using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Managers;
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

        public IReadOnlyList<WorldRenderChunk> CaptureRequiredRenderChunks()
        {
            ThrowIfReferenceMeshBuildFailed();
            using IDisposable stateScope = AcquireRenderStateReadScope();
            int radius = GameManager.settings.lod1RenderDistance;
            long regionLimit = GameManager.settings.regionWidthInChunks;
            (int centerX, int centerY, int centerZ) = PlayerChunkPosition;
            int minimumX = Math.Max(centerX - radius, (int)-regionLimit);
            int maximumX = Math.Min(centerX + radius, (int)regionLimit);
            int minimumY = Math.Max(centerY - radius, (int)-regionLimit);
            int maximumY = Math.Min(centerY + radius, (int)regionLimit);
            int minimumZ = Math.Max(centerZ - radius, (int)-regionLimit);
            int maximumZ = Math.Min(centerZ + radius, (int)regionLimit);
            int capacity = checked(
                (maximumX - minimumX + 1) *
                (maximumY - minimumY + 1) *
                (maximumZ - minimumZ + 1));
            var chunks = new List<WorldRenderChunk>(capacity);

            for (int chunkX = minimumX; chunkX <= maximumX; chunkX++)
            {
                for (int chunkY = minimumY; chunkY <= maximumY; chunkY++)
                {
                    for (int chunkZ = minimumZ; chunkZ <= maximumZ; chunkZ++)
                    {
                        var key = (chunkX, chunkY, chunkZ);
                        if (dirtyChunks.ContainsKey(key))
                        {
                            throw new InvalidOperationException(
                                $"Required render chunk {key} is dirty.");
                        }

                        if (!activeChunks.TryGetValue(key, out Chunk? chunk))
                        {
                            throw new InvalidOperationException(
                                $"Required render chunk {key} is not active.");
                        }

                        ChunkRender? renderer = chunk.chunkRender;
                        chunks.Add(new WorldRenderChunk(
                            chunkX,
                            chunkY,
                            chunkZ,
                            chunk,
                            renderer?.UploadData,
                            renderer?.IsOpenGlUploaded ?? false));
                    }
                }
            }

            return chunks;
        }
    }
}

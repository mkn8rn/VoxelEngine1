using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Application.Simulation
{
    internal static class HeadlessGtrtBenchmarkRunner
    {
        public static void Run(string outputPath)
        {
            GameDataStartup.Load();

            Console.WriteLine("Texture atlases initializing.");
            var textureAtlas = new BlockTextureAtlas(
                BlockTextureAtlasUploadMode.SimulatedGpuUpload);
            ChunkRender.terrainTextureAtlas = textureAtlas;

            using var world = new World(textureAtlas);
            Console.WriteLine("Initializing player.");
            var player = new Player(world);
            world.PlayerChunkPosition = (0, 0, 0);
            SimulatedGpuUploadBoundarySnapshot uploadBoundary =
                AcceptFirstSimulatedUpload(world);

            double generationToRender =
                StartupPerformanceRecorder.RecordGenerationToRender() ??
                throw new InvalidOperationException(
                    "The headless GTRT endpoint was already recorded.");
            Console.WriteLine(FormattableString.Invariant(
                $"Generation to Render time (GTRT): {generationToRender:R} ms."));
            GC.KeepAlive(player);

            StartupPerformanceRecorder.WriteHeadlessGtrtSnapshot(
                outputPath,
                uploadBoundary);
            Console.WriteLine(
                $"Headless GTRT metrics written to " +
                $"{Path.GetFullPath(outputPath)}");
        }

        private static SimulatedGpuUploadBoundarySnapshot
            AcceptFirstSimulatedUpload(World world)
        {
            using IDisposable renderStateScope =
                world.AcquireRenderStateReadScope();
            IReadOnlyList<WorldRenderChunk> chunks =
                world.CaptureActiveRenderChunks();
            foreach (WorldRenderChunk chunk in chunks)
            {
                ChunkRenderUploadData? upload = chunk.UploadData;
                if (upload is null ||
                    upload.OpaqueFaceCount + upload.TransparentFaceCount == 0)
                {
                    continue;
                }
                if (chunk.IsOpenGlUploaded)
                {
                    throw new InvalidOperationException(
                        "Headless render data was uploaded through OpenGL.");
                }

                int opaqueWordCount = upload.ReadOpaque(
                    static view => view.AsSpan().Length,
                    static rectangles => rectangles.Length);
                int transparentWordCount = upload.ReadTransparent(
                    static view => view.AsSpan().Length,
                    static rectangles => rectangles.Length);
                if (opaqueWordCount != upload.OpaqueWordCount ||
                    transparentWordCount != upload.TransparentWordCount)
                {
                    throw new InvalidDataException(
                        "The simulated upload word counts are not valid.");
                }

                return new SimulatedGpuUploadBoundarySnapshot
                {
                    RenderDataId = upload.RenderDataId,
                    ChunkX = chunk.ChunkX,
                    ChunkY = chunk.ChunkY,
                    ChunkZ = chunk.ChunkZ,
                    OpaqueFaceCount = upload.OpaqueFaceCount,
                    OpaqueRectangleCount = upload.OpaqueRectangleCount,
                    OpaqueWordCount = opaqueWordCount,
                    TransparentFaceCount = upload.TransparentFaceCount,
                    TransparentRectangleCount =
                        upload.TransparentRectangleCount,
                    TransparentWordCount = transparentWordCount
                };
            }

            throw new InvalidOperationException(
                "No render data reached the simulated GPU upload boundary.");
        }
    }
}

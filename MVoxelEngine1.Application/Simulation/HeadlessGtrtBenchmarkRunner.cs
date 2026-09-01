using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.WorldGeneration.Native;

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

            var loader = new WorldLoader();
            loader.ChooseWorld(
                FlagManager.flags.worldName,
                FlagManager.flags.seed);

            using NativeGtrtPipeline pipeline =
                NativeGtrtPipeline.Create(textureAtlas);
            NativePreUploadPacket nativePacket = pipeline.Run(loader.seed);

            double generationToRender =
                pipeline.GenerationToRenderMilliseconds ??
                throw new InvalidOperationException(
                    "The headless GTRT endpoint was not recorded.");
            Console.WriteLine(FormattableString.Invariant(
                $"Generation to Render time (GTRT): {generationToRender:R} ms."));
            Console.WriteLine(
                $"[World] Initial generation complete in " +
                $"{pipeline.InitialGenerationMilliseconds} ms.");
            Console.WriteLine(
                $"[World] Chunk mesh build complete in " +
                $"{pipeline.InitialMeshMilliseconds} ms.");

            var uploadBoundary = new SimulatedGpuUploadBoundarySnapshot
            {
                RenderDataId = nativePacket.RenderDataId,
                ChunkX = nativePacket.ChunkX,
                ChunkY = nativePacket.ChunkY,
                ChunkZ = nativePacket.ChunkZ,
                OpaqueFaceCount = nativePacket.OpaqueFaceCount,
                OpaqueRectangleCount =
                    nativePacket.OpaqueRectangleCount,
                OpaqueWordCount = nativePacket.OpaqueWordCount,
                TransparentFaceCount =
                    nativePacket.TransparentFaceCount,
                TransparentRectangleCount =
                    nativePacket.TransparentRectangleCount,
                TransparentWordCount =
                    nativePacket.TransparentWordCount
            };

            StartupPerformanceRecorder.WriteHeadlessGtrtSnapshot(
                outputPath,
                uploadBoundary);
            Console.WriteLine(
                $"Headless GTRT metrics written to " +
                $"{Path.GetFullPath(outputPath)}");
        }

    }
}

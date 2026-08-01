using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Application.Simulation
{
    internal static class FaceManifestRunner
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static void Run(
            string outputPath,
            string? inputScript,
            IReadOnlyList<TimedPlayerInputStep> steps,
            int frameRate)
        {
            GameDataStartup.Load();

            Console.WriteLine("Texture atlases initializing.");
            var textureAtlas = new BlockTextureAtlas(
                BlockTextureAtlasUploadMode.SimulatedGpuUpload);
            ChunkRender.terrainTextureAtlas = textureAtlas;

            using var world = new World();
            var player = new Player(world);
            if (steps.Count != 0)
            {
                Console.WriteLine($"Applying timed face manifest input: {inputScript}");
                TimedPlayerMovementResult movement = TimedPlayerMovementRunner.Run(
                    player,
                    steps,
                    frameRate);
                Console.WriteLine(
                    $"Timed face manifest input completed after " +
                    $"{movement.SimulationElapsedSeconds:F6} simulated seconds.");
            }

            WorldFaceManifest manifest = CaptureWhenReady(world);
            WriteAtomic(outputPath, manifest);
            Console.WriteLine(
                $"Canonical face manifest written to {Path.GetFullPath(outputPath)}");
        }

        private static WorldFaceManifest CaptureWhenReady(World world)
        {
            TimeSpan timeout = TimeSpan.FromSeconds(60);
            var clock = Stopwatch.StartNew();
            WorldRenderStateNotReadyException? lastFailure = null;
            while (clock.Elapsed < timeout)
            {
                try
                {
                    return WorldFaceManifestBuilder.Capture(
                        world,
                        FlagManager.flags.game!,
                        FlagManager.flags.seed!.Value,
                        FlagManager.flags.faceGenerationMode!.Value);
                }
                catch (WorldRenderStateNotReadyException ex)
                {
                    lastFailure = ex;
                    Thread.Sleep(10);
                }
            }

            throw new TimeoutException(
                "The required render chunks did not become stable within 60 seconds.",
                lastFailure);
        }

        private static void WriteAtomic(
            string outputPath,
            WorldFaceManifest manifest)
        {
            string finalPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("The face manifest output directory is invalid.");

            Directory.CreateDirectory(directory);
            if (File.Exists(finalPath))
                throw new IOException($"The face manifest output already exists: {finalPath}");

            string temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.incomplete");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    JsonSerializer.Serialize(stream, manifest, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, finalPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}

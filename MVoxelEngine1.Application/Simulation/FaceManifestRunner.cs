using MVoxelEngine1.Infrastructure.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.WorldGeneration.Native;

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

            Console.WriteLine(EngineMessages.InitializingTextureAtlases);
            var textureAtlas = new BlockTextureAtlas(
                BlockTextureAtlasUploadMode.SimulatedGpuUpload);
            ChunkRender.terrainTextureAtlas = textureAtlas;

            FaceGenerationMode requestedMode =
                FlagManager.flags.faceGenerationMode!.Value;
            if (requestedMode == FaceGenerationMode.Reference)
                FlagManager.flags.faceGenerationMode = FaceGenerationMode.Optimized;

            try
            {
                using var world = NativeWorld.CreateHeadless(textureAtlas);
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

                WorldFaceManifest manifest = CaptureWhenReady(world, requestedMode);
                WriteAtomic(outputPath, manifest);
                Console.WriteLine(
                    $"Canonical face manifest written to {Path.GetFullPath(outputPath)}");
            }
            finally
            {
                FlagManager.flags.faceGenerationMode = requestedMode;
            }
        }

        private static WorldFaceManifest CaptureWhenReady(
            NativeWorld world,
            FaceGenerationMode requestedMode)
        {
            return WorldFaceManifestBuilder.Capture(
                world, FlagManager.flags.game!, FlagManager.flags.seed!.Value, requestedMode);
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

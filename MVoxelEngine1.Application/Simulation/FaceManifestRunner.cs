using System.Text.Json;
using System.Text.Json.Serialization;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Managers;
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

        public static void Run(string outputPath)
        {
            GameDataStartup.Load();

            Console.WriteLine("Texture atlases initializing.");
            var textureAtlas = new BlockTextureAtlas(
                BlockTextureAtlasUploadMode.SimulatedGpuUpload);
            ChunkRender.terrainTextureAtlas = textureAtlas;

            using var world = new World();
            WorldFaceManifest manifest = WorldFaceManifestBuilder.Capture(
                world,
                FlagManager.flags.game!,
                FlagManager.flags.seed!.Value,
                FlagManager.flags.faceGenerationMode!.Value);
            WriteAtomic(outputPath, manifest);
            Console.WriteLine(
                $"Canonical face manifest written to {Path.GetFullPath(outputPath)}");
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

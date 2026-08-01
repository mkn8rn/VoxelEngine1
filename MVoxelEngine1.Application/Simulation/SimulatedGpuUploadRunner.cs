using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Application.Simulation
{
    internal static class SimulatedGpuUploadRunner
    {
        public static async Task RunAsync(
            string outputPath,
            string inputScript,
            IReadOnlyList<TimedPlayerInputStep> steps,
            int frameRate,
            int writerDelayMilliseconds,
            int? writerFailAfterRecords)
        {
            GameDataStartup.Load();

            Console.WriteLine("Texture atlases initializing.");
            var textureAtlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
            ChunkRender.terrainTextureAtlas = textureAtlas;

            using var world = new World();
            Console.WriteLine("Initializing player.");
            var player = new Player(world);
            world.PlayerChunkPosition = (0, 0, 0);
            int windowWidth = FlagManager.flags.windowWidth
                ?? throw new InvalidOperationException("The simulated window width is not set.");
            int windowHeight = FlagManager.flags.windowHeight
                ?? throw new InvalidOperationException("The simulated window height is not set.");

            await using var output = new SimulatedGpuUploadStream(
                outputPath,
                inputScript,
                frameRate,
                textureAtlas,
                world,
                player,
                windowWidth,
                windowHeight,
                writerDelayMilliseconds,
                writerFailAfterRecords);

            Console.WriteLine("Simulated GPU upload mode started without an OpenTK window.");
            long frameIndex = 0;
            double simulationElapsedSeconds = 0;
            SimulatedRenderFrameState frame = output.RenderFrame(
                frameIndex,
                simulationElapsedSeconds,
                wallElapsedSeconds: 0,
                deltaSeconds: 0,
                PlayerInputKeys.None);
            output.WriteSnapshot("initial", simulationElapsedSeconds, frame);

            TimedPlayerMovementResult movement = TimedPlayerMovementRunner.Run(
                player,
                steps,
                frameRate,
                boundary => output.WriteInputBoundary(
                    boundary.Started ? "inputStarted" : "inputEnded",
                    boundary.StepIndex,
                    boundary.Step,
                    boundary.SimulationElapsedSeconds),
                current => frame = output.RenderFrame(
                    current.FrameIndex,
                    current.SimulationElapsedSeconds,
                    current.WallElapsedSeconds,
                    current.DeltaSeconds,
                    current.Keys));
            frameIndex = movement.FrameIndex;
            simulationElapsedSeconds = movement.SimulationElapsedSeconds;

            output.WriteSnapshot("final", simulationElapsedSeconds, frame);
            await output.CompleteAsync(
                simulationElapsedSeconds,
                movement.WallElapsedSeconds);
            Console.WriteLine($"Simulated GPU upload data written to {Path.GetFullPath(outputPath)}");
        }
    }
}

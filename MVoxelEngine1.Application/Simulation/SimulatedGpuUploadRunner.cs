using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.WorldGeneration.Native;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Application.Simulation
{
    internal static class SimulatedGpuUploadRunner
    {
        public static void Run(
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

            using var world = CreateNativeWorld(textureAtlas);
            Console.WriteLine("Face generation mode: Optimized.");
            if (FlagManager.flags.faceGenerationMode == FaceGenerationMode.Reference)
                Console.WriteLine("Reference face validation enabled.");
            Console.WriteLine("Initializing player.");
            var player = new Player(world);
            world.PlayerChunkPosition = (0, 0, 0);
            int windowWidth = FlagManager.flags.windowWidth
                ?? throw new InvalidOperationException("The simulated window width is not set.");
            int windowHeight = FlagManager.flags.windowHeight
                ?? throw new InvalidOperationException("The simulated window height is not set.");

            var output = new SimulatedGpuUploadStream(
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

            try
            {

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
            output.CompleteAsync(
                simulationElapsedSeconds,
                movement.WallElapsedSeconds).GetAwaiter().GetResult();
            Console.WriteLine($"Simulated GPU upload data written to {Path.GetFullPath(outputPath)}");
            }
            finally
            {
                // The native world stays on its owner thread while the artifact
                // writer drains independently and never accesses engine state.
                output.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        private static NativeWorld CreateNativeWorld(BlockTextureAtlas textureAtlas)
        {
            FaceGenerationMode? requested = FlagManager.flags.faceGenerationMode;
            FlagManager.flags.faceGenerationMode = FaceGenerationMode.Optimized;
            try
            {
                return NativeWorld.CreateHeadless(textureAtlas);
            }
            finally
            {
                FlagManager.flags.faceGenerationMode = requested;
            }
        }
    }
}

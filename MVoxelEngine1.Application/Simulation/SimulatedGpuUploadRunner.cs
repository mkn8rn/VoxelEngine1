using MVoxelEngine1.Infrastructure.Resources;
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

            Console.WriteLine(EngineMessages.InitializingTextureAtlases);
            var textureAtlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
            ChunkRender.terrainTextureAtlas = textureAtlas;

            using var world = CreateNativeWorld(textureAtlas);
            Console.WriteLine(EngineMessages.OptimizedFaceMode);
            if (FlagManager.flags.faceGenerationMode == FaceGenerationMode.Reference)
                Console.WriteLine(EngineMessages.ReferenceValidationEnabled);
            Console.WriteLine(EngineMessages.InitializingPlayer);
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
            RunMovementAndCompleteOutput(outputPath, steps, frameRate, player, output);
            }
            finally
            {
                // The native world stays on its owner thread while the artifact
                // writer drains independently and never accesses engine state.
#pragma warning disable VSTHRD002 // Keep the NativeWorld owner thread while its independent writer drains; that writer captures no synchronization context and never accesses world state.
                output.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
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

        private static void RunMovementAndCompleteOutput(string outputPath, global::System.Collections.Generic.IReadOnlyList<global::MVoxelEngine1.Infrastructure.Models.Simulation.TimedPlayerInputStep> steps, int frameRate, global::MVoxelEngine1.Application.Gameplay.Player player, global::MVoxelEngine1.Application.Simulation.SimulatedGpuUploadStream output)
        {
            Console.WriteLine(EngineMessages.SimulatedUploadStarted);
            long frameIndex = 0;
            double simulationElapsedSeconds = 0;
            SimulatedRenderFrameState frame = output.RenderFrame(frameIndex, simulationElapsedSeconds, wallElapsedSeconds: 0, deltaSeconds: 0, PlayerInputKeys.None);
            output.WriteSnapshot("initial", simulationElapsedSeconds, frame);
            TimedPlayerMovementResult movement = TimedPlayerMovementRunner.Run(player, steps, frameRate, boundary => output.WriteInputBoundary(boundary.Started ? "inputStarted" : "inputEnded", boundary.StepIndex, boundary.Step, boundary.SimulationElapsedSeconds), current => frame = output.RenderFrame(current.FrameIndex, current.SimulationElapsedSeconds, current.WallElapsedSeconds, current.DeltaSeconds, current.Keys));
            frameIndex = movement.FrameIndex;
            simulationElapsedSeconds = movement.SimulationElapsedSeconds;
            output.WriteSnapshot("final", simulationElapsedSeconds, frame);
            output.CompleteAsync(simulationElapsedSeconds,
        #pragma warning disable VSTHRD002 // Keep the NativeWorld owner thread while its independent writer drains; that writer captures no synchronization context and never accesses world state.
            movement.WallElapsedSeconds).GetAwaiter().GetResult();
        #pragma warning restore VSTHRD002
            Console.WriteLine($"Simulated GPU upload data written to {Path.GetFullPath(outputPath)}");
        }
    }
}

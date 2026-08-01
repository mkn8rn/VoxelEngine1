using System.Diagnostics;
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
            int frameRate)
        {
            GameDataStartup.Load();

            Console.WriteLine("Texture atlases initializing.");
            var textureAtlas = new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
            ChunkRender.terrainTextureAtlas = textureAtlas;

            using var world = new World();
            Console.WriteLine("Initializing player.");
            var player = new Player(world);
            world.PlayerChunkPosition = (0, 0, 0);
            GameDataStartup.PrepareCamera();

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
                windowHeight);

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

            var movementClock = Stopwatch.StartNew();
            double frameIntervalSeconds = 1.0 / frameRate;
            for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
            {
                TimedPlayerInputStep step = steps[stepIndex];
                output.WriteInputBoundary(
                    "inputStarted",
                    stepIndex,
                    step,
                    simulationElapsedSeconds);

                var stepClock = Stopwatch.StartNew();
                double appliedSeconds = 0;
                double scheduledSeconds = 0;
                double stepStartSimulationSeconds = simulationElapsedSeconds;
                while (appliedSeconds < step.DurationSeconds)
                {
                    scheduledSeconds = Math.Min(
                        scheduledSeconds + frameIntervalSeconds,
                        step.DurationSeconds);
                    WaitUntil(stepClock, scheduledSeconds);

                    double elapsedSeconds = Math.Min(
                        stepClock.Elapsed.TotalSeconds,
                        step.DurationSeconds);
                    double deltaSeconds = elapsedSeconds - appliedSeconds;
                    if (deltaSeconds <= 0)
                        continue;

                    player.Update(step.Keys, deltaSeconds);
                    appliedSeconds = elapsedSeconds;
                    simulationElapsedSeconds = stepStartSimulationSeconds + appliedSeconds;
                    frameIndex++;
                    frame = output.RenderFrame(
                        frameIndex,
                        simulationElapsedSeconds,
                        movementClock.Elapsed.TotalSeconds,
                        deltaSeconds,
                        step.Keys);
                }

                simulationElapsedSeconds = stepStartSimulationSeconds + step.DurationSeconds;
                output.WriteInputBoundary(
                    "inputEnded",
                    stepIndex,
                    step,
                    simulationElapsedSeconds);
            }

            output.WriteSnapshot("final", simulationElapsedSeconds, frame);
            await output.CompleteAsync(simulationElapsedSeconds, movementClock.Elapsed.TotalSeconds);
            Console.WriteLine($"Simulated GPU upload data written to {Path.GetFullPath(outputPath)}");
        }

        private static void WaitUntil(Stopwatch clock, double targetSeconds)
        {
            while (true)
            {
                double remainingSeconds = targetSeconds - clock.Elapsed.TotalSeconds;
                if (remainingSeconds <= 0)
                    return;

                if (remainingSeconds > 0.004)
                    Thread.Sleep(TimeSpan.FromSeconds(remainingSeconds - 0.002));
                else
                    Thread.SpinWait(64);
            }
        }
    }
}

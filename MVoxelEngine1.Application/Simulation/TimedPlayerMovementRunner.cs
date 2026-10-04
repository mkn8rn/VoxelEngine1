using System.Diagnostics;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Application.Simulation
{
    internal static class TimedPlayerMovementRunner
    {
        public static TimedPlayerMovementResult Run(Player player, IReadOnlyList<TimedPlayerInputStep> steps, int frameRate, Action<TimedPlayerInputBoundary>? inputBoundary = null, Action<TimedPlayerMovementFrame>? frameUpdated = null)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(steps);
            if (frameRate <= 0 || frameRate > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(frameRate), "The simulated frame rate must be from 1 through 1000.");
            }

            long frameIndex = 0;
            double simulationElapsedSeconds = 0;
            double frameIntervalSeconds = 1.0 / frameRate;
            var movementClock = Stopwatch.StartNew();
            for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
            {
                TimedPlayerInputStep step = steps[stepIndex];
                inputBoundary?.Invoke(new TimedPlayerInputBoundary(true, stepIndex, step, simulationElapsedSeconds));
                var stepClock = Stopwatch.StartNew();
                double appliedSeconds = 0;
                long stepFrameIndex = 0;
                double stepStartSimulationSeconds = simulationElapsedSeconds;
                while (appliedSeconds < step.DurationSeconds)
                {
                    double scheduledSeconds = Math.Min(++stepFrameIndex * frameIntervalSeconds, step.DurationSeconds);
                    WaitUntil(stepClock, scheduledSeconds);
                    // Every scheduled slice is paced by elapsed wall time. A slow
                    // streaming frame drains the backlog through the same Player
                    // updates instead of skipping intervening chunk positions.
                    double deltaSeconds = scheduledSeconds - appliedSeconds;
                    if (deltaSeconds <= 0)
                        continue;
                    player.Update(step.Keys, deltaSeconds);
                    appliedSeconds = scheduledSeconds;
                    simulationElapsedSeconds = stepStartSimulationSeconds + appliedSeconds;
                    frameIndex++;
                    frameUpdated?.Invoke(new TimedPlayerMovementFrame(frameIndex, simulationElapsedSeconds, movementClock.Elapsed.TotalSeconds, deltaSeconds, step.Keys));
                }

                simulationElapsedSeconds = stepStartSimulationSeconds + step.DurationSeconds;
                inputBoundary?.Invoke(new TimedPlayerInputBoundary(false, stepIndex, step, simulationElapsedSeconds));
            }

            return new TimedPlayerMovementResult(frameIndex, simulationElapsedSeconds, movementClock.Elapsed.TotalSeconds);
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

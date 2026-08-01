using System.Diagnostics;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Application.Simulation
{
    internal readonly record struct TimedPlayerInputBoundary(
        bool Started,
        int StepIndex,
        TimedPlayerInputStep Step,
        double SimulationElapsedSeconds);

    internal readonly record struct TimedPlayerMovementFrame(
        long FrameIndex,
        double SimulationElapsedSeconds,
        double WallElapsedSeconds,
        double DeltaSeconds,
        PlayerInputKeys Keys);

    internal readonly record struct TimedPlayerMovementResult(
        long FrameIndex,
        double SimulationElapsedSeconds,
        double WallElapsedSeconds);

    internal static class TimedPlayerMovementRunner
    {
        public static TimedPlayerMovementResult Run(
            Player player,
            IReadOnlyList<TimedPlayerInputStep> steps,
            int frameRate,
            Action<TimedPlayerInputBoundary>? inputBoundary = null,
            Action<TimedPlayerMovementFrame>? frameUpdated = null)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(steps);
            if (frameRate <= 0 || frameRate > 1000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameRate),
                    "The simulated frame rate must be from 1 through 1000.");
            }

            long frameIndex = 0;
            double simulationElapsedSeconds = 0;
            double frameIntervalSeconds = 1.0 / frameRate;
            var movementClock = Stopwatch.StartNew();

            for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
            {
                TimedPlayerInputStep step = steps[stepIndex];
                inputBoundary?.Invoke(new TimedPlayerInputBoundary(
                    true,
                    stepIndex,
                    step,
                    simulationElapsedSeconds));

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
                    frameUpdated?.Invoke(new TimedPlayerMovementFrame(
                        frameIndex,
                        simulationElapsedSeconds,
                        movementClock.Elapsed.TotalSeconds,
                        deltaSeconds,
                        step.Keys));
                }

                simulationElapsedSeconds = stepStartSimulationSeconds + step.DurationSeconds;
                inputBoundary?.Invoke(new TimedPlayerInputBoundary(
                    false,
                    stepIndex,
                    step,
                    simulationElapsedSeconds));
            }

            return new TimedPlayerMovementResult(
                frameIndex,
                simulationElapsedSeconds,
                movementClock.Elapsed.TotalSeconds);
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

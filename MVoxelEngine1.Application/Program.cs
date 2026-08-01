using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Flags;
using MVoxelEngine1.Infrastructure.Diagnostics;
using System;
using System.Runtime;

namespace MVoxelEngine1.Application
{
    class Program
    {
        static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("APP_ENVIRONMENT", "Development");
            ConsoleFlags.Parse(args);
            EnvironmentFlags.LoadEnvironmentFlags();
            FlagManager.ApplyFlags(args);

            if (!string.IsNullOrWhiteSpace(FlagManager.flags.benchmarkOutput))
            {
                if (string.IsNullOrWhiteSpace(FlagManager.flags.game))
                    throw new InvalidOperationException("Benchmark game is not set.");
                if (!FlagManager.flags.seed.HasValue)
                    throw new InvalidOperationException("Benchmark seed is not set.");
                if (FlagManager.flags.renderStreamingIfAllowed is not false)
                    throw new InvalidOperationException("Benchmark mode requires renderStreamingIfAllowed=false.");

                StartupPerformanceRecorder.Begin(FlagManager.flags.game, FlagManager.flags.seed.Value);
            }

            using (Window game = new Window())
            {
                game.Run();
            }
        }
    }
}

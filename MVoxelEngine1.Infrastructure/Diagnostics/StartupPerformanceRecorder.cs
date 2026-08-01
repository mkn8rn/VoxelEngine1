using System.Diagnostics;
using System.Text.Json;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
    public sealed record StartupPerformanceSnapshot
    {
        public required string Game { get; init; }
        public required int Seed { get; init; }
        public required double GameLoadMilliseconds { get; init; }
        public required long InitialGenerationMilliseconds { get; init; }
        public required long InitialChunkMeshBuildMilliseconds { get; init; }
        public required double BuildMilliseconds { get; init; }
        public required double RenderMilliseconds { get; init; }
        public required double CameraAppearanceMilliseconds { get; init; }
        public required double GpuStreamingStartMilliseconds { get; init; }
        public required DateTimeOffset RecordedAtUtc { get; init; }
    }

    public static class StartupPerformanceRecorder
    {
        private const long UnrecordedMilliseconds = -1;
        private static readonly object Sync = new();
        private static Stopwatch? timer;
        private static Stopwatch? initialGenerationTimer;
        private static Stopwatch? initialChunkMeshBuildTimer;
        private static string game = string.Empty;
        private static int seed;
        private static long gameLoadTicks;
        private static long initialGenerationMilliseconds = UnrecordedMilliseconds;
        private static long initialChunkMeshBuildMilliseconds = UnrecordedMilliseconds;
        private static long buildTicks;
        private static long renderTicks;
        private static long cameraAppearanceTicks;
        private static long gpuStreamingStartTicks;

        public static bool IsRunning => Volatile.Read(ref timer) is not null;

        public static bool HasGpuStreamingStarted => Volatile.Read(ref gpuStreamingStartTicks) > 0;

        public static bool IsComplete =>
            Volatile.Read(ref gameLoadTicks) > 0 &&
            Volatile.Read(ref initialGenerationMilliseconds) >= 0 &&
            Volatile.Read(ref initialChunkMeshBuildMilliseconds) >= 0 &&
            Volatile.Read(ref buildTicks) > 0 &&
            Volatile.Read(ref renderTicks) > 0 &&
            Volatile.Read(ref cameraAppearanceTicks) > 0 &&
            Volatile.Read(ref gpuStreamingStartTicks) > 0;

        public static void Begin(string gameName, int worldSeed)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                throw new ArgumentException("Game name is null or empty.", nameof(gameName));

            lock (Sync)
            {
                game = gameName;
                seed = worldSeed;
                gameLoadTicks = 0;
                initialGenerationTimer = null;
                initialChunkMeshBuildTimer = null;
                initialGenerationMilliseconds = UnrecordedMilliseconds;
                initialChunkMeshBuildMilliseconds = UnrecordedMilliseconds;
                buildTicks = 0;
                renderTicks = 0;
                cameraAppearanceTicks = 0;
                gpuStreamingStartTicks = 0;
                timer = Stopwatch.StartNew();
            }
        }

        public static void RecordGameLoaded() => RecordElapsed(ref gameLoadTicks);

        public static void BeginInitialGeneration() =>
            BeginPhase(ref initialGenerationTimer, ref initialGenerationMilliseconds, "initial generation");

        public static long CompleteInitialGeneration() =>
            CompletePhase(ref initialGenerationTimer, ref initialGenerationMilliseconds, "initial generation");

        public static void BeginInitialChunkMeshBuild() =>
            BeginPhase(ref initialChunkMeshBuildTimer, ref initialChunkMeshBuildMilliseconds, "initial chunk mesh build");

        public static long CompleteInitialChunkMeshBuild() =>
            CompletePhase(ref initialChunkMeshBuildTimer, ref initialChunkMeshBuildMilliseconds, "initial chunk mesh build");

        public static void RecordFirstChunkBuild(TimeSpan duration) => RecordDuration(ref buildTicks, duration);

        public static void RecordFirstRender(TimeSpan duration) => RecordDuration(ref renderTicks, duration);

        public static void RecordCameraAppearance() => RecordElapsed(ref cameraAppearanceTicks);

        public static void RecordGpuStreamingStart() => RecordElapsed(ref gpuStreamingStartTicks);

        public static StartupPerformanceSnapshot CreateSnapshot()
        {
            if (!IsComplete)
                throw new InvalidOperationException("Startup performance metrics are incomplete.");

            return new StartupPerformanceSnapshot
            {
                Game = game,
                Seed = seed,
                GameLoadMilliseconds = ToMilliseconds(Volatile.Read(ref gameLoadTicks)),
                InitialGenerationMilliseconds = Volatile.Read(ref initialGenerationMilliseconds),
                InitialChunkMeshBuildMilliseconds = Volatile.Read(ref initialChunkMeshBuildMilliseconds),
                BuildMilliseconds = ToMilliseconds(Volatile.Read(ref buildTicks)),
                RenderMilliseconds = ToMilliseconds(Volatile.Read(ref renderTicks)),
                CameraAppearanceMilliseconds = ToMilliseconds(Volatile.Read(ref cameraAppearanceTicks)),
                GpuStreamingStartMilliseconds = ToMilliseconds(Volatile.Read(ref gpuStreamingStartTicks)),
                RecordedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public static void WriteSnapshot(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Benchmark output path is null or empty.", nameof(outputPath));

            string fullPath = Path.GetFullPath(outputPath);
            string? outputDirectory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Benchmark output directory is not available.");

            Directory.CreateDirectory(outputDirectory);
            string json = JsonSerializer.Serialize(CreateSnapshot(), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            File.WriteAllText(fullPath, json);
        }

        private static void BeginPhase(
            ref Stopwatch? phaseTimer,
            ref long destination,
            string phaseName)
        {
            lock (Sync)
            {
                if (phaseTimer is not null)
                    throw new InvalidOperationException($"The {phaseName} timer is already running.");

                destination = UnrecordedMilliseconds;
                phaseTimer = Stopwatch.StartNew();
            }
        }

        private static long CompletePhase(
            ref Stopwatch? phaseTimer,
            ref long destination,
            string phaseName)
        {
            lock (Sync)
            {
                if (phaseTimer is null)
                    throw new InvalidOperationException($"The {phaseName} timer is not running.");

                phaseTimer.Stop();
                long elapsedMilliseconds = phaseTimer.ElapsedMilliseconds;
                phaseTimer = null;
                Volatile.Write(ref destination, elapsedMilliseconds);
                return elapsedMilliseconds;
            }
        }

        private static void RecordElapsed(ref long destination)
        {
            Stopwatch? activeTimer = Volatile.Read(ref timer);
            if (activeTimer is null)
                return;

            long elapsedTicks = Math.Max(1, activeTimer.Elapsed.Ticks);
            Interlocked.CompareExchange(ref destination, elapsedTicks, 0);
        }

        private static void RecordDuration(ref long destination, TimeSpan duration)
        {
            if (!IsRunning)
                return;

            long durationTicks = Math.Max(1, duration.Ticks);
            Interlocked.CompareExchange(ref destination, durationTicks, 0);
        }

        private static double ToMilliseconds(long ticks) => TimeSpan.FromTicks(ticks).TotalMilliseconds;
    }
}

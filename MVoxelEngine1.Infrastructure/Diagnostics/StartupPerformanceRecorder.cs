using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
    public sealed record StartupBenchmarkParameters
    {
        public required int ChunkSizeX { get; init; }
        public required int ChunkSizeY { get; init; }
        public required int ChunkSizeZ { get; init; }
        public required int Lod1Radius { get; init; }
        public required int Lod2Radius { get; init; }
        public required int Lod3Radius { get; init; }
        public required int Lod4Radius { get; init; }
        public required int Lod5Radius { get; init; }
        public required int InitialGenerationBuffer { get; init; }
        public required int RuntimeGenerationBuffer { get; init; }
        public required int BlockTileWidth { get; init; }
        public required int BlockTileHeight { get; init; }
        public required bool RenderStreamingAllowed { get; init; }
        public required bool RenderStreamingEnabled { get; init; }
        public required string FaceGenerationMode { get; init; }
        public required float WorldGenerationWorkersPerCore { get; init; }
        public required float InitialWorldGenerationWorkersPerCore { get; init; }
        public required float MeshBuildWorkersPerCore { get; init; }
        public required float InitialMeshBuildWorkersPerCore { get; init; }
        public required int WindowWidth { get; init; }
        public required int WindowHeight { get; init; }
        public required int LogicalProcessorCount { get; init; }
        public required bool ServerGarbageCollection { get; init; }
        public required string GarbageCollectionLatencyMode { get; init; }
    }

    public sealed record StartupPerformanceSnapshot
    {
        public const double GtrtTargetMilliseconds = 2_000;
        public const long MaximumWorkingSetBytes = 16L * 1024 * 1024 * 1024;

        public required string Game { get; init; }
        public required int Seed { get; init; }
        public required string GameInputSha256 { get; init; }
        public required string BlockRegistrySha256 { get; init; }
        public required StartupBenchmarkParameters Parameters { get; init; }
        public required double TargetGenerationToRenderMilliseconds { get; init; }
        public required long MaximumWorkingSetBytesLimit { get; init; }
        public required double GameLoadMilliseconds { get; init; }
        public required double InitialGenerationStartMilliseconds { get; init; }
        public required long InitialGenerationMilliseconds { get; init; }
        public required double InitialGenerationCompleteMilliseconds { get; init; }
        public required double InitialChunkMeshBuildStartMilliseconds { get; init; }
        public required long InitialChunkMeshBuildMilliseconds { get; init; }
        public required double InitialChunkMeshBuildCompleteMilliseconds { get; init; }
        public required double BuildMilliseconds { get; init; }
        public required double RenderMilliseconds { get; init; }
        public required double CameraAppearanceMilliseconds { get; init; }
        public required double GpuStreamingStartMilliseconds { get; init; }
        public required double GenerationToRenderMilliseconds { get; init; }
        public required long WorkingSetBytes { get; init; }
        public required long PeakWorkingSetBytes { get; init; }
        public required long ManagedHeapBytes { get; init; }
        public required long TotalAllocatedBytes { get; init; }
        public required double ProcessorTimeMilliseconds { get; init; }
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
        private static long initialGenerationStartTicks;
        private static long initialGenerationMilliseconds = UnrecordedMilliseconds;
        private static long initialGenerationCompleteTicks;
        private static long initialChunkMeshBuildStartTicks;
        private static long initialChunkMeshBuildMilliseconds = UnrecordedMilliseconds;
        private static long initialChunkMeshBuildCompleteTicks;
        private static long buildTicks;
        private static long renderTicks;
        private static long cameraAppearanceTicks;
        private static long gpuStreamingStartTicks;
        private static long generationToRenderTicks;

        public static bool IsRunning => Volatile.Read(ref timer) is not null;

        public static bool HasGpuStreamingStarted => Volatile.Read(ref gpuStreamingStartTicks) > 0;

        public static bool IsComplete =>
            Volatile.Read(ref gameLoadTicks) > 0 &&
            Volatile.Read(ref initialGenerationStartTicks) > 0 &&
            Volatile.Read(ref initialGenerationMilliseconds) >= 0 &&
            Volatile.Read(ref initialGenerationCompleteTicks) > 0 &&
            Volatile.Read(ref initialChunkMeshBuildStartTicks) > 0 &&
            Volatile.Read(ref initialChunkMeshBuildMilliseconds) >= 0 &&
            Volatile.Read(ref initialChunkMeshBuildCompleteTicks) > 0 &&
            Volatile.Read(ref buildTicks) > 0 &&
            Volatile.Read(ref renderTicks) > 0 &&
            Volatile.Read(ref cameraAppearanceTicks) > 0 &&
            Volatile.Read(ref gpuStreamingStartTicks) > 0 &&
            Volatile.Read(ref generationToRenderTicks) > 0;

        public static void Begin(string gameName, int worldSeed)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                throw new ArgumentException("Game name is null or empty.", nameof(gameName));

            lock (Sync)
            {
                game = gameName;
                seed = worldSeed;
                gameLoadTicks = 0;
                initialGenerationStartTicks = 0;
                initialGenerationTimer = null;
                initialChunkMeshBuildTimer = null;
                initialGenerationMilliseconds = UnrecordedMilliseconds;
                initialGenerationCompleteTicks = 0;
                initialChunkMeshBuildStartTicks = 0;
                initialChunkMeshBuildMilliseconds = UnrecordedMilliseconds;
                initialChunkMeshBuildCompleteTicks = 0;
                buildTicks = 0;
                renderTicks = 0;
                cameraAppearanceTicks = 0;
                gpuStreamingStartTicks = 0;
                generationToRenderTicks = 0;
                timer = Stopwatch.StartNew();
            }
        }

        public static void RecordGameLoaded() => RecordElapsed(ref gameLoadTicks);

        public static void BeginInitialGeneration() =>
            BeginPhase(
                ref initialGenerationTimer,
                ref initialGenerationMilliseconds,
                ref initialGenerationStartTicks,
                "initial generation");

        public static long CompleteInitialGeneration() =>
            CompletePhase(
                ref initialGenerationTimer,
                ref initialGenerationMilliseconds,
                ref initialGenerationCompleteTicks,
                "initial generation");

        public static void BeginInitialChunkMeshBuild() =>
            BeginPhase(
                ref initialChunkMeshBuildTimer,
                ref initialChunkMeshBuildMilliseconds,
                ref initialChunkMeshBuildStartTicks,
                "initial chunk mesh build");

        public static long CompleteInitialChunkMeshBuild() =>
            CompletePhase(
                ref initialChunkMeshBuildTimer,
                ref initialChunkMeshBuildMilliseconds,
                ref initialChunkMeshBuildCompleteTicks,
                "initial chunk mesh build");

        public static void RecordFirstChunkBuild(TimeSpan duration) => RecordDuration(ref buildTicks, duration);

        public static void RecordFirstRender(TimeSpan duration) => RecordDuration(ref renderTicks, duration);

        public static void RecordCameraAppearance() => RecordElapsed(ref cameraAppearanceTicks);

        public static void RecordGpuStreamingStart() => RecordElapsed(ref gpuStreamingStartTicks);

        public static double? RecordGenerationToRender()
        {
            Stopwatch? activeTimer = Volatile.Read(ref timer);
            if (activeTimer is null)
                return null;

            long elapsedTicks = Math.Max(1, activeTimer.Elapsed.Ticks);
            return Interlocked.CompareExchange(
                       ref generationToRenderTicks,
                       elapsedTicks,
                       0) == 0
                ? ToMilliseconds(elapsedTicks)
                : null;
        }

        public static StartupPerformanceSnapshot CreateSnapshot()
        {
            if (!IsComplete)
                throw new InvalidOperationException("Startup performance metrics are incomplete.");

            using Process process = Process.GetCurrentProcess();
            process.Refresh();

            return new StartupPerformanceSnapshot
            {
                Game = game,
                Seed = seed,
                GameInputSha256 = RuntimeInputHasher.HashGameInputs(),
                BlockRegistrySha256 = RuntimeInputHasher.HashBlockRegistry(),
                Parameters = CaptureParameters(),
                TargetGenerationToRenderMilliseconds =
                    StartupPerformanceSnapshot.GtrtTargetMilliseconds,
                MaximumWorkingSetBytesLimit =
                    StartupPerformanceSnapshot.MaximumWorkingSetBytes,
                GameLoadMilliseconds = ToMilliseconds(Volatile.Read(ref gameLoadTicks)),
                InitialGenerationStartMilliseconds =
                    ToMilliseconds(Volatile.Read(ref initialGenerationStartTicks)),
                InitialGenerationMilliseconds = Volatile.Read(ref initialGenerationMilliseconds),
                InitialGenerationCompleteMilliseconds =
                    ToMilliseconds(Volatile.Read(ref initialGenerationCompleteTicks)),
                InitialChunkMeshBuildStartMilliseconds =
                    ToMilliseconds(Volatile.Read(ref initialChunkMeshBuildStartTicks)),
                InitialChunkMeshBuildMilliseconds = Volatile.Read(ref initialChunkMeshBuildMilliseconds),
                InitialChunkMeshBuildCompleteMilliseconds =
                    ToMilliseconds(Volatile.Read(ref initialChunkMeshBuildCompleteTicks)),
                BuildMilliseconds = ToMilliseconds(Volatile.Read(ref buildTicks)),
                RenderMilliseconds = ToMilliseconds(Volatile.Read(ref renderTicks)),
                CameraAppearanceMilliseconds = ToMilliseconds(Volatile.Read(ref cameraAppearanceTicks)),
                GpuStreamingStartMilliseconds = ToMilliseconds(Volatile.Read(ref gpuStreamingStartTicks)),
                GenerationToRenderMilliseconds =
                    ToMilliseconds(Volatile.Read(ref generationToRenderTicks)),
                WorkingSetBytes = process.WorkingSet64,
                PeakWorkingSetBytes = process.PeakWorkingSet64,
                ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
                ProcessorTimeMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
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
            ref long startTicks,
            string phaseName)
        {
            lock (Sync)
            {
                if (phaseTimer is not null)
                    throw new InvalidOperationException($"The {phaseName} timer is already running.");

                destination = UnrecordedMilliseconds;
                RecordElapsed(ref startTicks);
                phaseTimer = Stopwatch.StartNew();
            }
        }

        private static long CompletePhase(
            ref Stopwatch? phaseTimer,
            ref long destination,
            ref long completeTicks,
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
                RecordElapsed(ref completeTicks);
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

        private static StartupBenchmarkParameters CaptureParameters()
        {
            GameSettings settings = GameManager.settings;
            ProgramFlags flags = FlagManager.flags;
            return new StartupBenchmarkParameters
            {
                ChunkSizeX = settings.chunkMaxX,
                ChunkSizeY = settings.chunkMaxY,
                ChunkSizeZ = settings.chunkMaxZ,
                Lod1Radius = settings.lod1RenderDistance,
                Lod2Radius = settings.lod2RenderDistance,
                Lod3Radius = settings.lod3RenderDistance,
                Lod4Radius = settings.lod4RenderDistance,
                Lod5Radius = settings.lod5RenderDistance,
                InitialGenerationBuffer = settings.chunkGenerationBufferInitial,
                RuntimeGenerationBuffer = settings.chunkGenerationBufferRuntime,
                BlockTileWidth = settings.blockTileWidth,
                BlockTileHeight = settings.blockTileHeight,
                RenderStreamingAllowed = settings.renderStreamingAllowed,
                RenderStreamingEnabled = flags.renderStreamingIfAllowed ?? false,
                FaceGenerationMode =
                    (flags.faceGenerationMode ?? FaceGenerationMode.Optimized).ToString(),
                WorldGenerationWorkersPerCore =
                    flags.worldGenWorkersPerCore ?? 0,
                InitialWorldGenerationWorkersPerCore =
                    flags.worldGenWorkersPerCoreInitial ??
                    flags.worldGenWorkersPerCore ?? 0,
                MeshBuildWorkersPerCore =
                    flags.meshRenderWorkersPerCore ?? 0,
                InitialMeshBuildWorkersPerCore =
                    flags.meshRenderWorkersPerCoreInitial ??
                    flags.meshRenderWorkersPerCore ?? 0,
                WindowWidth = flags.windowWidth ?? 0,
                WindowHeight = flags.windowHeight ?? 0,
                LogicalProcessorCount = Environment.ProcessorCount,
                ServerGarbageCollection = GCSettings.IsServerGC,
                GarbageCollectionLatencyMode = GCSettings.LatencyMode.ToString()
            };
        }

        private static double ToMilliseconds(long ticks) => TimeSpan.FromTicks(ticks).TotalMilliseconds;
    }
}

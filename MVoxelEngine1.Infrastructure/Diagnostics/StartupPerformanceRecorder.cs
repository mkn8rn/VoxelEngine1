using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
    public static class StartupPerformanceRecorder
    {

    private static readonly System.Text.Json.JsonSerializerOptions EvidenceJsonOptions0 = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        private const long UnrecordedMilliseconds = -1;
        private static readonly object Sync = new();
        private static Stopwatch? timer;
        private static long initialGenerationStartTimestamp;
        private static long initialChunkMeshBuildStartTimestamp;
        private static string game = string.Empty;
        private static int seed;
        private static long gameLoadTicks;
        private static long seedAcceptedTicks;
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
        private static long generationToRenderCompleteTicks;
        private static int windowConstructionCount;
        private static int actualGpuUploadCount;
        private static int openGlCallsAllowed;
        private static int graphicsForbidden;
        public static int WindowConstructionCount => Volatile.Read(ref windowConstructionCount);
        public static int ActualGpuUploadCount => Volatile.Read(ref actualGpuUploadCount);

        public static void ForbidGraphics() => Volatile.Write(ref graphicsForbidden, 1);
        public static bool IsRunning => Volatile.Read(ref timer)is not null;
        public static bool HasGpuStreamingStarted => Volatile.Read(ref gpuStreamingStartTicks) > 0;
        public static bool IsComplete => Volatile.Read(ref gameLoadTicks) > 0 && Volatile.Read(ref seedAcceptedTicks) > 0 && Volatile.Read(ref initialGenerationStartTicks) > 0 && Volatile.Read(ref initialGenerationMilliseconds) >= 0 && Volatile.Read(ref initialGenerationCompleteTicks) > 0 && Volatile.Read(ref initialChunkMeshBuildStartTicks) > 0 && Volatile.Read(ref initialChunkMeshBuildMilliseconds) >= 0 && Volatile.Read(ref initialChunkMeshBuildCompleteTicks) > 0 && Volatile.Read(ref buildTicks) > 0 && Volatile.Read(ref renderTicks) > 0 && Volatile.Read(ref cameraAppearanceTicks) > 0 && Volatile.Read(ref gpuStreamingStartTicks) > 0 && Volatile.Read(ref generationToRenderTicks) > 0 && Volatile.Read(ref generationToRenderCompleteTicks) > 0;
        public static bool IsHeadlessGtrtComplete => Volatile.Read(ref gameLoadTicks) > 0 && Volatile.Read(ref seedAcceptedTicks) > 0 && Volatile.Read(ref initialGenerationStartTicks) > 0 && Volatile.Read(ref initialGenerationMilliseconds) >= 0 && Volatile.Read(ref initialGenerationCompleteTicks) > 0 && Volatile.Read(ref initialChunkMeshBuildStartTicks) > 0 && Volatile.Read(ref initialChunkMeshBuildMilliseconds) >= 0 && Volatile.Read(ref initialChunkMeshBuildCompleteTicks) > 0 && Volatile.Read(ref buildTicks) > 0 && Volatile.Read(ref generationToRenderTicks) > 0 && Volatile.Read(ref generationToRenderCompleteTicks) > 0;

        public static void Begin(string gameName, int worldSeed, bool openGlCallsAllowed)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                throw new ArgumentException("Game name is null or empty.", nameof(gameName));
            lock (Sync)
            {
                game = gameName;
                seed = worldSeed;
                gameLoadTicks = 0;
                seedAcceptedTicks = 0;
                initialGenerationStartTicks = 0;
                initialGenerationStartTimestamp = 0;
                initialChunkMeshBuildStartTimestamp = 0;
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
                generationToRenderCompleteTicks = 0;
                windowConstructionCount = 0;
                actualGpuUploadCount = 0;
                StartupPerformanceRecorder.openGlCallsAllowed = openGlCallsAllowed ? 1 : 0;
                GenerationPerformanceRecorder.Reset();
                MeshPerformanceRecorder.Reset();
                timer = Stopwatch.StartNew();
            }
        }

        public static void RecordGameLoaded() => RecordElapsed(ref gameLoadTicks);
        public static void RecordSeedAccepted()
        {
            Stopwatch? activeTimer = Volatile.Read(ref timer);
            if (activeTimer is null)
                return;
            long elapsedTicks = Math.Max(1, activeTimer.Elapsed.Ticks);
            if (Interlocked.CompareExchange(ref seedAcceptedTicks, elapsedTicks, 0) != 0)
            {
                throw new InvalidOperationException("The benchmark seed was already accepted.");
            }
        }

        public static void BeginInitialGeneration() => BeginPhase(ref initialGenerationStartTimestamp, ref initialGenerationMilliseconds, ref initialGenerationStartTicks, "initial generation");
        public static long CompleteInitialGeneration() => CompletePhase(ref initialGenerationStartTimestamp, ref initialGenerationMilliseconds, ref initialGenerationCompleteTicks, "initial generation");
        public static void BeginInitialChunkMeshBuild() => BeginPhase(ref initialChunkMeshBuildStartTimestamp, ref initialChunkMeshBuildMilliseconds, ref initialChunkMeshBuildStartTicks, "initial chunk mesh build");
        public static long CompleteInitialChunkMeshBuild() => CompletePhase(ref initialChunkMeshBuildStartTimestamp, ref initialChunkMeshBuildMilliseconds, ref initialChunkMeshBuildCompleteTicks, "initial chunk mesh build");
        public static void RecordFirstChunkBuild(TimeSpan duration) => RecordDuration(ref buildTicks, duration);
        public static void RecordFirstRender(TimeSpan duration) => RecordDuration(ref renderTicks, duration);
        public static void RecordCameraAppearance() => RecordElapsed(ref cameraAppearanceTicks);
        public static void RecordGpuStreamingStart()
        {
            Interlocked.Increment(ref actualGpuUploadCount);
            if (Volatile.Read(ref graphicsForbidden) != 0 || (IsRunning && Volatile.Read(ref openGlCallsAllowed) == 0))
            {
                throw new InvalidOperationException("Headless GTRT mode forbids real GPU uploads.");
            }

            RecordElapsed(ref gpuStreamingStartTicks);
        }

        public static void RecordWindowConstruction()
        {
            Interlocked.Increment(ref windowConstructionCount);
            if (Volatile.Read(ref graphicsForbidden) != 0 || (IsRunning && Volatile.Read(ref openGlCallsAllowed) == 0))
            {
                throw new InvalidOperationException("Headless GTRT mode forbids Window construction.");
            }
        }

        public static double? RecordGenerationToRender()
        {
            Stopwatch? activeTimer = Volatile.Read(ref timer);
            if (activeTimer is null)
                return null;
            lock (Sync)
            {
                if (generationToRenderCompleteTicks > 0)
                    return null;
                long acceptedTicks = Volatile.Read(ref seedAcceptedTicks);
                if (acceptedTicks <= 0)
                {
                    throw new InvalidOperationException("The benchmark seed was not accepted.");
                }

                long completeTicks = Math.Max(1, activeTimer.Elapsed.Ticks);
                long durationTicks = Math.Max(1, completeTicks - acceptedTicks);
                Volatile.Write(ref generationToRenderTicks, durationTicks);
                Volatile.Write(ref generationToRenderCompleteTicks, completeTicks);
                return ToMilliseconds(durationTicks);
            }
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
                TargetGenerationToRenderMilliseconds = StartupPerformanceSnapshot.GtrtTargetMilliseconds,
                MaximumWorkingSetBytesLimit = StartupPerformanceSnapshot.MaximumWorkingSetBytes,
                GameLoadMilliseconds = ToMilliseconds(Volatile.Read(ref gameLoadTicks)),
                SeedAcceptedMilliseconds = ToMilliseconds(Volatile.Read(ref seedAcceptedTicks)),
                InitialGenerationStartMilliseconds = ToMilliseconds(Volatile.Read(ref initialGenerationStartTicks)),
                InitialGenerationMilliseconds = Volatile.Read(ref initialGenerationMilliseconds),
                InitialGenerationCompleteMilliseconds = ToMilliseconds(Volatile.Read(ref initialGenerationCompleteTicks)),
                InitialChunkMeshBuildStartMilliseconds = ToMilliseconds(Volatile.Read(ref initialChunkMeshBuildStartTicks)),
                InitialChunkMeshBuildMilliseconds = Volatile.Read(ref initialChunkMeshBuildMilliseconds),
                InitialChunkMeshBuildCompleteMilliseconds = ToMilliseconds(Volatile.Read(ref initialChunkMeshBuildCompleteTicks)),
                BuildMilliseconds = ToMilliseconds(Volatile.Read(ref buildTicks)),
                RenderMilliseconds = ToMilliseconds(Volatile.Read(ref renderTicks)),
                CameraAppearanceMilliseconds = ToMilliseconds(Volatile.Read(ref cameraAppearanceTicks)),
                GpuStreamingStartMilliseconds = ToMilliseconds(Volatile.Read(ref gpuStreamingStartTicks)),
                GenerationToRenderMilliseconds = ToMilliseconds(Volatile.Read(ref generationToRenderTicks)),
                GenerationToRenderCompleteMilliseconds = ToMilliseconds(Volatile.Read(ref generationToRenderCompleteTicks)),
                WorkingSetBytes = process.WorkingSet64,
                PeakWorkingSetBytes = process.PeakWorkingSet64,
                ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
                ProcessorTimeMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                GenerationDiagnostics = GenerationPerformanceRecorder.CreateSnapshot(),
                MeshDiagnostics = MeshPerformanceRecorder.CreateSnapshot(),
                RecordedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public static HeadlessGtrtPerformanceSnapshot CreateHeadlessGtrtSnapshot(SimulatedGpuUploadBoundarySnapshot simulatedUploadBoundary)
        {
            ArgumentNullException.ThrowIfNull(simulatedUploadBoundary);
            if (!IsHeadlessGtrtComplete)
            {
                throw new InvalidOperationException("Headless GTRT metrics are incomplete.");
            }

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBytes = process.WorkingSet64;
            long peakWorkingSetBytes = process.PeakWorkingSet64;
            long managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            long totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            double processorTimeMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
            return new HeadlessGtrtPerformanceSnapshot
            {
                Mode = "headlessGtrt",
                WindowCreated = Volatile.Read(ref windowConstructionCount) != 0,
                WindowConstructionCount = Volatile.Read(ref windowConstructionCount),
                OpenGlCallsAllowed = Volatile.Read(ref openGlCallsAllowed) != 0,
                ActualGpuUploadCount = Volatile.Read(ref actualGpuUploadCount),
                Game = game,
                Seed = seed,
                GameInputSha256 = RuntimeInputHasher.HashGameInputs(),
                BlockRegistrySha256 = RuntimeInputHasher.HashBlockRegistry(),
                Parameters = CaptureParameters(),
                TargetGenerationToRenderMilliseconds = HeadlessGtrtPerformanceSnapshot.GtrtGoalMilliseconds,
                MaximumGenerationToRenderMilliseconds = HeadlessGtrtPerformanceSnapshot.MaximumGtrtMilliseconds,
                MaximumWorkingSetBytesLimit = HeadlessGtrtPerformanceSnapshot.MaximumWorkingSetBytes,
                GameLoadMilliseconds = ToMilliseconds(Volatile.Read(ref gameLoadTicks)),
                SeedAcceptedMilliseconds = ToMilliseconds(Volatile.Read(ref seedAcceptedTicks)),
                InitialGenerationStartMilliseconds = ToMilliseconds(Volatile.Read(ref initialGenerationStartTicks)),
                InitialGenerationMilliseconds = Volatile.Read(ref initialGenerationMilliseconds),
                InitialGenerationCompleteMilliseconds = ToMilliseconds(Volatile.Read(ref initialGenerationCompleteTicks)),
                InitialChunkMeshBuildStartMilliseconds = ToMilliseconds(Volatile.Read(ref initialChunkMeshBuildStartTicks)),
                InitialChunkMeshBuildMilliseconds = Volatile.Read(ref initialChunkMeshBuildMilliseconds),
                InitialChunkMeshBuildCompleteMilliseconds = ToMilliseconds(Volatile.Read(ref initialChunkMeshBuildCompleteTicks)),
                FirstChunkMeshBuildMilliseconds = ToMilliseconds(Volatile.Read(ref buildTicks)),
                GenerationToRenderMilliseconds = ToMilliseconds(Volatile.Read(ref generationToRenderTicks)),
                GenerationToRenderCompleteMilliseconds = ToMilliseconds(Volatile.Read(ref generationToRenderCompleteTicks)),
                SimulatedUploadBoundary = simulatedUploadBoundary,
                WorkingSetBytes = workingSetBytes,
                PeakWorkingSetBytes = peakWorkingSetBytes,
                ManagedHeapBytes = managedHeapBytes,
                TotalAllocatedBytes = totalAllocatedBytes,
                ProcessorTimeMilliseconds = processorTimeMilliseconds,
                GenerationDiagnostics = GenerationPerformanceRecorder.CreateSnapshot(),
                MeshDiagnostics = MeshPerformanceRecorder.CreateSnapshot(),
                RecordedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public static void WriteSnapshot(string outputPath)
        {
            WriteJson(outputPath, CreateSnapshot());
        }

        public static void WriteHeadlessGtrtSnapshot(string outputPath, SimulatedGpuUploadBoundarySnapshot simulatedUploadBoundary)
        {
            WriteJson(outputPath, CreateHeadlessGtrtSnapshot(simulatedUploadBoundary));
        }

        private static void BeginPhase(ref long phaseStartTimestamp, ref long destination, ref long startTicks, string phaseName)
        {
            lock (Sync)
            {
                if (phaseStartTimestamp != 0)
                    throw new InvalidOperationException($"The {phaseName} timer is already running.");
                destination = UnrecordedMilliseconds;
                RecordElapsed(ref startTicks);
                Volatile.Write(ref phaseStartTimestamp, Stopwatch.GetTimestamp());
            }
        }

        private static long CompletePhase(ref long phaseStartTimestamp, ref long destination, ref long completeTicks, string phaseName)
        {
            lock (Sync)
            {
                long startTimestamp = Interlocked.Exchange(ref phaseStartTimestamp, 0);
                if (startTimestamp == 0)
                    throw new InvalidOperationException($"The {phaseName} timer is not running.");
                long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
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

        public static StartupBenchmarkParameters CaptureParameters()
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
                FaceGenerationMode = (flags.faceGenerationMode ?? FaceGenerationMode.Optimized).ToString(),
                WorldGenerationWorkersPerCore = flags.worldGenWorkersPerCore ?? 0,
                InitialWorldGenerationWorkersPerCore = flags.worldGenWorkersPerCoreInitial ?? flags.worldGenWorkersPerCore ?? 0,
                MeshBuildWorkersPerCore = flags.meshRenderWorkersPerCore ?? 0,
                InitialMeshBuildWorkersPerCore = flags.meshRenderWorkersPerCoreInitial ?? flags.meshRenderWorkersPerCore ?? 0,
                WindowWidth = flags.windowWidth ?? 0,
                WindowHeight = flags.windowHeight ?? 0,
                LogicalProcessorCount = Environment.ProcessorCount,
                ServerGarbageCollection = GCSettings.IsServerGC,
                GarbageCollectionLatencyMode = GCSettings.LatencyMode.ToString()
            };
        }

        private static void WriteJson<T>(string outputPath, T snapshot)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Benchmark output path is null or empty.", nameof(outputPath));
            }

            string fullPath = Path.GetFullPath(outputPath);
            string? outputDirectory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Benchmark output directory is not available.");
            }

            Directory.CreateDirectory(outputDirectory);
            string temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.incomplete");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, snapshot, EvidenceJsonOptions0);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private static double ToMilliseconds(long ticks) => TimeSpan.FromTicks(ticks).TotalMilliseconds;
    }
}

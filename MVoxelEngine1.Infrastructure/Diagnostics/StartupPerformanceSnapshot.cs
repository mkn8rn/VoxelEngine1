using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
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
        public required double SeedAcceptedMilliseconds { get; init; }
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
        public required double GenerationToRenderCompleteMilliseconds { get; init; }
        public required long WorkingSetBytes { get; init; }
        public required long PeakWorkingSetBytes { get; init; }
        public required long ManagedHeapBytes { get; init; }
        public required long TotalAllocatedBytes { get; init; }
        public required double ProcessorTimeMilliseconds { get; init; }
        public required GenerationPerformanceSnapshot GenerationDiagnostics { get; init; }
        public required MeshPerformanceSnapshot MeshDiagnostics { get; init; }
        public required DateTimeOffset RecordedAtUtc { get; init; }
    }
}

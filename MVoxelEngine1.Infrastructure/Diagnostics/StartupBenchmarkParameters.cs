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
}

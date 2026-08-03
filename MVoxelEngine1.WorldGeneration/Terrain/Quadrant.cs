using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Generation.Biomes;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.Tools.Noise;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Terrain
{
    // Stores exact block columns and uniform ranges for one chunk column.
    internal struct ChunkColumnProfile
    {
        public BlockColumnProfile[] BlockColumns;
        public ColumnUniformRanges UniformRanges;
        public bool BlockColumnsBuilt;
    }

    internal struct ColumnUniformRanges
    {
        public bool HasMaterial;
        public int MinimumMaterialStart;
        public int MaximumMaterialEnd;
        public bool AllColumnsHaveStone;
        public int StoneStartMinimum;
        public int StoneStartMaximum;
        public int StoneEndMinimum;
        public int StoneEndMaximum;
        public bool AllColumnsHaveSoil;
        public int SoilStartMinimum;
        public int SoilStartMaximum;
        public int SoilEndMinimum;
        public int SoilEndMaximum;
        public bool AllColumnsHaveWater;
        public int WaterStartMinimum;
        public int WaterStartMaximum;
        public int WaterEndMinimum;
        public int WaterEndMaximum;
    }

    // previously named Batch - you may see out of date comments referencing Batch.
    internal sealed class Quadrant
    {
        // ------------------------------------------------------------
        // Configuration / identity
        // ------------------------------------------------------------
        // quad size was previously 32, but reduced to 16. You may see out of date comments referencing 32.
        public const int QUAD_SIZE = 16;                                   // Horizontal footprint width in chunk units (X & Z)
        public readonly int quadX;                                         // Batch index along X in chunk space (floor(cx/32))
        public readonly int quadZ;                                         // Batch index along Z in chunk space (floor(cz/32))

        // ------------------------------------------------------------
        // Global caches (shared across all batches in process)
        // ------------------------------------------------------------
        // Noise instances cached by seed for procedural height generation.
        private static readonly ConcurrentDictionary<long, OpenSimplexNoise> _noiseCache = new();

        // ------------------------------------------------------------
        // Per‑batch storage
        // ------------------------------------------------------------
        private readonly Dictionary<(int cx, int cy, int cz), Chunk> _chunks = new(); // All chunk instances belonging to this batch
        private readonly HashSet<(int cx, int cz)> _generatedColumns = new();          // Tracks columns that have at least one vertical layer materialized
        private readonly object _lock = new();                                         // Coarse lock for chunk/column mutation

        // ------------------------------------------------------------
        // Biome (single biome applied to all chunks in the batch for now)
        // ------------------------------------------------------------
        public Biome Biome { get; private set; }

        // Seed captured when first column generated (required for lazy per-block column builds).
        private long _seed;
        private bool _seedSet;

        private readonly ChunkColumnProfile[,] _profiles = new ChunkColumnProfile[QUAD_SIZE, QUAD_SIZE];

        // ------------------------------------------------------------
        // Batch state flags
        // ------------------------------------------------------------
        public volatile bool Dirty;                              // Marked when chunk additions/removals occur (world save grouping)

        // ------------------------------------------------------------
        // Uniform classification kinds for vertical chunk slabs
        // ------------------------------------------------------------
        internal enum UniformKind
        {
            None = 0,
            AllAir = 1,
            AllStone = 2,
            AllSoil = 3,
            AllWater = 4
        }

        // ------------------------------------------------------------
        // Public surface API
        // ------------------------------------------------------------
        public Quadrant(int batchX, int batchZ)
        {
            this.quadX = batchX;
            this.quadZ = batchZ;
        }

        public IEnumerable<Chunk> Chunks
        {
            get
            {
                lock (_lock)
                {
                    return new List<Chunk>(_chunks.Values);
                }
            }
        }

        public bool TryGetChunk(int cx, int cy, int cz, out Chunk chunk)
        {
            lock (_lock)
            {
                return _chunks.TryGetValue((cx, cy, cz), out chunk);
            }
        }

        public void AddOrReplaceChunk(Chunk chunk, int cx, int cy, int cz)
        {
            lock (_lock)
            {
                _chunks[(cx, cy, cz)] = chunk;
                _generatedColumns.Add((cx, cz));
                Dirty = true;
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _chunks.Count == 0;
                }
            }
        }

        public void SetBiomeIfUnset(Biome biome)
        {
            if (Biome == null && biome != null)
            {
                Biome = biome;
            }
        }

        // ------------------------------------------------------------
        // Static helpers for batch indexing
        // ------------------------------------------------------------
        public static (int bx, int bz) GetBatchIndices(int cx, int cz)
        {
            static int FloorDivLocal(int a, int b) => (int)Math.Floor((double)a / b);
            return (FloorDivLocal(cx, QUAD_SIZE), FloorDivLocal(cz, QUAD_SIZE));
        }

        public static (int localX, int localZ) LocalIndices(int cx, int cz)
        {
            int localX = (int)((uint)(cx % QUAD_SIZE + QUAD_SIZE) % QUAD_SIZE);
            int localZ = (int)((uint)(cz % QUAD_SIZE + QUAD_SIZE) % QUAD_SIZE);
            return (localX, localZ);
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

        // ------------------------------------------------------------
        // Global noise + heightmap generation & retrieval
        // ------------------------------------------------------------
        private static OpenSimplexNoise GetNoise(long seed)
        {
            return _noiseCache.GetOrAdd(seed, s => new OpenSimplexNoise(s));
        }

        internal static void FillHeightMap(
            long seed,
            int baseX,
            int baseZ,
            int sizeX,
            int sizeZ,
            Span<float> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);
            int valueCount = checked(sizeX * sizeZ);
            if (destination.Length < valueCount)
            {
                throw new ArgumentException(
                    "The destination is smaller than the height map.",
                    nameof(destination));
            }

            OpenSimplexNoise noise = GetNoise(seed);

            const float scale = 0.001f;
            const float minHeight = 1f;
            const float maxHeight = 1000f;

            for (int x = 0; x < sizeX; x++)
            {
                int rowOffset = x * sizeZ;
                int worldX = unchecked(x + baseX);
                for (int z = 0; z < sizeZ; z++)
                {
                    float noiseValue = (float)noise.Evaluate(
                        worldX * scale,
                        unchecked(z + baseZ) * scale);
                    float normalizedValue = noiseValue * 0.5f + 0.5f;
                    destination[rowOffset + z] = normalizedValue *
                        (maxHeight - minHeight) + minHeight;
                }
            }
        }

        private void EnsureBlockColumnsBuilt(
            int columnCx,
            int columnCz,
            scoped in NativeWorkspace<float> generationWorkspace)
        {
            var (lx, lz) = LocalIndices(columnCx, columnCz);
            ref ChunkColumnProfile profile = ref _profiles[lx, lz];
            if (profile.BlockColumnsBuilt)
                return;
            if (!_seedSet)
                throw new InvalidOperationException(
                    "Seed not set before building block columns.");
            Biome biome = Biome ?? throw new InvalidOperationException(
                "Biome must be set before building block columns.");

            int sizeX = GameManager.settings.chunkMaxX;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int profileCount = checked(sizeX * sizeZ);
            int workspaceLength = checked(profileCount * 2);
            var blockColumns = new BlockColumnProfile[profileCount];
            int baseWorldX = unchecked(columnCx * sizeX);
            int baseWorldZ = unchecked(columnCz * sizeZ);
            bool recordPerformance = StartupPerformanceRecorder.IsRunning;

            ColumnUniformRanges ranges = generationWorkspace.Process(
                workspaceLength,
                values => FillGenerationWorkspace(
                    values,
                    profileCount,
                    baseWorldX,
                    baseWorldZ,
                    sizeX,
                    sizeZ,
                    _seed,
                    recordPerformance),
                values => BuildBlockColumns(
                    values,
                    profileCount,
                    sizeX,
                    sizeZ,
                    biome,
                    blockColumns,
                    recordPerformance));

            profile.BlockColumns = blockColumns;
            profile.UniformRanges = ranges;
            profile.BlockColumnsBuilt = true;
        }

        private static void FillGenerationWorkspace(
            Span<float> values,
            int profileCount,
            int baseWorldX,
            int baseWorldZ,
            int sizeX,
            int sizeZ,
            long seed,
            bool recordPerformance)
        {
            Span<float> heights = values.Slice(0, profileCount);
            Span<float> noiseValues = values.Slice(profileCount, profileCount);
            long phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            FillHeightMap(
                seed,
                baseWorldX,
                baseWorldZ,
                sizeX,
                sizeZ,
                heights);
            if (recordPerformance)
            {
                GenerationPerformanceRecorder.RecordHeightMap(
                    GenerationPerformanceRecorder.GetElapsedTicks(phaseStart));
                phaseStart = Stopwatch.GetTimestamp();
            }

            TerrainGenerationUtils.FillSmoothValueNoise01(
                baseWorldX,
                baseWorldZ,
                sizeX,
                sizeZ,
                seed,
                noiseValues);
            if (recordPerformance)
            {
                GenerationPerformanceRecorder.RecordSmoothValueNoise(
                    GenerationPerformanceRecorder.GetElapsedTicks(phaseStart));
            }
        }

        private static ColumnUniformRanges BuildBlockColumns(
            ReadOnlySpan<float> values,
            int profileCount,
            int sizeX,
            int sizeZ,
            Biome biome,
            BlockColumnProfile[] blockColumns,
            bool recordPerformance)
        {
            ReadOnlySpan<float> heights = values.Slice(0, profileCount);
            ReadOnlySpan<float> noiseValues = values.Slice(
                profileCount,
                profileCount);
            var ranges = new ColumnUniformRanges
            {
                MinimumMaterialStart = int.MaxValue,
                MaximumMaterialEnd = int.MinValue,
                AllColumnsHaveStone = true,
                StoneStartMinimum = int.MaxValue,
                StoneStartMaximum = int.MinValue,
                StoneEndMinimum = int.MaxValue,
                StoneEndMaximum = int.MinValue,
                AllColumnsHaveSoil = true,
                SoilStartMinimum = int.MaxValue,
                SoilStartMaximum = int.MinValue,
                SoilEndMinimum = int.MaxValue,
                SoilEndMaximum = int.MinValue,
                AllColumnsHaveWater = true,
                WaterStartMinimum = int.MaxValue,
                WaterStartMaximum = int.MinValue,
                WaterEndMinimum = int.MaxValue,
                WaterEndMaximum = int.MinValue
            };
            long phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;

            for (int x = 0; x < sizeX; x++)
            {
                int rowOffset = x * sizeZ;
                int previousXOffset = x == 0
                    ? rowOffset
                    : rowOffset - sizeZ;
                int nextXOffset = x == sizeX - 1
                    ? rowOffset
                    : rowOffset + sizeZ;
                for (int z = 0; z < sizeZ; z++)
                {
                    int profileIndex = rowOffset + z;
                    int previousZ = z == 0 ? 0 : z - 1;
                    int nextZ = z == sizeZ - 1 ? z : z + 1;
                    int surface = (int)heights[profileIndex];
                    float dx = heights[nextXOffset + z] -
                        heights[previousXOffset + z];
                    float dz = heights[rowOffset + nextZ] -
                        heights[rowOffset + previousZ];
                    float grad = MathF.Sqrt(dx * dx + dz * dz);
                    float slope01 = MathF.Min(1f, grad / 6f);

                    var (stoneStart, stoneEnd, soilStart, soilEnd, waterStart, waterEnd) =
                        TerrainGenerationUtils.DeriveWorldStoneSoilSpansFromNoise(
                            surface,
                            biome,
                            slope01,
                            noiseValues[profileIndex]);

                    blockColumns[profileIndex] = new BlockColumnProfile
                    {
                        StoneStart = stoneStart,
                        StoneEnd = stoneEnd,
                        SoilStart = soilStart,
                        SoilEnd = soilEnd,
                        WaterStart = waterStart,
                        WaterEnd = waterEnd
                    };

                    bool hasStone = stoneStart >= 0 && stoneEnd >= stoneStart;
                    bool hasSoil = soilStart >= 0 && soilEnd >= soilStart;
                    bool hasWater = waterStart >= 0 && waterEnd >= waterStart;

                    if (hasStone)
                    {
                        ranges.HasMaterial = true;
                        if (stoneStart < ranges.MinimumMaterialStart) ranges.MinimumMaterialStart = stoneStart;
                        if (stoneEnd > ranges.MaximumMaterialEnd) ranges.MaximumMaterialEnd = stoneEnd;
                        if (stoneStart < ranges.StoneStartMinimum) ranges.StoneStartMinimum = stoneStart;
                        if (stoneStart > ranges.StoneStartMaximum) ranges.StoneStartMaximum = stoneStart;
                        if (stoneEnd < ranges.StoneEndMinimum) ranges.StoneEndMinimum = stoneEnd;
                        if (stoneEnd > ranges.StoneEndMaximum) ranges.StoneEndMaximum = stoneEnd;
                    }
                    else
                    {
                        ranges.AllColumnsHaveStone = false;
                    }

                    if (hasSoil)
                    {
                        ranges.HasMaterial = true;
                        if (soilStart < ranges.MinimumMaterialStart) ranges.MinimumMaterialStart = soilStart;
                        if (soilEnd > ranges.MaximumMaterialEnd) ranges.MaximumMaterialEnd = soilEnd;
                        if (soilStart < ranges.SoilStartMinimum) ranges.SoilStartMinimum = soilStart;
                        if (soilStart > ranges.SoilStartMaximum) ranges.SoilStartMaximum = soilStart;
                        if (soilEnd < ranges.SoilEndMinimum) ranges.SoilEndMinimum = soilEnd;
                        if (soilEnd > ranges.SoilEndMaximum) ranges.SoilEndMaximum = soilEnd;
                    }
                    else
                    {
                        ranges.AllColumnsHaveSoil = false;
                    }

                    if (hasWater)
                    {
                        ranges.HasMaterial = true;
                        if (waterStart < ranges.MinimumMaterialStart) ranges.MinimumMaterialStart = waterStart;
                        if (waterEnd > ranges.MaximumMaterialEnd) ranges.MaximumMaterialEnd = waterEnd;
                        if (waterStart < ranges.WaterStartMinimum) ranges.WaterStartMinimum = waterStart;
                        if (waterStart > ranges.WaterStartMaximum) ranges.WaterStartMaximum = waterStart;
                        if (waterEnd < ranges.WaterEndMinimum) ranges.WaterEndMinimum = waterEnd;
                        if (waterEnd > ranges.WaterEndMaximum) ranges.WaterEndMaximum = waterEnd;
                    }
                    else
                    {
                        ranges.AllColumnsHaveWater = false;
                    }
                }
            }

            if (recordPerformance)
            {
                GenerationPerformanceRecorder.RecordProfileDerivation(
                    GenerationPerformanceRecorder.GetElapsedTicks(phaseStart));
            }
            return ranges;
        }

        private BlockColumnProfile[] GetChunkBlockMap(
            int columnCx,
            int columnCz,
            scoped in NativeWorkspace<float> generationWorkspace)
        {
            EnsureBlockColumnsBuilt(
                columnCx,
                columnCz,
                in generationWorkspace);
            var (lx, lz) = LocalIndices(columnCx, columnCz);
            ref readonly ChunkColumnProfile profile = ref _profiles[lx, lz];
            return profile.BlockColumns;
        }

        private UniformKind ClassifyColumnVerticalChunk(
            int columnCx,
            int columnCz,
            int cy,
            int sizeY,
            out byte materialMask)
        {
            var (lx, lz) = LocalIndices(columnCx, columnCz);
            ref readonly ColumnUniformRanges ranges = ref _profiles[lx, lz].UniformRanges;
            int baseY = cy * sizeY;
            int topY = baseY + sizeY - 1;
            materialMask = 0;
            if (ranges.StoneStartMinimum <= topY &&
                ranges.StoneEndMaximum >= baseY)
            {
                materialMask |= 1;
            }
            if (ranges.SoilStartMinimum <= topY &&
                ranges.SoilEndMaximum >= baseY)
            {
                materialMask |= 2;
            }
            if (ranges.WaterStartMinimum <= topY &&
                ranges.WaterEndMaximum >= baseY)
            {
                materialMask |= 4;
            }

            if (ranges.AllColumnsHaveWater &&
                ranges.WaterStartMaximum <= baseY &&
                ranges.WaterEndMinimum >= topY)
                return UniformKind.AllWater;
            if (ranges.AllColumnsHaveStone &&
                ranges.StoneStartMaximum <= baseY &&
                ranges.StoneEndMinimum >= topY)
                return UniformKind.AllStone;
            if (ranges.AllColumnsHaveSoil &&
                ranges.SoilStartMaximum <= baseY &&
                ranges.SoilEndMinimum >= topY)
                return UniformKind.AllSoil;
            if (!ranges.HasMaterial ||
                topY < ranges.MinimumMaterialStart ||
                baseY > ranges.MaximumMaterialEnd)
                return UniformKind.AllAir;
            return UniformKind.None;
        }

        // Delegate for registering newly created chunks with world dictionaries.
        public delegate void ChunkRegistrar((int cx, int cy, int cz) key, Chunk chunk, bool insideLod1);

        // ------------------------------------------------------------
        // Column generation entry point
        // ------------------------------------------------------------
        // Generates (or loads) all vertical chunk layers inside a single column of the batch.
        // Builds exact block-column spans once, classifies each slab, creates chunks, and registers them.
        internal void GenerateOrLoadColumn(
            int cx,
            int cz,
            int playerCx,
            int playerCy,
            int playerCz,
            int lodDist,
            int verticalRange,
            long regionLimit,
            long seed,
            string chunkSaveDirectory,
            int sizeX,
            int sizeY,
            int sizeZ,
            scoped in NativeWorkspace<float> generationWorkspace,
            ChunkRegistrar registrar)
        {
            if (Math.Abs(cx - playerCx) > lodDist + 1 || Math.Abs(cz - playerCz) > lodDist + 1)
                return;

            bool recordPerformance = StartupPerformanceRecorder.IsRunning;
            long profileTicks = 0;
            long classificationTicks = 0;
            long spanMapTicks = 0;
            long constructionTicks = 0;
            long registrationTicks = 0;

            if (Biome == null)
            {
                int worldBaseX = cx * sizeX;
                int worldBaseZ = cz * sizeZ;
                Biome = BiomeManager.SelectBiomeForChunk(seed, worldBaseX, worldBaseZ);
            }
            if (!_seedSet) { _seed = seed; _seedSet = true; }

            int vMin = playerCy - verticalRange;
            int vMax = playerCy + verticalRange;
            if (vMin < -regionLimit) vMin = (int)-regionLimit;
            if (vMax > regionLimit) vMax = (int)regionLimit;

            int columnBaseX = cx * sizeX;
            int columnBaseZ = cz * sizeZ;
            long phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            BlockColumnProfile[] spanMap = GetChunkBlockMap(
                cx,
                cz,
                in generationWorkspace);
            if (recordPerformance)
                spanMapTicks += GenerationPerformanceRecorder.GetElapsedTicks(phaseStart);

            for (int cy = vMin; cy <= vMax; cy++)
            {
                if (TryGetChunk(cx, cy, cz, out _)) continue;
                phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
                UniformKind classified = ClassifyColumnVerticalChunk(
                    cx,
                    cz,
                    cy,
                    sizeY,
                    out byte materialMask);
                if (recordPerformance)
                    classificationTicks += GenerationPerformanceRecorder.GetElapsedTicks(phaseStart);
                Chunk.UniformOverride overrideKind = classified switch
                {
                    UniformKind.AllAir => Chunk.UniformOverride.AllAir,
                    UniformKind.AllStone => Chunk.UniformOverride.AllStone,
                    UniformKind.AllSoil => Chunk.UniformOverride.AllSoil,
                    UniformKind.AllWater => Chunk.UniformOverride.AllWater,
                    _ => Chunk.UniformOverride.None
                };

                var worldPos = new Vector3(columnBaseX, cy * sizeY, columnBaseZ);
                phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
                var chunk = new Chunk(
                    worldPos,
                    seed,
                    chunkSaveDirectory,
                    autoGenerate: true,
                    uniformOverride: overrideKind,
                    columnSpanMap: spanMap,
                    generatedMaterialMask: materialMask);
                if (recordPerformance)
                {
                    constructionTicks += GenerationPerformanceRecorder.GetElapsedTicks(phaseStart);
                    GenerationPerformanceRecorder.RecordChunkKind((int)overrideKind);
                }
                AddOrReplaceChunk(chunk, cx, cy, cz);

                bool insideLod1 = Math.Abs(cx - playerCx) <= lodDist && Math.Abs(cz - playerCz) <= lodDist && Math.Abs(cy - playerCy) <= verticalRange;
                phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
                registrar((cx, cy, cz), chunk, insideLod1);
                if (recordPerformance)
                    registrationTicks += GenerationPerformanceRecorder.GetElapsedTicks(phaseStart);
            }

            if (recordPerformance)
            {
                GenerationPerformanceRecorder.RecordColumn(
                    profileTicks,
                    classificationTicks,
                    spanMapTicks,
                    constructionTicks,
                    registrationTicks);
            }
        }
    }
}

using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using System.Diagnostics;

namespace MVoxelEngine1.Graphics.Terrain.Sections
{
    internal partial class SectionRender
    {
        private void BuildGeneratedSpans(
            out int opaqueFaceCount,
            out byte[] opaqueOffsets,
            out uint[] opaqueTileIndices,
            out byte[] opaqueFaceDirs,
            out int transparentFaceCount,
            out byte[] transparentOffsets,
            out uint[] transparentTileIndices,
            out byte[] transparentFaceDirs)
        {
            GeneratedChunkSpanData source = data.GeneratedSpans ??
                throw new InvalidOperationException("Generated span data is not available.");

            bool recordPerformance = StartupPerformanceRecorder.IsRunning;
            long phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            var counter = new GeneratedFaceWriter(source, atlas);
            GenerateGeneratedFaces(source, ref counter);
            long countPassTicks = recordPerformance
                ? MeshPerformanceRecorder.GetElapsedTicks(phaseStart)
                : 0;

            phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            var writer = new GeneratedFaceWriter(
                source,
                atlas,
                counter.OpaqueCount,
                counter.TransparentCount);
            long preparationTicks = recordPerformance
                ? MeshPerformanceRecorder.GetElapsedTicks(phaseStart)
                : 0;
            phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            GenerateGeneratedFaces(source, ref writer);
            writer.ValidateCompletion();
            long writePassTicks = recordPerformance
                ? MeshPerformanceRecorder.GetElapsedTicks(phaseStart)
                : 0;

            opaqueFaceCount = writer.OpaqueCount;
            opaqueOffsets = writer.OpaqueOffsets!;
            opaqueTileIndices = writer.OpaqueTileIndices!;
            opaqueFaceDirs = writer.OpaqueDirections!;
            transparentFaceCount = writer.TransparentCount;
            transparentOffsets = writer.TransparentOffsets!;
            transparentTileIndices = writer.TransparentTileIndices!;
            transparentFaceDirs = writer.TransparentDirections!;
            if (recordPerformance)
            {
                MeshPerformanceRecorder.RecordGeneratedSpanPhases(
                    countPassTicks,
                    preparationTicks,
                    writePassTicks,
                    opaqueFaceCount,
                    transparentFaceCount);
            }
        }

        private void GenerateGeneratedFaces(
            GeneratedChunkSpanData source,
            ref GeneratedFaceWriter writer)
        {
            for (int x = 0; x < source.Width; x++)
            {
                for (int z = 0; z < source.Depth; z++)
                {
                    ref readonly BlockColumnProfile column =
                        ref source.Columns[x * source.Depth + z];
                    GenerateGeneratedInterval(
                        source,
                        column,
                        source.StoneBlockId,
                        column.StoneStart,
                        column.StoneEnd,
                        x,
                        z,
                        ref writer);
                    GenerateGeneratedInterval(
                        source,
                        column,
                        source.SoilBlockId,
                        column.SoilStart,
                        column.SoilEnd,
                        x,
                        z,
                        ref writer);
                    GenerateGeneratedInterval(
                        source,
                        column,
                        source.WaterBlockId,
                        column.WaterStart,
                        column.WaterEnd,
                        x,
                        z,
                        ref writer);
                }
            }
        }

        private void GenerateGeneratedInterval(
            GeneratedChunkSpanData source,
            in BlockColumnProfile column,
            ushort blockId,
            int intervalStart,
            int intervalEnd,
            int x,
            int z,
            ref GeneratedFaceWriter writer)
        {
            if (intervalStart < 0 || intervalEnd < intervalStart)
                return;

            int chunkStart = source.ChunkBaseY;
            int chunkEnd = chunkStart + source.Height - 1;
            int worldStart = Math.Max(intervalStart, chunkStart);
            int worldEnd = Math.Min(intervalEnd, chunkEnd);
            if (worldStart > worldEnd)
                return;

            int localStart = worldStart - chunkStart;
            int localEnd = worldEnd - chunkStart;
            int horizontalIndex = x * source.Depth + z;

            if (localStart == 0)
            {
                GetBoundaryNeighbor(
                    data.NeighborPlaneNegY,
                    data.NeighborTransparentPlaneNegY,
                    horizontalIndex,
                    out bool neighborOpaque,
                    out ushort neighborId);
                if (FaceVisible(blockId, neighborOpaque, neighborId))
                    writer.Emit(blockId, 2, x, localStart, z);
            }
            else
            {
                ushort neighborId = source.GetBlockWorld(column, worldStart - 1);
                if (FaceVisible(
                    blockId,
                    TerrainLoader.IsOpaque(neighborId),
                    neighborId))
                {
                    writer.Emit(blockId, 2, x, localStart, z);
                }
            }

            if (localEnd == source.Height - 1)
            {
                GetBoundaryNeighbor(
                    data.NeighborPlanePosY,
                    data.NeighborTransparentPlanePosY,
                    horizontalIndex,
                    out bool neighborOpaque,
                    out ushort neighborId);
                if (FaceVisible(blockId, neighborOpaque, neighborId))
                    writer.Emit(blockId, 3, x, localEnd, z);
            }
            else
            {
                ushort neighborId = source.GetBlockWorld(column, worldEnd + 1);
                if (FaceVisible(
                    blockId,
                    TerrainLoader.IsOpaque(neighborId),
                    neighborId))
                {
                    writer.Emit(blockId, 3, x, localEnd, z);
                }
            }

            if (x == 0)
            {
                EmitGeneratedBoundaryRange(
                    blockId,
                    0,
                    x,
                    z,
                    localStart,
                    localEnd,
                    data.NeighborPlaneNegX,
                    data.NeighborTransparentPlaneNegX,
                    z * source.Height,
                    ref writer);
            }
            else
            {
                EmitGeneratedColumnRange(
                    source,
                    source.Columns[(x - 1) * source.Depth + z],
                    blockId,
                    0,
                    x,
                    z,
                    worldStart,
                    worldEnd,
                    ref writer);
            }

            if (x == source.Width - 1)
            {
                EmitGeneratedBoundaryRange(
                    blockId,
                    1,
                    x,
                    z,
                    localStart,
                    localEnd,
                    data.NeighborPlanePosX,
                    data.NeighborTransparentPlanePosX,
                    z * source.Height,
                    ref writer);
            }
            else
            {
                EmitGeneratedColumnRange(
                    source,
                    source.Columns[(x + 1) * source.Depth + z],
                    blockId,
                    1,
                    x,
                    z,
                    worldStart,
                    worldEnd,
                    ref writer);
            }

            if (z == 0)
            {
                EmitGeneratedBoundaryRange(
                    blockId,
                    4,
                    x,
                    z,
                    localStart,
                    localEnd,
                    data.NeighborPlaneNegZ,
                    data.NeighborTransparentPlaneNegZ,
                    x * source.Height,
                    ref writer);
            }
            else
            {
                EmitGeneratedColumnRange(
                    source,
                    source.Columns[x * source.Depth + z - 1],
                    blockId,
                    4,
                    x,
                    z,
                    worldStart,
                    worldEnd,
                    ref writer);
            }

            if (z == source.Depth - 1)
            {
                EmitGeneratedBoundaryRange(
                    blockId,
                    5,
                    x,
                    z,
                    localStart,
                    localEnd,
                    data.NeighborPlanePosZ,
                    data.NeighborTransparentPlanePosZ,
                    x * source.Height,
                    ref writer);
            }
            else
            {
                EmitGeneratedColumnRange(
                    source,
                    source.Columns[x * source.Depth + z + 1],
                    blockId,
                    5,
                    x,
                    z,
                    worldStart,
                    worldEnd,
                    ref writer);
            }
        }

        private static void EmitGeneratedColumnRange(
            GeneratedChunkSpanData source,
            in BlockColumnProfile neighborColumn,
            ushort blockId,
            byte direction,
            int x,
            int z,
            int worldStart,
            int worldEnd,
            ref GeneratedFaceWriter writer)
        {
            int current = worldStart;
            while (current <= worldEnd)
            {
                GetGeneratedNeighborRun(
                    source,
                    neighborColumn,
                    current,
                    worldEnd,
                    out ushort neighborId,
                    out int runEnd);
                if (FaceVisible(
                    blockId,
                    TerrainLoader.IsOpaque(neighborId),
                    neighborId))
                {
                    writer.EmitYRange(
                        blockId,
                        direction,
                        x,
                        current - source.ChunkBaseY,
                        runEnd - source.ChunkBaseY,
                        z);
                }

                current = runEnd + 1;
            }
        }

        private static void EmitGeneratedBoundaryRange(
            ushort blockId,
            byte direction,
            int x,
            int z,
            int localStart,
            int localEnd,
            ulong[] opaquePlane,
            ushort[] transparentPlane,
            int planeBaseIndex,
            ref GeneratedFaceWriter writer)
        {
            int visibleStart = -1;
            for (int y = localStart; y <= localEnd; y++)
            {
                GetBoundaryNeighbor(
                    opaquePlane,
                    transparentPlane,
                    planeBaseIndex + y,
                    out bool neighborOpaque,
                    out ushort neighborId);
                bool visible = FaceVisible(blockId, neighborOpaque, neighborId);
                if (visible && visibleStart < 0)
                {
                    visibleStart = y;
                }
                else if (!visible && visibleStart >= 0)
                {
                    writer.EmitYRange(
                        blockId,
                        direction,
                        x,
                        visibleStart,
                        y - 1,
                        z);
                    visibleStart = -1;
                }
            }

            if (visibleStart >= 0)
            {
                writer.EmitYRange(
                    blockId,
                    direction,
                    x,
                    visibleStart,
                    localEnd,
                    z);
            }
        }

        private static void GetGeneratedNeighborRun(
            GeneratedChunkSpanData source,
            in BlockColumnProfile column,
            int worldY,
            int maximumWorldY,
            out ushort blockId,
            out int runEnd)
        {
            if (column.StoneStart >= 0 &&
                column.StoneEnd >= column.StoneStart &&
                worldY >= column.StoneStart &&
                worldY <= column.StoneEnd)
            {
                blockId = source.StoneBlockId;
                runEnd = Math.Min(column.StoneEnd, maximumWorldY);
                return;
            }
            if (column.SoilStart >= 0 &&
                column.SoilEnd >= column.SoilStart &&
                worldY >= column.SoilStart &&
                worldY <= column.SoilEnd)
            {
                blockId = source.SoilBlockId;
                runEnd = Math.Min(column.SoilEnd, maximumWorldY);
                return;
            }
            if (column.WaterStart >= 0 &&
                column.WaterEnd >= column.WaterStart &&
                worldY >= column.WaterStart &&
                worldY <= column.WaterEnd)
            {
                blockId = source.WaterBlockId;
                runEnd = Math.Min(column.WaterEnd, maximumWorldY);
                return;
            }

            blockId = 0;
            runEnd = maximumWorldY;
            if (column.StoneStart >= 0 && column.StoneStart > worldY)
                runEnd = Math.Min(runEnd, column.StoneStart - 1);
            if (column.SoilStart >= 0 && column.SoilStart > worldY)
                runEnd = Math.Min(runEnd, column.SoilStart - 1);
            if (column.WaterStart >= 0 && column.WaterStart > worldY)
                runEnd = Math.Min(runEnd, column.WaterStart - 1);
        }

        private static void GetBoundaryNeighbor(
            ulong[] opaquePlane,
            ushort[] transparentPlane,
            int index,
            out bool opaque,
            out ushort blockId)
        {
            opaque = PlaneBit(opaquePlane, index);
            blockId = !opaque && transparentPlane is not null &&
                (uint)index < (uint)transparentPlane.Length
                    ? transparentPlane[index]
                    : (ushort)0;
        }

        private static bool FaceVisible(
            ushort sourceBlockId,
            bool neighborOpaque,
            ushort neighborBlockId)
        {
            if (TerrainLoader.IsOpaque(sourceBlockId))
                return !neighborOpaque;
            if (neighborOpaque)
                return false;
            return neighborBlockId == 0 || neighborBlockId != sourceBlockId;
        }

        private struct GeneratedFaceWriter
        {
            private readonly GeneratedChunkSpanData source;
            private readonly uint[]? faceTiles;
            private int opaqueWriteIndex;
            private int transparentWriteIndex;

            public GeneratedFaceWriter(
                GeneratedChunkSpanData source,
                BlockTextureAtlas atlas)
            {
                this.source = source;
                faceTiles = null;
                OpaqueCount = 0;
                TransparentCount = 0;
                OpaqueOffsets = null;
                OpaqueTileIndices = null;
                OpaqueDirections = null;
                TransparentOffsets = null;
                TransparentTileIndices = null;
                TransparentDirections = null;
                opaqueWriteIndex = 0;
                transparentWriteIndex = 0;
            }

            public GeneratedFaceWriter(
                GeneratedChunkSpanData source,
                BlockTextureAtlas atlas,
                int opaqueCount,
                int transparentCount)
            {
                this.source = source;
                faceTiles = BuildFaceTiles(source, atlas);
                OpaqueCount = opaqueCount;
                TransparentCount = transparentCount;
                OpaqueOffsets = new byte[checked(opaqueCount * 3)];
                OpaqueTileIndices = new uint[opaqueCount];
                OpaqueDirections = new byte[opaqueCount];
                TransparentOffsets = new byte[checked(transparentCount * 3)];
                TransparentTileIndices = new uint[transparentCount];
                TransparentDirections = new byte[transparentCount];
                opaqueWriteIndex = 0;
                transparentWriteIndex = 0;
            }

            public int OpaqueCount { get; private set; }

            public int TransparentCount { get; private set; }

            public byte[]? OpaqueOffsets { get; }

            public uint[]? OpaqueTileIndices { get; }

            public byte[]? OpaqueDirections { get; }

            public byte[]? TransparentOffsets { get; }

            public uint[]? TransparentTileIndices { get; }

            public byte[]? TransparentDirections { get; }

            public void Emit(
                ushort blockId,
                byte direction,
                int x,
                int y,
                int z)
            {
                if (TerrainLoader.IsOpaque(blockId))
                {
                    if (OpaqueOffsets is null)
                    {
                        OpaqueCount++;
                        return;
                    }

                    Write(
                        blockId,
                        direction,
                        x,
                        y,
                        z,
                        OpaqueOffsets,
                        OpaqueTileIndices!,
                        OpaqueDirections!,
                        opaqueWriteIndex++);
                    return;
                }

                if (TransparentOffsets is null)
                {
                    TransparentCount++;
                    return;
                }

                Write(
                    blockId,
                    direction,
                    x,
                    y,
                    z,
                    TransparentOffsets,
                    TransparentTileIndices!,
                    TransparentDirections!,
                    transparentWriteIndex++);
            }

            public void EmitYRange(
                ushort blockId,
                byte direction,
                int x,
                int startY,
                int endY,
                int z)
            {
                int count = endY - startY + 1;
                if (TerrainLoader.IsOpaque(blockId) && OpaqueOffsets is null)
                {
                    OpaqueCount = checked(OpaqueCount + count);
                    return;
                }
                if (!TerrainLoader.IsOpaque(blockId) && TransparentOffsets is null)
                {
                    TransparentCount = checked(TransparentCount + count);
                    return;
                }

                for (int y = startY; y <= endY; y++)
                    Emit(blockId, direction, x, y, z);
            }

            public void ValidateCompletion()
            {
                if (opaqueWriteIndex != OpaqueCount ||
                    transparentWriteIndex != TransparentCount)
                {
                    throw new InvalidOperationException(
                        "Generated face counts changed between count and write passes.");
                }
            }

            private void Write(
                ushort blockId,
                byte direction,
                int x,
                int y,
                int z,
                byte[] offsets,
                uint[] tileIndices,
                byte[] directions,
                int index)
            {
                int offsetIndex = index * 3;
                offsets[offsetIndex] = checked((byte)x);
                offsets[offsetIndex + 1] = checked((byte)y);
                offsets[offsetIndex + 2] = checked((byte)z);
                tileIndices[index] = GetFaceTile(blockId, direction);
                directions[index] = direction;
            }

            private uint GetFaceTile(ushort blockId, byte direction)
            {
                int materialIndex = blockId == source.StoneBlockId
                    ? 0
                    : blockId == source.SoilBlockId
                        ? 1
                        : blockId == source.WaterBlockId
                            ? 2
                            : throw new InvalidOperationException(
                                "Generated face uses an unknown block identifier.");
                return faceTiles![materialIndex * 6 + direction];
            }

            private static uint[] BuildFaceTiles(
                GeneratedChunkSpanData source,
                BlockTextureAtlas atlas)
            {
                ushort[] blockIds =
                {
                    source.StoneBlockId,
                    source.SoilBlockId,
                    source.WaterBlockId
                };
                var result = new uint[18];
                for (int material = 0; material < blockIds.Length; material++)
                {
                    for (byte direction = 0; direction < 6; direction++)
                    {
                        result[material * 6 + direction] = ComputeTileIndex(
                            atlas,
                            blockIds[material],
                            (Faces)direction);
                    }
                }

                return result;
            }
        }
    }
}

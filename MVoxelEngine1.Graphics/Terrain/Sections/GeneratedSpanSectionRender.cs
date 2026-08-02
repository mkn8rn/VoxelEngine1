using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Buffers;
using System.Collections.Generic;

namespace MVoxelEngine1.Graphics.Terrain.Sections
{
    internal partial class SectionRender
    {
        private FaceRectangleMeshData BuildGeneratedSpanRectangles()
        {
            GeneratedChunkSpanData source = data.GeneratedSpans ??
                throw new InvalidOperationException("Generated span data is not available.");

            bool recordPerformance = StartupPerformanceRecorder.IsRunning;
            long phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            int horizontalCellCount = checked(source.Width * source.Depth);
            int[] bottomFaces = ArrayPool<int>.Shared.Rent(horizontalCellCount);
            int[] topFaces = ArrayPool<int>.Shared.Rent(horizontalCellCount);
            var writer = new GeneratedFaceRectangleWriter(source, atlas);
            long preparationTicks = recordPerformance
                ? MeshPerformanceRecorder.GetElapsedTicks(phaseStart)
                : 0;
            FaceRectangleMeshData result;
            phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            try
            {
                for (int material = 0; material < 3; material++)
                {
                    Array.Fill(bottomFaces, -1, 0, horizontalCellCount);
                    Array.Fill(topFaces, -1, 0, horizontalCellCount);
                    ushort blockId = GetGeneratedMaterialBlockId(
                        source,
                        material);
                    GenerateGeneratedMaterial(
                        source,
                        material,
                        blockId,
                        bottomFaces,
                        topFaces,
                        ref writer);
                    EmitGeneratedHorizontalRectangles(
                        blockId,
                        2,
                        bottomFaces,
                        source.Width,
                        source.Depth,
                        ref writer);
                    EmitGeneratedHorizontalRectangles(
                        blockId,
                        3,
                        topFaces,
                        source.Width,
                        source.Depth,
                        ref writer);
                }

                result = writer.Complete();
            }
            finally
            {
                ArrayPool<int>.Shared.Return(bottomFaces);
                ArrayPool<int>.Shared.Return(topFaces);
            }

            long writePassTicks = recordPerformance
                ? MeshPerformanceRecorder.GetElapsedTicks(phaseStart)
                : 0;
            if (recordPerformance)
            {
                MeshPerformanceRecorder.RecordGeneratedSpanPhases(
                    countPassTicks: 0,
                    preparationTicks,
                    writePassTicks,
                    result.OpaqueFaceCount,
                    result.TransparentFaceCount,
                    result.OpaqueRectangleCount,
                    result.TransparentRectangleCount);
            }

            return result;
        }

        private void GenerateGeneratedMaterial(
            GeneratedChunkSpanData source,
            int material,
            ushort blockId,
            int[] bottomFaces,
            int[] topFaces,
            ref GeneratedFaceRectangleWriter writer)
        {
            for (int x = 0; x < source.Width; x++)
            {
                for (int z = 0; z < source.Depth; z++)
                {
                    ref readonly BlockColumnProfile column =
                        ref source.Columns[x * source.Depth + z];
                    GetGeneratedMaterialInterval(
                        column,
                        material,
                        out int intervalStart,
                        out int intervalEnd);
                    GenerateGeneratedIntervalRectangles(
                        source,
                        column,
                        blockId,
                        intervalStart,
                        intervalEnd,
                        x,
                        z,
                        bottomFaces,
                        topFaces,
                        ref writer);
                }
            }
        }

        private static ushort GetGeneratedMaterialBlockId(
            GeneratedChunkSpanData source,
            int material) => material switch
            {
                0 => source.StoneBlockId,
                1 => source.SoilBlockId,
                2 => source.WaterBlockId,
                _ => throw new ArgumentOutOfRangeException(nameof(material))
            };

        private static void GetGeneratedMaterialInterval(
            in BlockColumnProfile column,
            int material,
            out int intervalStart,
            out int intervalEnd)
        {
            switch (material)
            {
                case 0:
                    intervalStart = column.StoneStart;
                    intervalEnd = column.StoneEnd;
                    return;
                case 1:
                    intervalStart = column.SoilStart;
                    intervalEnd = column.SoilEnd;
                    return;
                case 2:
                    intervalStart = column.WaterStart;
                    intervalEnd = column.WaterEnd;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(material));
            }
        }

        private void GenerateGeneratedIntervalRectangles(
            GeneratedChunkSpanData source,
            in BlockColumnProfile column,
            ushort blockId,
            int intervalStart,
            int intervalEnd,
            int x,
            int z,
            int[] bottomFaces,
            int[] topFaces,
            ref GeneratedFaceRectangleWriter writer)
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
                    bottomFaces[horizontalIndex] = localStart;
            }
            else
            {
                ushort neighborId = source.GetBlockWorld(column, worldStart - 1);
                if (FaceVisible(
                    blockId,
                    TerrainLoader.IsOpaque(neighborId),
                    neighborId))
                {
                    bottomFaces[horizontalIndex] = localStart;
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
                    topFaces[horizontalIndex] = localEnd;
            }
            else
            {
                ushort neighborId = source.GetBlockWorld(column, worldEnd + 1);
                if (FaceVisible(
                    blockId,
                    TerrainLoader.IsOpaque(neighborId),
                    neighborId))
                {
                    topFaces[horizontalIndex] = localEnd;
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

        private static void EmitGeneratedHorizontalRectangles(
            ushort blockId,
            byte direction,
            int[] faceHeights,
            int width,
            int depth,
            ref GeneratedFaceRectangleWriter writer)
        {
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int index = x * depth + z;
                    int y = faceHeights[index];
                    if (y < 0)
                        continue;

                    int extentX = 1;
                    while (x + extentX < width &&
                           faceHeights[(x + extentX) * depth + z] == y)
                    {
                        extentX++;
                    }

                    int extentZ = 1;
                    while (z + extentZ < depth)
                    {
                        bool rowMatches = true;
                        for (int offsetX = 0; offsetX < extentX; offsetX++)
                        {
                            if (faceHeights[
                                    (x + offsetX) * depth + z + extentZ] != y)
                            {
                                rowMatches = false;
                                break;
                            }
                        }

                        if (!rowMatches)
                            break;
                        extentZ++;
                    }

                    for (int offsetX = 0; offsetX < extentX; offsetX++)
                    {
                        int row = (x + offsetX) * depth + z;
                        Array.Fill(faceHeights, -2, row, extentZ);
                    }

                    int anchorZ = direction == 3
                        ? z + extentZ - 1
                        : z;
                    writer.EmitRectangle(
                        blockId,
                        direction,
                        x,
                        y,
                        anchorZ,
                        extentX,
                        extentZ);
                }
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
            ref GeneratedFaceRectangleWriter writer)
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
            ref GeneratedFaceRectangleWriter writer)
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

        private struct GeneratedFaceRectangleWriter
        {
            private readonly GeneratedChunkSpanData source;
            private readonly uint[] faceTiles;
            private readonly List<uint> opaqueRectangles;
            private readonly List<uint> transparentRectangles;

            public GeneratedFaceRectangleWriter(
                GeneratedChunkSpanData source,
                BlockTextureAtlas atlas)
            {
                this.source = source;
                faceTiles = BuildFaceTiles(source, atlas);
                opaqueRectangles = new List<uint>(12_288);
                transparentRectangles = new List<uint>(1_536);
                OpaqueFaceCount = 0;
                TransparentFaceCount = 0;
            }

            public int OpaqueFaceCount { get; private set; }

            public int TransparentFaceCount { get; private set; }

            public void EmitRectangle(
                ushort blockId,
                byte direction,
                int x,
                int y,
                int z,
                int extentU,
                int extentV)
            {
                uint position = PackedFaceRectangle.PackPosition(
                    x,
                    y,
                    z,
                    direction);
                uint tileIndex = GetFaceTile(blockId, direction);
                uint attributes = PackedFaceRectangle.PackAttributes(
                    extentU,
                    extentV,
                    tileIndex);
                int logicalFaceCount = checked(extentU * extentV);
                List<uint> destination;
                if (TerrainLoader.IsOpaque(blockId))
                {
                    OpaqueFaceCount = checked(
                        OpaqueFaceCount + logicalFaceCount);
                    destination = opaqueRectangles;
                }
                else
                {
                    TransparentFaceCount = checked(
                        TransparentFaceCount + logicalFaceCount);
                    destination = transparentRectangles;
                }

                destination.Add(position);
                destination.Add(attributes);
            }

            public void EmitYRange(
                ushort blockId,
                byte direction,
                int x,
                int startY,
                int endY,
                int z)
            {
                EmitRectangle(
                    blockId,
                    direction,
                    x,
                    startY,
                    z,
                    1,
                    endY - startY + 1);
            }

            public FaceRectangleMeshData Complete()
            {
                uint[] opaque = opaqueRectangles.ToArray();
                uint[] transparent = transparentRectangles.ToArray();
                return new FaceRectangleMeshData(
                    OpaqueFaceCount,
                    opaque,
                    TransparentFaceCount,
                    transparent);
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
                return faceTiles[materialIndex * 6 + direction];
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

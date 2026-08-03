using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Buffers;
using Supprocom.NativeAllocationManagement;
using System.Runtime.CompilerServices;

namespace MVoxelEngine1.Graphics.Terrain.Sections
{
    internal partial class SectionRender
    {
        private FaceRectangleMeshData BuildGeneratedSpanRectangles(
            NativePool<uint> nativePool,
            PackedFaceStagingWorkspace stagingWorkspace)
        {
            GeneratedChunkSpanData source = data.GeneratedSpans ??
                throw new InvalidOperationException("Generated span data is not available.");
            if ((uint)(source.Width - 1) > byte.MaxValue ||
                (uint)(source.Height - 1) > byte.MaxValue ||
                (uint)(source.Depth - 1) > byte.MaxValue)
            {
                throw new InvalidDataException(
                    "Generated span dimensions exceed the packed face format.");
            }

            bool recordPerformance = StartupPerformanceRecorder.IsRunning;
            long phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            int horizontalCellCount = checked(source.Width * source.Depth);
            int[] bottomFaces = ArrayPool<int>.Shared.Rent(horizontalCellCount);
            int[] topFaces = ArrayPool<int>.Shared.Rent(horizontalCellCount);
            var materials = new GeneratedMaterialRuntime(source);
            var writer = new GeneratedFaceRectangleWriter(
                source,
                atlas,
                stagingWorkspace);
            bool directInteriorSides =
                source.OrderedContiguousSpans &&
                materials.SupportsContiguousTerrainFastPath;
            long preparationTicks = recordPerformance
                ? MeshPerformanceRecorder.GetElapsedTicks(phaseStart)
                : 0;
            FaceRectangleMeshData result;
            phaseStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            try
            {
                if (directInteriorSides)
                {
                    EmitContiguousInteriorSideRectangles(
                        source,
                        ref writer);
                }

                for (int material = 0; material < 3; material++)
                {
                    if ((source.MaterialMask & (1 << material)) == 0)
                        continue;
                    Array.Fill(bottomFaces, -1, 0, horizontalCellCount);
                    Array.Fill(topFaces, -1, 0, horizontalCellCount);
                    ushort blockId = materials.GetBlockId(material);
                    bool blockOpaque = materials.IsOpaque(material);
                    writer.SelectMaterial(material, blockOpaque);
                    GenerateGeneratedMaterial(
                        source,
                        materials,
                        material,
                        blockId,
                        blockOpaque,
                        !directInteriorSides,
                        bottomFaces,
                        topFaces,
                        ref writer);
                    EmitGeneratedHorizontalRectangles(
                        2,
                        bottomFaces,
                        source.Width,
                        source.Depth,
                        ref writer);
                    EmitGeneratedHorizontalRectangles(
                        3,
                        topFaces,
                        source.Width,
                        source.Depth,
                        ref writer);
                }

                writer.CommitBuffers();
                using NativeBuilder<uint> opaqueRectangles =
                    nativePool.CreateBuilder(writer.OpaqueWordCount);
                using NativeBuilder<uint> transparentRectangles =
                    nativePool.CreateBuilder(writer.TransparentWordCount);
                if (writer.OpaqueWordCount != 0)
                    opaqueRectangles.Append(writer.OpaqueWords);
                if (writer.TransparentWordCount != 0)
                    transparentRectangles.Append(writer.TransparentWords);

                NativeTransfer<uint>? opaque = null;
                NativeTransfer<uint>? transparent = null;
                try
                {
                    opaque = opaqueRectangles.Complete();
                    transparent = transparentRectangles.Complete();
                    result = new FaceRectangleMeshData(
                        writer.OpaqueFaceCount,
                        NativeTransfer<uint>.Move(ref opaque),
                        writer.TransparentFaceCount,
                        NativeTransfer<uint>.Move(ref transparent));
                }
                finally
                {
                    opaque?.Dispose();
                    transparent?.Dispose();
                }
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
            in GeneratedMaterialRuntime materials,
            int material,
            ushort blockId,
            bool blockOpaque,
            bool emitInteriorSides,
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
                        materials,
                        column,
                        blockId,
                        blockOpaque,
                        emitInteriorSides,
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
            in GeneratedMaterialRuntime materials,
            in BlockColumnProfile column,
            ushort blockId,
            bool blockOpaque,
            bool emitInteriorSides,
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
                if (FaceVisible(
                    blockOpaque,
                    blockId,
                    neighborOpaque,
                    neighborId))
                    bottomFaces[horizontalIndex] = localStart;
            }
            else
            {
                GetGeneratedBlock(
                    materials,
                    column,
                    worldStart - 1,
                    out ushort neighborId,
                    out bool neighborOpaque);
                if (FaceVisible(
                    blockOpaque,
                    blockId,
                    neighborOpaque,
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
                if (FaceVisible(
                    blockOpaque,
                    blockId,
                    neighborOpaque,
                    neighborId))
                    topFaces[horizontalIndex] = localEnd;
            }
            else
            {
                GetGeneratedBlock(
                    materials,
                    column,
                    worldEnd + 1,
                    out ushort neighborId,
                    out bool neighborOpaque);
                if (FaceVisible(
                    blockOpaque,
                    blockId,
                    neighborOpaque,
                    neighborId))
                {
                    topFaces[horizontalIndex] = localEnd;
                }
            }

            if (x == 0)
            {
                EmitGeneratedBoundaryRange(
                    blockId,
                    blockOpaque,
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
            else if (emitInteriorSides)
            {
                EmitGeneratedColumnRange(
                    source,
                    materials,
                    source.Columns[(x - 1) * source.Depth + z],
                    blockId,
                    blockOpaque,
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
                    blockOpaque,
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
            else if (emitInteriorSides)
            {
                EmitGeneratedColumnRange(
                    source,
                    materials,
                    source.Columns[(x + 1) * source.Depth + z],
                    blockId,
                    blockOpaque,
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
                    blockOpaque,
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
            else if (emitInteriorSides)
            {
                EmitGeneratedColumnRange(
                    source,
                    materials,
                    source.Columns[x * source.Depth + z - 1],
                    blockId,
                    blockOpaque,
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
                    blockOpaque,
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
            else if (emitInteriorSides)
            {
                EmitGeneratedColumnRange(
                    source,
                    materials,
                    source.Columns[x * source.Depth + z + 1],
                    blockId,
                    blockOpaque,
                    5,
                    x,
                    z,
                    worldStart,
                    worldEnd,
                    ref writer);
            }
        }

        private static void EmitContiguousInteriorSideRectangles(
            GeneratedChunkSpanData source,
            ref GeneratedFaceRectangleWriter writer)
        {
            for (int x = 0; x < source.Width - 1; x++)
            {
                int leftBase = x * source.Depth;
                int rightBase = leftBase + source.Depth;
                for (int z = 0; z < source.Depth; z++)
                {
                    ref readonly BlockColumnProfile left =
                        ref source.Columns[leftBase + z];
                    ref readonly BlockColumnProfile right =
                        ref source.Columns[rightBase + z];
                    EmitContiguousColumnSide(
                        source,
                        left,
                        right,
                        1,
                        x,
                        z,
                        ref writer);
                    EmitContiguousColumnSide(
                        source,
                        right,
                        left,
                        0,
                        x + 1,
                        z,
                        ref writer);
                }
            }

            for (int x = 0; x < source.Width; x++)
            {
                int columnBase = x * source.Depth;
                for (int z = 0; z < source.Depth - 1; z++)
                {
                    ref readonly BlockColumnProfile negative =
                        ref source.Columns[columnBase + z];
                    ref readonly BlockColumnProfile positive =
                        ref source.Columns[columnBase + z + 1];
                    EmitContiguousColumnSide(
                        source,
                        negative,
                        positive,
                        5,
                        x,
                        z,
                        ref writer);
                    EmitContiguousColumnSide(
                        source,
                        positive,
                        negative,
                        4,
                        x,
                        z + 1,
                        ref writer);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitContiguousColumnSide(
            GeneratedChunkSpanData source,
            in BlockColumnProfile sourceColumn,
            in BlockColumnProfile neighborColumn,
            byte direction,
            int x,
            int z,
            ref GeneratedFaceRectangleWriter writer)
        {
            int chunkStart = source.ChunkBaseY;
            int chunkEnd = chunkStart + source.Height - 1;
            if ((source.MaterialMask & 0b011) != 0 &&
                TryGetGroundRange(sourceColumn, out int groundStart, out int groundEnd))
            {
                groundStart = Math.Max(groundStart, chunkStart);
                groundEnd = Math.Min(groundEnd, chunkEnd);
                if (groundStart <= groundEnd)
                {
                    bool neighborHasGround = TryGetGroundRange(
                        neighborColumn,
                        out int neighborStart,
                        out int neighborEnd);
                    EmitGroundDifference(
                        source,
                        sourceColumn,
                        groundStart,
                        groundEnd,
                        neighborHasGround,
                        neighborStart,
                        neighborEnd,
                        direction,
                        x,
                        z,
                        ref writer);
                }
            }

            if ((source.MaterialMask & 0b100) == 0 ||
                sourceColumn.WaterStart < 0 ||
                sourceColumn.WaterEnd < sourceColumn.WaterStart)
            {
                return;
            }

            int waterStart = Math.Max(sourceColumn.WaterStart, chunkStart);
            int waterEnd = Math.Min(sourceColumn.WaterEnd, chunkEnd);
            if (waterStart > waterEnd)
                return;
            bool neighborOccupied = TryGetOccupiedRange(
                neighborColumn,
                out int occupiedStart,
                out int occupiedEnd);
            EmitMaterialDifference(
                waterStart,
                waterEnd,
                neighborOccupied,
                occupiedStart,
                occupiedEnd,
                2,
                false,
                source,
                direction,
                x,
                z,
                ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetGroundRange(
            in BlockColumnProfile column,
            out int start,
            out int end)
        {
            bool hasStone = column.StoneStart >= 0 &&
                column.StoneEnd >= column.StoneStart;
            bool hasSoil = column.SoilStart >= 0 &&
                column.SoilEnd >= column.SoilStart;
            if (!hasStone && !hasSoil)
            {
                start = 0;
                end = -1;
                return false;
            }

            start = hasStone ? column.StoneStart : column.SoilStart;
            end = hasSoil ? column.SoilEnd : column.StoneEnd;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetOccupiedRange(
            in BlockColumnProfile column,
            out int start,
            out int end)
        {
            bool hasGround = TryGetGroundRange(column, out start, out end);
            bool hasWater = column.WaterStart >= 0 &&
                column.WaterEnd >= column.WaterStart;
            if (!hasWater)
                return hasGround;
            if (!hasGround)
                start = column.WaterStart;
            end = column.WaterEnd;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitGroundDifference(
            GeneratedChunkSpanData source,
            in BlockColumnProfile sourceColumn,
            int sourceStart,
            int sourceEnd,
            bool neighborPresent,
            int neighborStart,
            int neighborEnd,
            byte direction,
            int x,
            int z,
            ref GeneratedFaceRectangleWriter writer)
        {
            if (!neighborPresent ||
                neighborEnd < sourceStart ||
                neighborStart > sourceEnd)
            {
                EmitGroundSegment(
                    source,
                    sourceColumn,
                    sourceStart,
                    sourceEnd,
                    direction,
                    x,
                    z,
                    ref writer);
                return;
            }

            if (sourceStart < neighborStart)
            {
                EmitGroundSegment(
                    source,
                    sourceColumn,
                    sourceStart,
                    neighborStart - 1,
                    direction,
                    x,
                    z,
                    ref writer);
            }
            if (sourceEnd > neighborEnd)
            {
                EmitGroundSegment(
                    source,
                    sourceColumn,
                    neighborEnd + 1,
                    sourceEnd,
                    direction,
                    x,
                    z,
                    ref writer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitGroundSegment(
            GeneratedChunkSpanData source,
            in BlockColumnProfile column,
            int segmentStart,
            int segmentEnd,
            byte direction,
            int x,
            int z,
            ref GeneratedFaceRectangleWriter writer)
        {
            if ((source.MaterialMask & 0b001) != 0)
            {
                int start = Math.Max(segmentStart, column.StoneStart);
                int end = Math.Min(segmentEnd, column.StoneEnd);
                if (start <= end)
                {
                    writer.EmitMaterialYRange(
                        0,
                        true,
                        direction,
                        x,
                        start - source.ChunkBaseY,
                        end - source.ChunkBaseY,
                        z);
                }
            }

            if ((source.MaterialMask & 0b010) == 0)
                return;
            int soilStart = Math.Max(segmentStart, column.SoilStart);
            int soilEnd = Math.Min(segmentEnd, column.SoilEnd);
            if (soilStart <= soilEnd)
            {
                writer.EmitMaterialYRange(
                    1,
                    true,
                    direction,
                    x,
                    soilStart - source.ChunkBaseY,
                    soilEnd - source.ChunkBaseY,
                    z);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitMaterialDifference(
            int sourceStart,
            int sourceEnd,
            bool neighborPresent,
            int neighborStart,
            int neighborEnd,
            int material,
            bool opaque,
            GeneratedChunkSpanData source,
            byte direction,
            int x,
            int z,
            ref GeneratedFaceRectangleWriter writer)
        {
            if (!neighborPresent ||
                neighborEnd < sourceStart ||
                neighborStart > sourceEnd)
            {
                writer.EmitMaterialYRange(
                    material,
                    opaque,
                    direction,
                    x,
                    sourceStart - source.ChunkBaseY,
                    sourceEnd - source.ChunkBaseY,
                    z);
                return;
            }

            if (sourceStart < neighborStart)
            {
                writer.EmitMaterialYRange(
                    material,
                    opaque,
                    direction,
                    x,
                    sourceStart - source.ChunkBaseY,
                    neighborStart - source.ChunkBaseY - 1,
                    z);
            }
            if (sourceEnd > neighborEnd)
            {
                writer.EmitMaterialYRange(
                    material,
                    opaque,
                    direction,
                    x,
                    neighborEnd - source.ChunkBaseY + 1,
                    sourceEnd - source.ChunkBaseY,
                    z);
            }
        }

        private static void EmitGeneratedHorizontalRectangles(
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
            in GeneratedMaterialRuntime materials,
            in BlockColumnProfile neighborColumn,
            ushort blockId,
            bool blockOpaque,
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
                    materials,
                    neighborColumn,
                    current,
                    worldEnd,
                    out ushort neighborId,
                    out bool neighborOpaque,
                    out int runEnd);
                if (FaceVisible(
                    blockOpaque,
                    blockId,
                    neighborOpaque,
                    neighborId))
                {
                    writer.EmitYRange(
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
            bool blockOpaque,
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
                bool visible = FaceVisible(
                    blockOpaque,
                    blockId,
                    neighborOpaque,
                    neighborId);
                if (visible && visibleStart < 0)
                {
                    visibleStart = y;
                }
                else if (!visible && visibleStart >= 0)
                {
                    writer.EmitYRange(
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
                    direction,
                    x,
                    visibleStart,
                    localEnd,
                    z);
            }
        }

        private static void GetGeneratedNeighborRun(
            in GeneratedMaterialRuntime materials,
            in BlockColumnProfile column,
            int worldY,
            int maximumWorldY,
            out ushort blockId,
            out bool blockOpaque,
            out int runEnd)
        {
            if (column.StoneStart >= 0 &&
                column.StoneEnd >= column.StoneStart &&
                worldY >= column.StoneStart &&
                worldY <= column.StoneEnd)
            {
                blockId = materials.StoneBlockId;
                blockOpaque = materials.StoneOpaque;
                runEnd = Math.Min(column.StoneEnd, maximumWorldY);
                return;
            }
            if (column.SoilStart >= 0 &&
                column.SoilEnd >= column.SoilStart &&
                worldY >= column.SoilStart &&
                worldY <= column.SoilEnd)
            {
                blockId = materials.SoilBlockId;
                blockOpaque = materials.SoilOpaque;
                runEnd = Math.Min(column.SoilEnd, maximumWorldY);
                return;
            }
            if (column.WaterStart >= 0 &&
                column.WaterEnd >= column.WaterStart &&
                worldY >= column.WaterStart &&
                worldY <= column.WaterEnd)
            {
                blockId = materials.WaterBlockId;
                blockOpaque = materials.WaterOpaque;
                runEnd = Math.Min(column.WaterEnd, maximumWorldY);
                return;
            }

            blockId = 0;
            blockOpaque = false;
            runEnd = maximumWorldY;
            if (column.StoneStart >= 0 && column.StoneStart > worldY)
                runEnd = Math.Min(runEnd, column.StoneStart - 1);
            if (column.SoilStart >= 0 && column.SoilStart > worldY)
                runEnd = Math.Min(runEnd, column.SoilStart - 1);
            if (column.WaterStart >= 0 && column.WaterStart > worldY)
                runEnd = Math.Min(runEnd, column.WaterStart - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetGeneratedBlock(
            in GeneratedMaterialRuntime materials,
            in BlockColumnProfile column,
            int worldY,
            out ushort blockId,
            out bool blockOpaque)
        {
            if (column.StoneStart >= 0 &&
                column.StoneEnd >= column.StoneStart &&
                worldY >= column.StoneStart &&
                worldY <= column.StoneEnd)
            {
                blockId = materials.StoneBlockId;
                blockOpaque = materials.StoneOpaque;
                return;
            }
            if (column.SoilStart >= 0 &&
                column.SoilEnd >= column.SoilStart &&
                worldY >= column.SoilStart &&
                worldY <= column.SoilEnd)
            {
                blockId = materials.SoilBlockId;
                blockOpaque = materials.SoilOpaque;
                return;
            }
            if (column.WaterStart >= 0 &&
                column.WaterEnd >= column.WaterStart &&
                worldY >= column.WaterStart &&
                worldY <= column.WaterEnd)
            {
                blockId = materials.WaterBlockId;
                blockOpaque = materials.WaterOpaque;
                return;
            }

            blockId = 0;
            blockOpaque = false;
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
            bool sourceOpaque,
            ushort sourceBlockId,
            bool neighborOpaque,
            ushort neighborBlockId)
        {
            if (sourceOpaque)
                return !neighborOpaque;
            if (neighborOpaque)
                return false;
            return neighborBlockId == 0 || neighborBlockId != sourceBlockId;
        }

        private readonly struct GeneratedMaterialRuntime
        {
            public GeneratedMaterialRuntime(GeneratedChunkSpanData source)
            {
                StoneBlockId = source.StoneBlockId;
                SoilBlockId = source.SoilBlockId;
                WaterBlockId = source.WaterBlockId;
                StoneOpaque = TerrainLoader.IsOpaque(StoneBlockId);
                SoilOpaque = TerrainLoader.IsOpaque(SoilBlockId);
                WaterOpaque = TerrainLoader.IsOpaque(WaterBlockId);
            }

            public ushort StoneBlockId { get; }

            public ushort SoilBlockId { get; }

            public ushort WaterBlockId { get; }

            public bool StoneOpaque { get; }

            public bool SoilOpaque { get; }

            public bool WaterOpaque { get; }

            public bool SupportsContiguousTerrainFastPath =>
                StoneBlockId != 0 &&
                SoilBlockId != 0 &&
                WaterBlockId != 0 &&
                StoneOpaque &&
                SoilOpaque &&
                !WaterOpaque;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ushort GetBlockId(int material) => material switch
            {
                0 => StoneBlockId,
                1 => SoilBlockId,
                2 => WaterBlockId,
                _ => throw new ArgumentOutOfRangeException(nameof(material))
            };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsOpaque(int material) => material switch
            {
                0 => StoneOpaque,
                1 => SoilOpaque,
                2 => WaterOpaque,
                _ => throw new ArgumentOutOfRangeException(nameof(material))
            };
        }

        private struct GeneratedFaceRectangleWriter
        {
            private readonly uint[] faceTileAttributes;
            private readonly PackedFaceStagingWorkspace stagingWorkspace;
            private uint[] opaqueWords;
            private uint[] transparentWords;
            private uint[] currentWords;
            private int opaqueWordCount;
            private int transparentWordCount;
            private int currentWordCount;
            private int opaqueFaceCount;
            private int transparentFaceCount;
            private int currentFaceCount;
            private int currentTileOffset;
            private bool currentOpaque;
            private bool materialSelected;

            public GeneratedFaceRectangleWriter(
                GeneratedChunkSpanData source,
                BlockTextureAtlas atlas,
                PackedFaceStagingWorkspace stagingWorkspace)
            {
                faceTileAttributes = BuildFaceTileAttributes(source, atlas);
                this.stagingWorkspace = stagingWorkspace;
                opaqueWords = stagingWorkspace.OpaqueBuffer;
                transparentWords = stagingWorkspace.TransparentBuffer;
                currentWords = Array.Empty<uint>();
                opaqueWordCount = 0;
                transparentWordCount = 0;
                currentWordCount = 0;
                opaqueFaceCount = 0;
                transparentFaceCount = 0;
                currentFaceCount = 0;
                currentTileOffset = 0;
                currentOpaque = false;
                materialSelected = false;
            }

            public int OpaqueFaceCount => opaqueFaceCount;

            public int TransparentFaceCount => transparentFaceCount;

            public int OpaqueWordCount => opaqueWordCount;

            public int TransparentWordCount => transparentWordCount;

            public ReadOnlySpan<uint> OpaqueWords =>
                opaqueWords.AsSpan(0, opaqueWordCount);

            public ReadOnlySpan<uint> TransparentWords =>
                transparentWords.AsSpan(0, transparentWordCount);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EmitMaterialYRange(
                int material,
                bool opaque,
                byte direction,
                int x,
                int startY,
                int endY,
                int z)
            {
                uint position = (uint)x |
                    ((uint)startY << 8) |
                    ((uint)z << 16) |
                    ((uint)direction << 24);
                uint attributes =
                    ((uint)(endY - startY) << 8) |
                    faceTileAttributes[material * 6 + direction];
                int faceCount = endY - startY + 1;
                if (opaque)
                {
                    AppendMaterialRectangle(
                        ref opaqueWords,
                        ref opaqueWordCount,
                        ref opaqueFaceCount,
                        position,
                        attributes,
                        faceCount);
                }
                else
                {
                    AppendMaterialRectangle(
                        ref transparentWords,
                        ref transparentWordCount,
                        ref transparentFaceCount,
                        position,
                        attributes,
                        faceCount);
                }
            }

            public void SelectMaterial(int material, bool opaque)
            {
                if ((uint)material >= 3)
                    throw new ArgumentOutOfRangeException(nameof(material));
                CommitCurrentMaterial();
                currentTileOffset = material * 6;
                currentOpaque = opaque;
                currentWords = opaque ? opaqueWords : transparentWords;
                currentWordCount = opaque
                    ? opaqueWordCount
                    : transparentWordCount;
                currentFaceCount = opaque
                    ? opaqueFaceCount
                    : transparentFaceCount;
                materialSelected = true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EmitRectangle(
                byte direction,
                int x,
                int y,
                int z,
                int extentU,
                int extentV)
            {
                uint position = (uint)x |
                    ((uint)y << 8) |
                    ((uint)z << 16) |
                    ((uint)direction << 24);
                uint attributes = (uint)(extentU - 1) |
                    ((uint)(extentV - 1) << 8) |
                    faceTileAttributes[currentTileOffset + direction];
                currentFaceCount += extentU * extentV;
                int required = currentWordCount + 2;
                if (required > currentWords.Length)
                {
                    int nextLength = Math.Max(
                        required,
                        checked(currentWords.Length * 2));
                    Array.Resize(ref currentWords, nextLength);
                }

                currentWords[currentWordCount] = position;
                currentWords[currentWordCount + 1] = attributes;
                currentWordCount = required;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EmitYRange(
                byte direction,
                int x,
                int startY,
                int endY,
                int z)
            {
                EmitRectangle(
                    direction,
                    x,
                    startY,
                    z,
                    1,
                    endY - startY + 1);
            }

            public void CommitBuffers()
            {
                CommitCurrentMaterial();
                stagingWorkspace.Adopt(opaqueWords, transparentWords);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void AppendMaterialRectangle(
                ref uint[] words,
                ref int wordCount,
                ref int totalFaceCount,
                uint position,
                uint attributes,
                int faceCount)
            {
                int required = wordCount + 2;
                if (required > words.Length)
                {
                    int nextLength = Math.Max(
                        required,
                        checked(words.Length * 2));
                    Array.Resize(ref words, nextLength);
                }

                words[wordCount] = position;
                words[wordCount + 1] = attributes;
                wordCount = required;
                totalFaceCount += faceCount;
            }

            private void CommitCurrentMaterial()
            {
                if (!materialSelected)
                    return;
                if (currentOpaque)
                {
                    opaqueWords = currentWords;
                    opaqueWordCount = currentWordCount;
                    opaqueFaceCount = currentFaceCount;
                }
                else
                {
                    transparentWords = currentWords;
                    transparentWordCount = currentWordCount;
                    transparentFaceCount = currentFaceCount;
                }
            }

            private static uint[] BuildFaceTileAttributes(
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
                        uint tileIndex = ComputeTileIndex(
                            atlas,
                            blockIds[material],
                            (Faces)direction);
                        if (tileIndex > ushort.MaxValue)
                            throw new ArgumentOutOfRangeException(
                                nameof(tileIndex));
                        result[material * 6 + direction] = tileIndex << 16;
                    }
                }

                return result;
            }
        }
    }
}

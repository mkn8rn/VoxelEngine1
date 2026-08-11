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
                var state = new GeneratedFaceRectangleState(
                    source,
                    atlas,
                    stagingWorkspace);
                using NativeBuilder<uint> opaqueRectangles = new();
                opaqueRectangles.Borrow(
                    (scoped ref NativeBuilderBorrow<uint> opaqueOutput) =>
                    {
                        GenerateGeneratedSpanRectangleWords(
                            source,
                            materials,
                            directInteriorSides,
                            bottomFaces,
                            topFaces,
                            state,
                            ref opaqueOutput);
                    });
                state.CommitTransparentBuffer();
                using NativeBuilder<uint> transparentRectangles = new(
                    preLease: state.TransparentWordCount);
                if (state.TransparentWordCount != 0)
                    transparentRectangles.Append(state.TransparentWords);

                NativeTransfer<uint>? opaque = null;
                NativeTransfer<uint>? transparent = null;
                try
                {
                    opaque = opaqueRectangles.Complete();
                    transparent = transparentRectangles.Complete();
                    result = new FaceRectangleMeshData(
                        state.OpaqueFaceCount,
                        NativeTransfer<uint>.Move(ref opaque),
                        state.TransparentFaceCount,
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

        private void GenerateGeneratedSpanRectangleWords(
            GeneratedChunkSpanData source,
            GeneratedMaterialRuntime materials,
            bool directInteriorSides,
            int[] bottomFaces,
            int[] topFaces,
            GeneratedFaceRectangleState state,
            scoped ref NativeBuilderBorrow<uint> opaqueOutput)
        {
            int horizontalCellCount = checked(source.Width * source.Depth);
            if (directInteriorSides)
            {
                for (int x = 0; x < source.Width - 1; x++)
                {
                    WriteContiguousInteriorXRow(
                        source,
                        x,
                        state,
                        ref opaqueOutput);
                }

                for (int x = 0; x < source.Width; x++)
                {
                    WriteContiguousInteriorZRow(
                        source,
                        x,
                        state,
                        ref opaqueOutput);
                }
            }

            for (int material = 0; material < 3; material++)
            {
                if ((source.MaterialMask & (1 << material)) == 0)
                    continue;
                Array.Fill(bottomFaces, -1, 0, horizontalCellCount);
                Array.Fill(topFaces, -1, 0, horizontalCellCount);
                ushort blockId = materials.GetBlockId(material);
                bool blockOpaque = materials.IsOpaque(material);
                for (int x = 0; x < source.Width; x++)
                {
                    if (blockOpaque)
                    {
                        WriteGeneratedMaterialRow(
                            source,
                            materials,
                            material,
                            blockId,
                            !directInteriorSides,
                            x,
                            bottomFaces,
                            topFaces,
                            state,
                            ref opaqueOutput);
                    }
                    else
                    {
                        var writer = new GeneratedFaceRectangleWriter(state);
                        int opaqueWordCount = 0;
                        writer.SelectMaterial(material, opaque: false);
                        GenerateGeneratedMaterialRow(
                            source,
                            materials,
                            material,
                            blockId,
                            blockOpaque: false,
                            !directInteriorSides,
                            x,
                            bottomFaces,
                            topFaces,
                            Span<uint>.Empty,
                            ref opaqueWordCount,
                            ref writer);
                    }
                }

                WriteGeneratedHorizontalRectangles(
                    source,
                    material,
                    blockOpaque,
                    2,
                    bottomFaces,
                    state,
                    ref opaqueOutput);
                WriteGeneratedHorizontalRectangles(
                    source,
                    material,
                    blockOpaque,
                    3,
                    topFaces,
                    state,
                    ref opaqueOutput);
            }
        }

        private void GenerateGeneratedMaterialRow(
            GeneratedChunkSpanData source,
            GeneratedMaterialRuntime materials,
            int material,
            ushort blockId,
            bool blockOpaque,
            bool emitInteriorSides,
            int x,
            int[] bottomFaces,
            int[] topFaces,
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
            ref GeneratedFaceRectangleWriter writer)
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
                    opaqueWords,
                    ref opaqueWordCount,
                    ref writer);
            }
        }

        private void WriteGeneratedMaterialRow(
            GeneratedChunkSpanData source,
            GeneratedMaterialRuntime materials,
            int material,
            ushort blockId,
            bool emitInteriorSides,
            int x,
            int[] bottomFaces,
            int[] topFaces,
            GeneratedFaceRectangleState state,
            scoped ref NativeBuilderBorrow<uint> opaqueOutput)
        {
            if (!emitInteriorSides &&
                x > 0 &&
                x < source.Width - 1)
            {
                int maximumInteriorWordCount = checked(
                    4 * ((source.Height + 1) / 2));
                opaqueOutput.Write(
                    maximumInteriorWordCount,
                    batch =>
                    {
                        Span<uint> opaqueWords = batch.AsSpan();
                        int opaqueWordCount = 0;
                        var writer = new GeneratedFaceRectangleWriter(state);
                        writer.SelectMaterial(material, opaque: true);
                        GenerateGeneratedMaterialRow(
                            source,
                            materials,
                            material,
                            blockId,
                            blockOpaque: true,
                            emitInteriorSides: false,
                            x,
                            bottomFaces,
                            topFaces,
                            opaqueWords,
                            ref opaqueWordCount,
                            ref writer);
                        batch.Commit(opaqueWordCount);
                    });
                return;
            }

            int maximumWordCount = checked(
                source.Depth *
                4 *
                ((source.Height + 1) / 2) *
                2);
            uint[] opaqueWords = state.GetOpaqueBatchBuffer(maximumWordCount);
            int opaqueWordCount = 0;
            var stagedWriter = new GeneratedFaceRectangleWriter(state);
            stagedWriter.SelectMaterial(material, opaque: true);
            GenerateGeneratedMaterialRow(
                source,
                materials,
                material,
                blockId,
                blockOpaque: true,
                emitInteriorSides,
                x,
                bottomFaces,
                topFaces,
                opaqueWords,
                ref opaqueWordCount,
                ref stagedWriter);
            if (opaqueWordCount != 0)
            {
                opaqueOutput.Append(
                    opaqueWords.AsSpan(0, opaqueWordCount));
            }
        }

        private static void WriteGeneratedHorizontalRectangles(
            GeneratedChunkSpanData source,
            int material,
            bool opaque,
            byte direction,
            int[] faceHeights,
            GeneratedFaceRectangleState state,
            scoped ref NativeBuilderBorrow<uint> opaqueOutput)
        {
            if (!opaque)
            {
                var writer = new GeneratedFaceRectangleWriter(state);
                int opaqueWordCount = 0;
                writer.SelectMaterial(material, opaque: false);
                EmitGeneratedHorizontalRectangles(
                    direction,
                    faceHeights,
                    source.Width,
                    source.Depth,
                    Span<uint>.Empty,
                    ref opaqueWordCount,
                    ref writer);
                return;
            }

            int maximumWordCount = checked(
                source.Width * source.Depth * 2);
            uint[] opaqueWords = state.GetOpaqueBatchBuffer(maximumWordCount);
            int stagedWordCount = 0;
            var stagedWriter = new GeneratedFaceRectangleWriter(state);
            stagedWriter.SelectMaterial(material, opaque: true);
            EmitGeneratedHorizontalRectangles(
                direction,
                faceHeights,
                source.Width,
                source.Depth,
                opaqueWords,
                ref stagedWordCount,
                ref stagedWriter);
            if (stagedWordCount != 0)
            {
                opaqueOutput.Append(
                    opaqueWords.AsSpan(0, stagedWordCount));
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
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
                    ref writer);
            }
        }

        private static void WriteContiguousInteriorXRow(
            GeneratedChunkSpanData source,
            int x,
            GeneratedFaceRectangleState state,
            scoped ref NativeBuilderBorrow<uint> opaqueOutput)
        {
            int maximumWordCount = checked(source.Depth * 12 * 2);
            opaqueOutput.Write(
                maximumWordCount,
                batch =>
                {
                    Span<uint> opaqueWords = batch.AsSpan();
                    int opaqueWordCount = 0;
                    var writer = new GeneratedFaceRectangleWriter(state);
                    int leftBase = x * source.Depth;
                    int rightBase = leftBase + source.Depth;
                    for (int z = 0; z < source.Depth; z++)
                    {
                        ref readonly BlockColumnProfile left =
                            ref source.Columns[leftBase + z];
                        ref readonly BlockColumnProfile right =
                            ref source.Columns[rightBase + z];
                        EmitContiguousColumnPair(
                            source,
                            left,
                            right,
                            1,
                            x,
                            z,
                            0,
                            x + 1,
                            z,
                            opaqueWords,
                            ref opaqueWordCount,
                            ref writer);
                    }

                    batch.Commit(opaqueWordCount);
                });
        }

        private static void WriteContiguousInteriorZRow(
            GeneratedChunkSpanData source,
            int x,
            GeneratedFaceRectangleState state,
            scoped ref NativeBuilderBorrow<uint> opaqueOutput)
        {
            int maximumWordCount = checked(
                (source.Depth - 1) * 12 * 2);
            opaqueOutput.Write(
                maximumWordCount,
                batch =>
                {
                    Span<uint> opaqueWords = batch.AsSpan();
                    int opaqueWordCount = 0;
                    var writer = new GeneratedFaceRectangleWriter(state);
                    int columnBase = x * source.Depth;
                    for (int z = 0; z < source.Depth - 1; z++)
                    {
                        ref readonly BlockColumnProfile negative =
                            ref source.Columns[columnBase + z];
                        ref readonly BlockColumnProfile positive =
                            ref source.Columns[columnBase + z + 1];
                        EmitContiguousColumnPair(
                            source,
                            negative,
                            positive,
                            5,
                            x,
                            z,
                            4,
                            x,
                            z + 1,
                            opaqueWords,
                            ref opaqueWordCount,
                            ref writer);
                    }

                    batch.Commit(opaqueWordCount);
                });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitContiguousColumnPair(
            GeneratedChunkSpanData source,
            in BlockColumnProfile firstColumn,
            in BlockColumnProfile secondColumn,
            byte firstDirection,
            int firstX,
            int firstZ,
            byte secondDirection,
            int secondX,
            int secondZ,
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
            ref GeneratedFaceRectangleWriter writer)
        {
            int chunkStart = source.ChunkBaseY;
            int chunkEnd = chunkStart + source.Height - 1;
            bool firstHasGround = TryGetGroundRange(
                firstColumn,
                out int firstGroundStart,
                out int firstGroundEnd);
            bool secondHasGround = TryGetGroundRange(
                secondColumn,
                out int secondGroundStart,
                out int secondGroundEnd);

            if ((source.MaterialMask & 0b011) != 0)
            {
                if (firstHasGround &&
                    secondHasGround &&
                    firstGroundStart == secondGroundStart)
                {
                    if (firstGroundEnd > secondGroundEnd)
                    {
                        int start = Math.Max(secondGroundEnd + 1, chunkStart);
                        int end = Math.Min(firstGroundEnd, chunkEnd);
                        if (start <= end)
                        {
                            EmitGroundSegment(
                                source,
                                firstColumn,
                                start,
                                end,
                                firstDirection,
                                firstX,
                                firstZ,
                                opaqueWords,
                                ref opaqueWordCount,
                                ref writer);
                        }
                    }
                    else if (secondGroundEnd > firstGroundEnd)
                    {
                        int start = Math.Max(firstGroundEnd + 1, chunkStart);
                        int end = Math.Min(secondGroundEnd, chunkEnd);
                        if (start <= end)
                        {
                            EmitGroundSegment(
                                source,
                                secondColumn,
                                start,
                                end,
                                secondDirection,
                                secondX,
                                secondZ,
                                opaqueWords,
                                ref opaqueWordCount,
                                ref writer);
                        }
                    }
                }
                else
                {
                    if (firstHasGround)
                    {
                        int firstStart = Math.Max(firstGroundStart, chunkStart);
                        int firstEnd = Math.Min(firstGroundEnd, chunkEnd);
                        if (firstStart <= firstEnd)
                        {
                            EmitGroundDifference(
                                source,
                                firstColumn,
                                firstStart,
                                firstEnd,
                                secondHasGround,
                                secondGroundStart,
                                secondGroundEnd,
                                firstDirection,
                                firstX,
                                firstZ,
                                opaqueWords,
                                ref opaqueWordCount,
                                ref writer);
                        }
                    }

                    if (secondHasGround)
                    {
                        int secondStart = Math.Max(secondGroundStart, chunkStart);
                        int secondEnd = Math.Min(secondGroundEnd, chunkEnd);
                        if (secondStart <= secondEnd)
                        {
                            EmitGroundDifference(
                                source,
                                secondColumn,
                                secondStart,
                                secondEnd,
                                firstHasGround,
                                firstGroundStart,
                                firstGroundEnd,
                                secondDirection,
                                secondX,
                                secondZ,
                                opaqueWords,
                                ref opaqueWordCount,
                                ref writer);
                        }
                    }
                }
            }

            if ((source.MaterialMask & 0b100) == 0)
                return;

            bool firstHasWater = firstColumn.WaterStart >= 0 &&
                firstColumn.WaterEnd >= firstColumn.WaterStart;
            bool secondHasWater = secondColumn.WaterStart >= 0 &&
                secondColumn.WaterEnd >= secondColumn.WaterStart;
            bool firstOccupied = firstHasGround || firstHasWater;
            bool secondOccupied = secondHasGround || secondHasWater;
            int firstOccupiedStart = firstHasGround
                ? firstGroundStart
                : firstColumn.WaterStart;
            int firstOccupiedEnd = firstHasWater
                ? firstColumn.WaterEnd
                : firstGroundEnd;
            int secondOccupiedStart = secondHasGround
                ? secondGroundStart
                : secondColumn.WaterStart;
            int secondOccupiedEnd = secondHasWater
                ? secondColumn.WaterEnd
                : secondGroundEnd;

            if (firstHasWater)
            {
                int firstStart = Math.Max(firstColumn.WaterStart, chunkStart);
                int firstEnd = Math.Min(firstColumn.WaterEnd, chunkEnd);
                bool fullyCovered = secondOccupied &&
                    secondOccupiedStart <= firstStart &&
                    secondOccupiedEnd >= firstEnd;
                if (firstStart <= firstEnd && !fullyCovered)
                {
                    EmitMaterialDifference(
                        firstStart,
                        firstEnd,
                        secondOccupied,
                        secondOccupiedStart,
                        secondOccupiedEnd,
                        2,
                        false,
                        source,
                        firstDirection,
                        firstX,
                        firstZ,
                        opaqueWords,
                        ref opaqueWordCount,
                        ref writer);
                }
            }

            if (!secondHasWater)
                return;
            int secondWaterStart = Math.Max(
                secondColumn.WaterStart,
                chunkStart);
            int secondWaterEnd = Math.Min(secondColumn.WaterEnd, chunkEnd);
            bool secondFullyCovered = firstOccupied &&
                firstOccupiedStart <= secondWaterStart &&
                firstOccupiedEnd >= secondWaterEnd;
            if (secondWaterStart <= secondWaterEnd && !secondFullyCovered)
            {
                EmitMaterialDifference(
                    secondWaterStart,
                    secondWaterEnd,
                    firstOccupied,
                    firstOccupiedStart,
                    firstOccupiedEnd,
                    2,
                    false,
                    source,
                    secondDirection,
                    secondX,
                    secondZ,
                    opaqueWords,
                    ref opaqueWordCount,
                    ref writer);
            }
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
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
                    opaqueWords,
                    ref opaqueWordCount,
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
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
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
                        z,
                        opaqueWords,
                        ref opaqueWordCount);
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
                    z,
                    opaqueWords,
                    ref opaqueWordCount);
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
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
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
                    z,
                    opaqueWords,
                    ref opaqueWordCount);
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
                    z,
                    opaqueWords,
                    ref opaqueWordCount);
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
                    z,
                    opaqueWords,
                    ref opaqueWordCount);
            }
        }

        private static void EmitGeneratedHorizontalRectangles(
            byte direction,
            int[] faceHeights,
            int width,
            int depth,
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
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
                        extentZ,
                        opaqueWords,
                        ref opaqueWordCount);
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
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
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
                        z,
                        opaqueWords,
                        ref opaqueWordCount);
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
            scoped Span<uint> opaqueWords,
            ref int opaqueWordCount,
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
                        z,
                        opaqueWords,
                        ref opaqueWordCount);
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
                    z,
                    opaqueWords,
                    ref opaqueWordCount);
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

        private sealed class GeneratedFaceRectangleState
        {
            private readonly uint[] faceTileAttributes;
            private readonly PackedFaceStagingWorkspace stagingWorkspace;
            private uint[] transparentWords;
            private int transparentWordCount;
            private int opaqueFaceCount;
            private int transparentFaceCount;

            public GeneratedFaceRectangleState(
                GeneratedChunkSpanData source,
                BlockTextureAtlas atlas,
                PackedFaceStagingWorkspace stagingWorkspace)
            {
                faceTileAttributes = BuildFaceTileAttributes(source, atlas);
                this.stagingWorkspace = stagingWorkspace;
                transparentWords = stagingWorkspace.TransparentBuffer;
                transparentWordCount = 0;
                opaqueFaceCount = 0;
                transparentFaceCount = 0;
            }

            public int OpaqueFaceCount => opaqueFaceCount;

            public int TransparentFaceCount => transparentFaceCount;

            public int TransparentWordCount => transparentWordCount;

            public ReadOnlySpan<uint> TransparentWords =>
                transparentWords.AsSpan(0, transparentWordCount);

            public uint[] GetOpaqueBatchBuffer(int minimumWordCount) =>
                stagingWorkspace.GetOpaqueBatchBuffer(minimumWordCount);

            public uint GetFaceTileAttribute(int material, byte direction) =>
                faceTileAttributes[material * 6 + direction];

            public uint GetFaceTileAttribute(int tileOffset) =>
                faceTileAttributes[tileOffset];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void AddOpaqueFaces(int count)
            {
                opaqueFaceCount = checked(opaqueFaceCount + count);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void AppendTransparentRectangle(
                uint position,
                uint attributes,
                int faceCount)
            {
                int required = transparentWordCount + 2;
                if (required > transparentWords.Length)
                {
                    int nextLength = Math.Max(
                        required,
                        checked(transparentWords.Length * 2));
                    Array.Resize(ref transparentWords, nextLength);
                }

                transparentWords[transparentWordCount] = position;
                transparentWords[transparentWordCount + 1] = attributes;
                transparentWordCount = required;
                transparentFaceCount = checked(
                    transparentFaceCount + faceCount);
            }

            public void CommitTransparentBuffer()
            {
                stagingWorkspace.AdoptTransparent(transparentWords);
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

        private struct GeneratedFaceRectangleWriter
        {
            private readonly GeneratedFaceRectangleState state;
            private int currentTileOffset;
            private bool currentOpaque;
            private bool materialSelected;

            public GeneratedFaceRectangleWriter(
                GeneratedFaceRectangleState state)
            {
                this.state = state;
                currentTileOffset = 0;
                currentOpaque = false;
                materialSelected = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EmitMaterialYRange(
                int material,
                bool opaque,
                byte direction,
                int x,
                int startY,
                int endY,
                int z,
                scoped Span<uint> opaqueWords,
                ref int opaqueWordCount)
            {
                uint position = (uint)x |
                    ((uint)startY << 8) |
                    ((uint)z << 16) |
                    ((uint)direction << 24);
                uint attributes =
                    ((uint)(endY - startY) << 8) |
                    state.GetFaceTileAttribute(material, direction);
                AppendRectangle(
                    opaque,
                    position,
                    attributes,
                    endY - startY + 1,
                    opaqueWords,
                    ref opaqueWordCount);
            }

            public void SelectMaterial(int material, bool opaque)
            {
                if ((uint)material >= 3)
                    throw new ArgumentOutOfRangeException(nameof(material));
                currentTileOffset = material * 6;
                currentOpaque = opaque;
                materialSelected = true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EmitRectangle(
                byte direction,
                int x,
                int y,
                int z,
                int extentU,
                int extentV,
                scoped Span<uint> opaqueWords,
                ref int opaqueWordCount)
            {
                if (!materialSelected)
                {
                    throw new InvalidOperationException(
                        "A material is not selected for the face rectangle.");
                }

                uint position = (uint)x |
                    ((uint)y << 8) |
                    ((uint)z << 16) |
                    ((uint)direction << 24);
                uint attributes = (uint)(extentU - 1) |
                    ((uint)(extentV - 1) << 8) |
                    state.GetFaceTileAttribute(
                        currentTileOffset + direction);
                AppendRectangle(
                    currentOpaque,
                    position,
                    attributes,
                    checked(extentU * extentV),
                    opaqueWords,
                    ref opaqueWordCount);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EmitYRange(
                byte direction,
                int x,
                int startY,
                int endY,
                int z,
                scoped Span<uint> opaqueWords,
                ref int opaqueWordCount)
            {
                EmitRectangle(
                    direction,
                    x,
                    startY,
                    z,
                    1,
                    endY - startY + 1,
                    opaqueWords,
                    ref opaqueWordCount);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void AppendRectangle(
                bool opaque,
                uint position,
                uint attributes,
                int faceCount,
                scoped Span<uint> opaqueWords,
                ref int opaqueWordCount)
            {
                if (!opaque)
                {
                    state.AppendTransparentRectangle(
                        position,
                        attributes,
                        faceCount);
                    return;
                }

                int required = opaqueWordCount + 2;
                if (required > opaqueWords.Length)
                {
                    throw new InvalidOperationException(
                        "The bounded opaque face batch is too small.");
                }

                opaqueWords[opaqueWordCount] = position;
                opaqueWords[opaqueWordCount + 1] = attributes;
                opaqueWordCount = required;
                state.AddOpaqueFaces(faceCount);
            }
        }
    }
}

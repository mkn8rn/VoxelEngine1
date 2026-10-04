using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;
internal static class NativeGeneratedMesh
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool TryBuild(scoped ref NativeGtrtSessionView session, scoped ref readonly NativeWorkItem claimedWork, int workerIndex)
    {
        if ((uint)(session.ChunkSizeX - 1) > byte.MaxValue || (uint)(session.ChunkSizeY - 1) > byte.MaxValue || (uint)(session.ChunkSizeZ - 1) > byte.MaxValue || claimedWork.Kind != NativeWorkKind.BuildChunkMesh || (uint)claimedWork.RecordIndex >= (uint)session.ChunkCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            session.TryAbandonMesh(in claimedWork);
            return false;
        }

        if (!session.TryAcquireMeshWorkspace(workerIndex))
        {
            session.TryAbandonMesh(in claimedWork);
            return false;
        }

        bool completed = false;
        try
        {
            if (session.CancellationRequested)
                return false;
            Span<int> negativeFaces = session.GetMeshNegativeFaceScratch(workerIndex);
            Span<int> positiveFaces = session.GetMeshPositiveFaceScratch(workerIndex);
            if (!TryBuildPacket(ref session, in claimedWork, workerIndex, negativeFaces, positiveFaces))
                return false;

            if (session.CancellationRequested)
                return false;
            completed = session.TryCompleteMesh(in claimedWork);
            return completed;
        }
        finally
        {
            if (!completed)
                session.TryAbandonMesh(in claimedWork);
            session.ReleaseMeshWorkspace(workerIndex);
        }
    }

    private static bool TryBuildPacket(scoped ref NativeGtrtSessionView session,
        scoped ref readonly NativeWorkItem work, int workerIndex, Span<int> negativeFaces, Span<int> positiveFaces)
    {
        NativeTerrainMaterialSet materials = NativeGeneratedTerrain.GetMaterials(ref session, work.RecordIndex);
        if (!TryRequiresVoxelMesh(ref session, work.RecordIndex, out bool voxelMesh))
            return false;
        if (!voxelMesh && NativeTransparentProfileMesh.Supports(in materials))
            return TryBuildProfilePacket(ref session, in work, workerIndex, in materials, negativeFaces, positiveFaces);
        return TryBuildCountedPacket(ref session, in work, in materials, negativeFaces, positiveFaces);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryBuildProfilePacket(scoped ref NativeGtrtSessionView session,
        scoped ref readonly NativeWorkItem work, int workerIndex, scoped in NativeTerrainMaterialSet materials,
        Span<int> negativeFaces, Span<int> positiveFaces)
    {
        Span<uint> opaque = session.GetMeshProfilePacketScratch(workerIndex, opaque: true);
        Span<uint> transparent = session.GetMeshProfilePacketScratch(workerIndex, opaque: false);
        var writer = new NativeGeneratedFaceWriter(materials, opaque, transparent);
        if (!Emit(ref session, work.RecordIndex, negativeFaces, positiveFaces, ref writer) || !writer.Valid)
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            return false;
        }
        if (session.CancellationRequested)
            return false;
        if (!session.TryBeginPacket(in work, writer.OpaqueWordCount, writer.OpaqueFaceCount,
                writer.TransparentWordCount, writer.TransparentFaceCount, out NativePacketWriteView packet))
            return false;
        opaque[..writer.OpaqueWordCount].CopyTo(packet.OpaqueWords);
        transparent[..writer.TransparentWordCount].CopyTo(packet.TransparentWords);
        return true;
    }

    private static bool TryBuildCountedPacket(scoped ref NativeGtrtSessionView session,
        scoped ref readonly NativeWorkItem work, scoped in NativeTerrainMaterialSet materials,
        Span<int> negativeFaces, Span<int> positiveFaces)
    {
        var counter = new NativeGeneratedFaceWriter(materials);
        if (!Emit(ref session, work.RecordIndex, negativeFaces, positiveFaces, ref counter) || !counter.Valid)
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            return false;
        }
        if (session.CancellationRequested)
            return false;
        if (!session.TryBeginPacket(in work, counter.OpaqueWordCount, counter.OpaqueFaceCount,
                counter.TransparentWordCount, counter.TransparentFaceCount, out NativePacketWriteView packet))
            return false;
        var writer = new NativeGeneratedFaceWriter(materials, packet.OpaqueWords, packet.TransparentWords);
        if (!Emit(ref session, work.RecordIndex, negativeFaces, positiveFaces, ref writer) || !writer.Valid ||
            writer.OpaqueWordCount != counter.OpaqueWordCount || writer.OpaqueFaceCount != counter.OpaqueFaceCount ||
            writer.TransparentWordCount != counter.TransparentWordCount || writer.TransparentFaceCount != counter.TransparentFaceCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Emit(scoped ref NativeGtrtSessionView session, int chunkIndex, Span<int> negativeFaces, Span<int> positiveFaces, scoped ref NativeGeneratedFaceWriter writer)
    {
        if (!NativeUniformChunkMesh.TryEmit(ref session, chunkIndex, negativeFaces, ref writer, out bool handled))
            return false;
        if (handled)
            return writer.Valid;
        if (!TryRequiresVoxelMesh(ref session, chunkIndex, out bool requiresVoxelMesh))
        {
            return false;
        }

        if (requiresVoxelMesh)
        {
            return NativeVoxelMesh.TryEmit(ref session, chunkIndex, negativeFaces, positiveFaces, ref writer);
        }

        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        NativeTerrainMaterialSet materials = NativeGeneratedTerrain.GetMaterials(ref session, chunkIndex);
        ReadOnlySpan<BlockColumnProfile> columns = session.GetColumnProfiles(chunk.ColumnIndex);
        int horizontalFaceCount = checked(session.ChunkSizeX * session.ChunkSizeZ);
        Span<int> bottomFaces = negativeFaces.Slice(0, horizontalFaceCount);
        Span<int> topFaces = positiveFaces.Slice(0, horizontalFaceCount);
        var context = new NativeProfileMeshContext(columns, materials, session.ChunkSizeX,
            session.ChunkSizeY, session.ChunkSizeZ, chunk.ChunkY * session.ChunkSizeY);
        bool contiguousSides = SupportsContiguousFastPath(materials);
        bool transparentSides = NativeTransparentProfileMesh.Supports(in materials);
        bool directInteriorSides = contiguousSides || transparentSides;
        if (contiguousSides)
        {
            EmitContiguousInteriorSideRectangles(columns, session.ChunkSizeX, session.ChunkSizeY, session.ChunkSizeZ, chunk.ChunkY * session.ChunkSizeY, ref writer);
        }
        else if (transparentSides)
        {
            NativeTransparentProfileMesh.EmitInteriorSides(columns, session.ChunkSizeX, session.ChunkSizeY,
                session.ChunkSizeZ, chunk.ChunkY * session.ChunkSizeY, ref writer);
        }

        for (int material = 0; material < 3; material++)
        {
            NativeBlockDescriptor descriptor = GetMaterial(materials, material);
            if (descriptor.Id == 0 || !MaterialIntersectsChunk(ref session, chunk, material))
                continue;
            bottomFaces.Fill(-1);
            topFaces.Fill(-1);
            writer.SelectMaterial(material, IsOpaque(descriptor));
            if (!GenerateMaterial(ref session, chunkIndex, in context, material, descriptor.Id, IsOpaque(descriptor), !directInteriorSides, bottomFaces, topFaces, ref writer))
            {
                return false;
            }

            EmitHorizontalRectangles(direction: 2, bottomFaces, session.ChunkSizeX, session.ChunkSizeZ, ref writer);
            EmitHorizontalRectangles(direction: 3, topFaces, session.ChunkSizeX, session.ChunkSizeZ, ref writer);
        }

        return writer.Valid;
    }

    private static bool MaterialIntersectsChunk(scoped ref NativeGtrtSessionView session,
        NativeChunkRecord chunk, int material)
    {
        if (session.Columns[chunk.ColumnIndex].SummaryComputed == 0)
            return true;
        ref readonly NativeColumnSummary summary = ref session.ColumnSummaries[chunk.ColumnIndex];
        int start = material switch
        {
            0 => summary.StoneStartMinimum,
            1 => summary.SoilStartMinimum,
            _ => summary.WaterStartMinimum
        };
        int end = material switch
        {
            0 => summary.StoneEndMaximum,
            1 => summary.SoilEndMaximum,
            _ => summary.WaterEndMaximum
        };
        int bottom = chunk.ChunkY * session.ChunkSizeY;
        return start <= bottom + session.ChunkSizeY - 1 && end >= bottom;
    }

    private static bool TryRequiresVoxelMesh(scoped ref NativeGtrtSessionView session, int chunkIndex, out bool required)
    {
        NativeChunkRecord chunk = session.Chunks[chunkIndex];
        if (!IsStorageKindValid(chunk.StorageKind))
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            required = false;
            return false;
        }

        NativeColumnRecord column = session.Columns[chunk.ColumnIndex];
        required = chunk.StorageKind != NativeChunkStorageKind.GeneratedProfile || column.ReplacementMode == 2;
        if (required)
            return true;
        for (byte direction = 0; direction < 6; direction++)
        {
            int neighborX = chunk.ChunkX;
            int neighborY = chunk.ChunkY;
            int neighborZ = chunk.ChunkZ;
            ApplyNeighborOffset(direction, ref neighborX, ref neighborY, ref neighborZ);

            int neighborColumnIndex = session.GetColumnIndex(neighborX, neighborZ);
            if (neighborColumnIndex >= 0)
            {
                NativeColumnRecord neighborColumn = session.Columns[neighborColumnIndex];
                if (neighborColumn.ReplacementMode == 2 || !SameMaterials(ref session, in column, in neighborColumn))
                {
                    required = true;
                    return true;
                }
            }

            if (!TryCheckNeighborStorage(ref session, neighborX, neighborY, neighborZ, out bool neighborRequires))
                return false;
            if (neighborRequires)
            {
                required = true;
                return true;
            }
        }

        return true;
    }

    private static bool TryCheckNeighborStorage(scoped ref NativeGtrtSessionView session,
        int neighborX, int neighborY, int neighborZ, out bool required)
    {
        required = false;
        int neighborIndex = session.GetChunkIndex(neighborX, neighborY, neighborZ);
        if (neighborIndex < 0)
        {
            int materializedNeighbor = session.FindMaterializedChunkIndex(neighborX, neighborY, neighborZ);
            if (materializedNeighbor >= 0)
            {
                required = true;
                return true;
            }

            if (session.State.FailureCode != 0)
                return false;
            return true;
        }

        NativeChunkStorageKind neighborKind = session.Chunks[neighborIndex].StorageKind;
        if (!IsStorageKindValid(neighborKind))
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            return false;
        }

        if (neighborKind != NativeChunkStorageKind.GeneratedProfile)
        {
            required = true;
            return true;
        }
        return true;
    }

    private static void ApplyNeighborOffset(byte direction, ref int neighborX, ref int neighborY, ref int neighborZ)
    {
        switch (direction)
        {
            case 0:
                neighborX--;
                break;
            case 1:
                neighborX++;
                break;
            case 2:
                neighborY--;
                break;
            case 3:
                neighborY++;
                break;
            case 4:
                neighborZ--;
                break;
            case 5:
                neighborZ++;
                break;
        }
    }

    private static bool SameMaterials(scoped ref NativeGtrtSessionView session, scoped ref readonly NativeColumnRecord first, scoped ref readonly NativeColumnRecord second)
    {
        NativeTerrainMaterialSet a = first.ReplacementMode == 1 ? first.ResolvedMaterials : session.Materials;
        NativeTerrainMaterialSet b = second.ReplacementMode == 1 ? second.ResolvedMaterials : session.Materials;
        return a.Stone.Id == b.Stone.Id && a.Soil.Id == b.Soil.Id && a.Water.Id == b.Water.Id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsStorageKindValid(NativeChunkStorageKind kind) => kind == NativeChunkStorageKind.GeneratedProfile || kind == NativeChunkStorageKind.HybridSections || kind == NativeChunkStorageKind.MaterializedSections || kind == NativeChunkStorageKind.UniformSections;
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool GenerateMaterial(scoped ref NativeGtrtSessionView session, int chunkIndex, scoped in NativeProfileMeshContext context, int material, ushort blockId, bool blockOpaque, bool emitInteriorSides, Span<int> bottomFaces, Span<int> topFaces, scoped ref NativeGeneratedFaceWriter writer)
    {
        int width = context.Width;
        int depth = context.Depth;
        ReadOnlySpan<BlockColumnProfile> columns = context.Profiles;
        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                BlockColumnProfile column = columns[x * depth + z];
                GetMaterialInterval(in column, material, out int intervalStart, out int intervalEnd);
                if (!GenerateIntervalRectangles(ref session, chunkIndex, in context, in column, blockId, blockOpaque, emitInteriorSides, intervalStart, intervalEnd, x, z, bottomFaces, topFaces, ref writer))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void GetMaterialInterval(scoped ref readonly BlockColumnProfile column, int material, out int intervalStart, out int intervalEnd)
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
                intervalStart = 0;
                intervalEnd = -1;
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool GenerateIntervalRectangles(scoped ref NativeGtrtSessionView session, int chunkIndex, scoped in NativeProfileMeshContext context, scoped ref readonly BlockColumnProfile column, ushort blockId, bool blockOpaque, bool emitInteriorSides, int intervalStart, int intervalEnd, int x, int z, Span<int> bottomFaces, Span<int> topFaces, scoped ref NativeGeneratedFaceWriter writer)
    {
        if (intervalStart < 0 || intervalEnd < intervalStart)
            return true;
        int chunkStart = context.ChunkStart;
        int chunkEnd = context.ChunkEnd;
        int worldStart = Math.Max(intervalStart, chunkStart);
        int worldEnd = Math.Min(intervalEnd, chunkEnd);
        if (worldStart > worldEnd)
            return true;
        int localStart = worldStart - chunkStart;
        int localEnd = worldEnd - chunkStart;
        int depth = context.Depth;
        int horizontalIndex = x * depth + z;
        ref readonly NativeTerrainMaterialSet materials = ref context.Materials;
        ushort bottomId = materials.GetBlockWorld(in column, worldStart - 1);
        if (!materials.TryIsOpaque(bottomId, out bool bottomOpaque))
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            return false;
        }

        if (NativeGeneratedTerrain.FaceVisible(blockOpaque, blockId, bottomOpaque, bottomId))
        {
            bottomFaces[horizontalIndex] = localStart;
        }

        ushort topId = materials.GetBlockWorld(in column, worldEnd + 1);
        if (!materials.TryIsOpaque(topId, out bool topOpaque))
        {
            session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
            return false;
        }

        if (NativeGeneratedTerrain.FaceVisible(blockOpaque, blockId, topOpaque, topId))
        {
            topFaces[horizontalIndex] = localEnd;
        }

        if ((emitInteriorSides || x == 0) && !EmitSide(ref session, chunkIndex, in context, blockId, blockOpaque, direction: 0, x, z, worldStart, worldEnd, ref writer))
            return false;
        if ((emitInteriorSides || x == context.Width - 1) && !EmitSide(ref session, chunkIndex, in context, blockId, blockOpaque, direction: 1, x, z, worldStart, worldEnd, ref writer))
            return false;
        if ((emitInteriorSides || z == 0) && !EmitSide(ref session, chunkIndex, in context, blockId, blockOpaque, direction: 4, x, z, worldStart, worldEnd, ref writer))
            return false;
        if ((emitInteriorSides || z == context.Depth - 1) && !EmitSide(ref session, chunkIndex, in context, blockId, blockOpaque, direction: 5, x, z, worldStart, worldEnd, ref writer))
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool EmitSide(scoped ref NativeGtrtSessionView session, int chunkIndex, scoped in NativeProfileMeshContext context, ushort blockId, bool blockOpaque, byte direction, int x, int z, int worldStart, int worldEnd, scoped ref NativeGeneratedFaceWriter writer)
    {
        int neighborX = x;
        int neighborZ = z;
        switch (direction)
        {
            case 0:
                neighborX--;
                break;
            case 1:
                neighborX++;
                break;
            case 4:
                neighborZ--;
                break;
            case 5:
                neighborZ++;
                break;
            default:
                return false;
        }

        bool interior = (uint)neighborX < (uint)context.Width && (uint)neighborZ < (uint)context.Depth;
        BlockColumnProfile neighbor;
        if (interior)
        {
            neighbor = context.Profiles[neighborX * context.Depth + neighborZ];
        }
        else if (!NativeGeneratedTerrain.TryGetProfile(ref session, chunkIndex, neighborX, neighborZ, out neighbor))
        {
            return false;
        }

        EmitColumnRange(context.Materials, in neighbor, blockId, blockOpaque, direction, x, z,
            worldStart, worldEnd, context.ChunkStart, ref writer);
        return true;
    }

    private static void EmitContiguousInteriorSideRectangles(ReadOnlySpan<BlockColumnProfile> columns, int width, int height, int depth, int chunkStart, scoped ref NativeGeneratedFaceWriter writer)
    {
        int chunkEnd = chunkStart + height - 1;
        for (int x = 0; x < width - 1; x++)
        {
            int leftBase = x * depth;
            int rightBase = leftBase + depth;
            for (int z = 0; z < depth; z++)
            {
                BlockColumnProfile left = columns[leftBase + z];
                BlockColumnProfile right = columns[rightBase + z];
                EmitContiguousColumnPair(in left, in right, chunkStart, chunkEnd, firstDirection: 1, firstX: x, firstZ: z, secondDirection: 0, secondX: x + 1, secondZ: z, ref writer);
            }
        }

        for (int x = 0; x < width; x++)
        {
            int columnBase = x * depth;
            for (int z = 0; z < depth - 1; z++)
            {
                BlockColumnProfile negative = columns[columnBase + z];
                BlockColumnProfile positive = columns[columnBase + z + 1];
                EmitContiguousColumnPair(in negative, in positive, chunkStart, chunkEnd, firstDirection: 5, firstX: x, firstZ: z, secondDirection: 4, secondX: x, secondZ: z + 1, ref writer);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitContiguousColumnPair(scoped ref readonly BlockColumnProfile firstColumn, scoped ref readonly BlockColumnProfile secondColumn, int chunkStart, int chunkEnd, byte firstDirection, int firstX, int firstZ, byte secondDirection, int secondX, int secondZ, scoped ref NativeGeneratedFaceWriter writer)
    {
        bool firstHasGround = TryGetGroundRange(in firstColumn, out int firstGroundStart, out int firstGroundEnd);
        bool secondHasGround = TryGetGroundRange(in secondColumn, out int secondGroundStart, out int secondGroundEnd);
        if (firstHasGround && secondHasGround && firstGroundStart == secondGroundStart)
        {
            if (firstGroundEnd > secondGroundEnd)
            {
                int start = Math.Max(secondGroundEnd + 1, chunkStart);
                int end = Math.Min(firstGroundEnd, chunkEnd);
                if (start <= end)
                {
                    EmitGroundSegment(in firstColumn, start, end, chunkStart, firstDirection, firstX, firstZ, ref writer);
                }
            }
            else if (secondGroundEnd > firstGroundEnd)
            {
                int start = Math.Max(firstGroundEnd + 1, chunkStart);
                int end = Math.Min(secondGroundEnd, chunkEnd);
                if (start <= end)
                {
                    EmitGroundSegment(in secondColumn, start, end, chunkStart, secondDirection, secondX, secondZ, ref writer);
                }
            }
        }
        else
        {
        EmitDisjointGroundDifferences(in firstColumn, in secondColumn, chunkStart, chunkEnd, firstDirection, firstX, firstZ, secondDirection, secondX, secondZ, ref writer, firstHasGround, firstGroundStart, firstGroundEnd, secondHasGround, secondGroundStart, secondGroundEnd);
        }

        bool firstHasWater = HasRange(firstColumn.WaterStart, firstColumn.WaterEnd);
        bool secondHasWater = HasRange(secondColumn.WaterStart, secondColumn.WaterEnd);
        bool firstOccupied = firstHasGround || firstHasWater;
        bool secondOccupied = secondHasGround || secondHasWater;
        int firstOccupiedStart = firstHasGround ? firstGroundStart : firstColumn.WaterStart;
        int firstOccupiedEnd = firstHasWater ? firstColumn.WaterEnd : firstGroundEnd;
        int secondOccupiedStart = secondHasGround ? secondGroundStart : secondColumn.WaterStart;
        int secondOccupiedEnd = secondHasWater ? secondColumn.WaterEnd : secondGroundEnd;
        if (firstHasWater)
        {
            int firstStart = Math.Max(firstColumn.WaterStart, chunkStart);
            int firstEnd = Math.Min(firstColumn.WaterEnd, chunkEnd);
            bool fullyCovered = secondOccupied && secondOccupiedStart <= firstStart && secondOccupiedEnd >= firstEnd;
            if (firstStart <= firstEnd && !fullyCovered)
            {
                EmitMaterialDifference(firstStart, firstEnd, secondOccupied, secondOccupiedStart, secondOccupiedEnd, material: 2, opaque: false, chunkStart, firstDirection, firstX, firstZ, ref writer);
            }
        }

        if (!secondHasWater)
            return;
        int secondWaterStart = Math.Max(secondColumn.WaterStart, chunkStart);
        int secondWaterEnd = Math.Min(secondColumn.WaterEnd, chunkEnd);
        bool secondFullyCovered = firstOccupied && firstOccupiedStart <= secondWaterStart && firstOccupiedEnd >= secondWaterEnd;
        if (secondWaterStart <= secondWaterEnd && !secondFullyCovered)
        {
            EmitMaterialDifference(secondWaterStart, secondWaterEnd, firstOccupied, firstOccupiedStart, firstOccupiedEnd, material: 2, opaque: false, chunkStart, secondDirection, secondX, secondZ, ref writer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetGroundRange(scoped ref readonly BlockColumnProfile column, out int start, out int end)
    {
        bool hasStone = HasRange(column.StoneStart, column.StoneEnd);
        bool hasSoil = HasRange(column.SoilStart, column.SoilEnd);
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
    private static void EmitGroundDifference(scoped ref readonly BlockColumnProfile sourceColumn, int sourceStart, int sourceEnd, bool neighborPresent, int neighborStart, int neighborEnd, int chunkStart, byte direction, int x, int z, scoped ref NativeGeneratedFaceWriter writer)
    {
        if (!neighborPresent || neighborEnd < sourceStart || neighborStart > sourceEnd)
        {
            EmitGroundSegment(in sourceColumn, sourceStart, sourceEnd, chunkStart, direction, x, z, ref writer);
            return;
        }

        if (sourceStart < neighborStart)
        {
            EmitGroundSegment(in sourceColumn, sourceStart, neighborStart - 1, chunkStart, direction, x, z, ref writer);
        }

        if (sourceEnd > neighborEnd)
        {
            EmitGroundSegment(in sourceColumn, neighborEnd + 1, sourceEnd, chunkStart, direction, x, z, ref writer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitGroundSegment(scoped ref readonly BlockColumnProfile column, int segmentStart, int segmentEnd, int chunkStart, byte direction, int x, int z, scoped ref NativeGeneratedFaceWriter writer)
    {
        int stoneStart = Math.Max(segmentStart, column.StoneStart);
        int stoneEnd = Math.Min(segmentEnd, column.StoneEnd);
        if (stoneStart <= stoneEnd)
        {
            writer.EmitMaterialYRange(material: 0, opaque: true, direction, x, stoneStart - chunkStart, stoneEnd - chunkStart, z);
        }

        int soilStart = Math.Max(segmentStart, column.SoilStart);
        int soilEnd = Math.Min(segmentEnd, column.SoilEnd);
        if (soilStart <= soilEnd)
        {
            writer.EmitMaterialYRange(material: 1, opaque: true, direction, x, soilStart - chunkStart, soilEnd - chunkStart, z);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitMaterialDifference(int sourceStart, int sourceEnd, bool neighborPresent, int neighborStart, int neighborEnd, int material, bool opaque, int chunkStart, byte direction, int x, int z, scoped ref NativeGeneratedFaceWriter writer)
    {
        if (!neighborPresent || neighborEnd < sourceStart || neighborStart > sourceEnd)
        {
            writer.EmitMaterialYRange(material, opaque, direction, x, sourceStart - chunkStart, sourceEnd - chunkStart, z);
            return;
        }

        if (sourceStart < neighborStart)
        {
            writer.EmitMaterialYRange(material, opaque, direction, x, sourceStart - chunkStart, neighborStart - chunkStart - 1, z);
        }

        if (sourceEnd > neighborEnd)
        {
            writer.EmitMaterialYRange(material, opaque, direction, x, neighborEnd - chunkStart + 1, sourceEnd - chunkStart, z);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EmitHorizontalRectangles(byte direction, Span<int> faceHeights, int width, int depth, scoped ref NativeGeneratedFaceWriter writer)
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
                while (x + extentX < width && faceHeights[(x + extentX) * depth + z] == y)
                {
                    extentX++;
                }

                int extentZ = 1;
                while (z + extentZ < depth)
                {
                    bool rowMatches = true;
                    for (int offsetX = 0; offsetX < extentX; offsetX++)
                    {
                        if (faceHeights[(x + offsetX) * depth + z + extentZ] != y)
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
                    faceHeights.Slice(row, extentZ).Fill(-2);
                }

                int anchorZ = direction == 3 ? z + extentZ - 1 : z;
                writer.EmitClippedProfileRectangle(direction, x, y, anchorZ, extentX, extentZ);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EmitColumnRange(NativeTerrainMaterialSet materials, scoped ref readonly BlockColumnProfile neighborColumn, ushort blockId, bool blockOpaque, byte direction, int x, int z, int worldStart, int worldEnd, int chunkStart, scoped ref NativeGeneratedFaceWriter writer)
    {
        int current = worldStart;
        while (current <= worldEnd)
        {
            GetNeighborRun(materials, in neighborColumn, current, worldEnd, out ushort neighborId, out bool neighborOpaque, out int runEnd);
            if (NativeGeneratedTerrain.FaceVisible(blockOpaque, blockId, neighborOpaque, neighborId))
            {
                writer.EmitYRange(direction, x, current - chunkStart, runEnd - chunkStart, z);
            }

            current = runEnd + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GetNeighborRun(NativeTerrainMaterialSet materials, scoped ref readonly BlockColumnProfile column, int worldY, int maximumWorldY, out ushort blockId, out bool blockOpaque, out int runEnd)
    {
        if (HasRange(column.StoneStart, column.StoneEnd) && worldY >= column.StoneStart && worldY <= column.StoneEnd)
        {
            blockId = materials.Stone.Id;
            blockOpaque = IsOpaque(materials.Stone);
            runEnd = Math.Min(column.StoneEnd, maximumWorldY);
            return;
        }

        if (HasRange(column.SoilStart, column.SoilEnd) && worldY >= column.SoilStart && worldY <= column.SoilEnd)
        {
            blockId = materials.Soil.Id;
            blockOpaque = IsOpaque(materials.Soil);
            runEnd = Math.Min(column.SoilEnd, maximumWorldY);
            return;
        }

        if (HasRange(column.WaterStart, column.WaterEnd) && worldY >= column.WaterStart && worldY <= column.WaterEnd)
        {
            blockId = materials.Water.Id;
            blockOpaque = IsOpaque(materials.Water);
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
    private static NativeBlockDescriptor GetMaterial(NativeTerrainMaterialSet materials, int material) => material switch
    {
        0 => materials.Stone,
        1 => materials.Soil,
        2 => materials.Water,
        _ => default
    };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SupportsContiguousFastPath(NativeTerrainMaterialSet materials) => materials.Stone.Id != 0 && materials.Soil.Id != 0 && materials.Water.Id != 0 && IsOpaque(materials.Stone) && IsOpaque(materials.Soil) && !IsOpaque(materials.Water);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOpaque(NativeBlockDescriptor descriptor) => descriptor.HasFlag(NativeBlockFlags.Opaque);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasRange(int start, int end) => start >= 0 && end >= start;

    private static void EmitDisjointGroundDifferences(scoped ref readonly global::MVoxelEngine1.Infrastructure.Models.Generation.BlockColumnProfile firstColumn, scoped ref readonly global::MVoxelEngine1.Infrastructure.Models.Generation.BlockColumnProfile secondColumn, int chunkStart, int chunkEnd, byte firstDirection, int firstX, int firstZ, byte secondDirection, int secondX, int secondZ, scoped ref global::MVoxelEngine1.WorldGeneration.Native.NativeGeneratedFaceWriter writer, bool firstHasGround, int firstGroundStart, int firstGroundEnd, bool secondHasGround, int secondGroundStart, int secondGroundEnd)
    {
        if (firstHasGround)
        {
            int firstStart = Math.Max(firstGroundStart, chunkStart);
            int firstEnd = Math.Min(firstGroundEnd, chunkEnd);
            if (firstStart <= firstEnd)
            {
                EmitGroundDifference(in firstColumn, firstStart, firstEnd, secondHasGround, secondGroundStart, secondGroundEnd, chunkStart, firstDirection, firstX, firstZ, ref writer);
            }
        }

        if (secondHasGround)
        {
            int secondStart = Math.Max(secondGroundStart, chunkStart);
            int secondEnd = Math.Min(secondGroundEnd, chunkEnd);
            if (secondStart <= secondEnd)
            {
                EmitGroundDifference(in secondColumn, secondStart, secondEnd, firstHasGround, firstGroundStart, firstGroundEnd, chunkStart, secondDirection, secondX, secondZ, ref writer);
            }
        }
    }
}

namespace MVoxelEngine1.WorldGeneration.Native;

internal static class NativeVoxelMesh
{
    internal static bool TryEmit(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        Span<int> negativeFaceScratch,
        Span<int> positiveFaceScratch,
        scoped ref NativeGeneratedFaceWriter writer)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            if (!TryEmitAxis(
                    ref session,
                    chunkIndex,
                    axis,
                    negativeFaceScratch,
                    positiveFaceScratch,
                    ref writer))
            {
                return false;
            }
        }

        return writer.Valid;
    }

    private static bool TryEmitAxis(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int axis,
        Span<int> negativeFaceScratch,
        Span<int> positiveFaceScratch,
        scoped ref NativeGeneratedFaceWriter writer)
    {
        GetAxisDimensions(
            ref session,
            axis,
            out int normalSize,
            out int uSize,
            out int vSize,
            out byte negativeDirection,
            out byte positiveDirection);
        int planeCellCount = checked(uSize * vSize);
        if (negativeFaceScratch.Length < planeCellCount ||
            positiveFaceScratch.Length < planeCellCount)
        {
            session.Fail(NativeGtrtFailureCode.InvalidMeshWorkspace);
            return false;
        }

        Span<int> negativeFaces = negativeFaceScratch.Slice(
            0,
            planeCellCount);
        Span<int> positiveFaces = positiveFaceScratch.Slice(
            0,
            planeCellCount);
        for (int boundary = 0; boundary <= normalSize; boundary++)
        {
            if (!TryFillMasks(
                    ref session,
                    chunkIndex,
                    axis,
                    boundary,
                    normalSize,
                    uSize,
                    vSize,
                    negativeFaces,
                    positiveFaces))
            {
                return false;
            }

            if (boundary < normalSize &&
                !TryEmitMask(
                    ref session,
                    negativeFaces,
                    uSize,
                    vSize,
                    negativeDirection,
                    boundary,
                    ref writer))
            {
                return false;
            }
            if (boundary > 0 &&
                !TryEmitMask(
                    ref session,
                    positiveFaces,
                    uSize,
                    vSize,
                    positiveDirection,
                    boundary - 1,
                    ref writer))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFillMasks(
        scoped ref NativeGtrtSessionView session,
        int chunkIndex,
        int axis,
        int boundary,
        int normalSize,
        int uSize,
        int vSize,
        Span<int> negativeFaces,
        Span<int> positiveFaces)
    {
        for (int v = 0; v < vSize; v++)
        {
            int rowOffset = checked(v * uSize);
            for (int u = 0; u < uSize; u++)
            {
                GetPairCoordinates(
                    axis,
                    boundary,
                    u,
                    v,
                    out int lowerX,
                    out int lowerY,
                    out int lowerZ,
                    out int upperX,
                    out int upperY,
                    out int upperZ);
                if (!NativeGeneratedTerrain.TryGetBlock(
                        ref session,
                        chunkIndex,
                        lowerX,
                        lowerY,
                        lowerZ,
                        out ushort lowerId) ||
                    !NativeGeneratedTerrain.TryGetBlock(
                        ref session,
                        chunkIndex,
                        upperX,
                        upperY,
                        upperZ,
                        out ushort upperId) ||
                    !session.TryIsBlockOpaque(
                        lowerId,
                        out bool lowerOpaque) ||
                    !session.TryIsBlockOpaque(
                        upperId,
                        out bool upperOpaque))
                {
                    session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
                    return false;
                }

                int index = rowOffset + u;
                negativeFaces[index] = boundary < normalSize &&
                    upperId != 0 &&
                    NativeGeneratedTerrain.FaceVisible(
                        upperOpaque,
                        upperId,
                        lowerOpaque,
                        lowerId)
                    ? upperId
                    : 0;
                positiveFaces[index] = boundary > 0 &&
                    lowerId != 0 &&
                    NativeGeneratedTerrain.FaceVisible(
                        lowerOpaque,
                        lowerId,
                        upperOpaque,
                        upperId)
                    ? lowerId
                    : 0;
            }
        }

        return true;
    }

    private static bool TryEmitMask(
        scoped ref NativeGtrtSessionView session,
        Span<int> faces,
        int uSize,
        int vSize,
        byte direction,
        int normalCoordinate,
        scoped ref NativeGeneratedFaceWriter writer)
    {
        for (int v = 0; v < vSize; v++)
        {
            for (int u = 0; u < uSize; u++)
            {
                int index = checked(v * uSize + u);
                int blockId = faces[index];
                if (blockId == 0)
                    continue;

                int extentU = 1;
                while (u + extentU < uSize &&
                       faces[index + extentU] == blockId)
                {
                    extentU++;
                }

                int extentV = 1;
                while (v + extentV < vSize &&
                       RowMatches(
                           faces,
                           uSize,
                           u,
                           v + extentV,
                           extentU,
                           blockId))
                {
                    extentV++;
                }

                for (int offsetV = 0;
                     offsetV < extentV;
                     offsetV++)
                {
                    faces.Slice(
                        checked((v + offsetV) * uSize + u),
                        extentU).Clear();
                }

                if (!session.TryGetBlockDescriptor(
                        checked((ushort)blockId),
                        out NativeBlockDescriptor descriptor))
                {
                    session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
                    return false;
                }
                GetAnchor(
                    direction,
                    normalCoordinate,
                    u,
                    v,
                    extentU,
                    extentV,
                    out int anchorX,
                    out int anchorY,
                    out int anchorZ);
                writer.EmitBlockRectangle(
                    in descriptor,
                    direction,
                    anchorX,
                    anchorY,
                    anchorZ,
                    extentU,
                    extentV);
                if (!writer.Valid)
                {
                    session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
                    return false;
                }
            }
        }

        return true;
    }

    private static bool RowMatches(
        ReadOnlySpan<int> faces,
        int uSize,
        int startU,
        int v,
        int extentU,
        int blockId)
    {
        int rowOffset = checked(v * uSize + startU);
        for (int offsetU = 0; offsetU < extentU; offsetU++)
        {
            if (faces[rowOffset + offsetU] != blockId)
                return false;
        }

        return true;
    }

    private static void GetAxisDimensions(
        scoped ref NativeGtrtSessionView session,
        int axis,
        out int normalSize,
        out int uSize,
        out int vSize,
        out byte negativeDirection,
        out byte positiveDirection)
    {
        switch (axis)
        {
            case 0:
                normalSize = session.ChunkSizeX;
                uSize = session.ChunkSizeZ;
                vSize = session.ChunkSizeY;
                negativeDirection = 0;
                positiveDirection = 1;
                return;
            case 1:
                normalSize = session.ChunkSizeY;
                uSize = session.ChunkSizeX;
                vSize = session.ChunkSizeZ;
                negativeDirection = 2;
                positiveDirection = 3;
                return;
            case 2:
                normalSize = session.ChunkSizeZ;
                uSize = session.ChunkSizeX;
                vSize = session.ChunkSizeY;
                negativeDirection = 4;
                positiveDirection = 5;
                return;
            default:
                normalSize = 0;
                uSize = 0;
                vSize = 0;
                negativeDirection = 0;
                positiveDirection = 0;
                session.Fail(NativeGtrtFailureCode.InvalidGeneratedMesh);
                return;
        }
    }

    private static void GetPairCoordinates(
        int axis,
        int boundary,
        int u,
        int v,
        out int lowerX,
        out int lowerY,
        out int lowerZ,
        out int upperX,
        out int upperY,
        out int upperZ)
    {
        switch (axis)
        {
            case 0:
                lowerX = boundary - 1;
                lowerY = v;
                lowerZ = u;
                upperX = boundary;
                upperY = v;
                upperZ = u;
                return;
            case 1:
                lowerX = u;
                lowerY = boundary - 1;
                lowerZ = v;
                upperX = u;
                upperY = boundary;
                upperZ = v;
                return;
            default:
                lowerX = u;
                lowerY = v;
                lowerZ = boundary - 1;
                upperX = u;
                upperY = v;
                upperZ = boundary;
                return;
        }
    }

    private static void GetAnchor(
        byte direction,
        int normalCoordinate,
        int u,
        int v,
        int extentU,
        int extentV,
        out int x,
        out int y,
        out int z)
    {
        switch (direction)
        {
            case 0:
                x = normalCoordinate;
                y = v;
                z = u;
                return;
            case 1:
                x = normalCoordinate;
                y = v;
                z = u + extentU - 1;
                return;
            case 2:
                x = u;
                y = normalCoordinate;
                z = v;
                return;
            case 3:
                x = u;
                y = normalCoordinate;
                z = v + extentV - 1;
                return;
            case 4:
                x = u + extentU - 1;
                y = v;
                z = normalCoordinate;
                return;
            default:
                x = u;
                y = v;
                z = normalCoordinate;
                return;
        }
    }
}

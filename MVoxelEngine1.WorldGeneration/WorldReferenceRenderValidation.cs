using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.WorldGeneration.Terrain;

namespace MVoxelEngine1.WorldGeneration
{
    public partial class World
    {
        private Exception? meshBuildFailure;

        private void ValidateReferenceRenderData(
            (int cx, int cy, int cz) key,
            Chunk chunk)
        {
            ChunkRenderUploadData? data = chunk.chunkRender?.UploadData;
            var opaqueFaces = new HashSet<int>();
            var transparentFaces = new HashSet<int>();

            if (data is not null)
            {
                ReadActualReferenceFaces(
                    data.OpaqueRectangles.Span,
                    data.OpaqueFaceCount,
                    opaqueFaces,
                    key,
                    "opaque");
                ReadActualReferenceFaces(
                    data.TransparentRectangles.Span,
                    data.TransparentFaceCount,
                    transparentFaces,
                    key,
                    "transparent");
            }

            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;
            int originX = checked(key.cx * maxX);
            int originY = checked(key.cy * maxY);
            int originZ = checked(key.cz * maxZ);

            for (int x = 0; x < maxX; x++)
            {
                for (int y = 0; y < maxY; y++)
                {
                    for (int z = 0; z < maxZ; z++)
                    {
                        ushort blockId = chunk.GetBlockLocal(x, y, z);
                        if (blockId == (ushort)BaseBlockType.Empty)
                            continue;

                        bool opaque = TerrainLoader.IsOpaque(blockId);
                        HashSet<int> actualFaces = opaque
                            ? opaqueFaces
                            : transparentFaces;
                        for (byte direction = 0; direction < NeighborDirs.Length; direction++)
                        {
                            (int dx, int dy, int dz) = NeighborDirs[direction];
                            int neighborX = x + dx;
                            int neighborY = y + dy;
                            int neighborZ = z + dz;
                            ushort neighborId =
                                (uint)neighborX < (uint)maxX &&
                                (uint)neighborY < (uint)maxY &&
                                (uint)neighborZ < (uint)maxZ
                                    ? chunk.GetBlockLocal(neighborX, neighborY, neighborZ)
                                    : GetBlock(
                                        originX + neighborX,
                                        originY + neighborY,
                                        originZ + neighborZ);
                            bool visible = opaque
                                ? !TerrainLoader.IsOpaque(neighborId)
                                : neighborId == (ushort)BaseBlockType.Empty ||
                                  (!TerrainLoader.IsOpaque(neighborId) &&
                                   neighborId != blockId);
                            if (!visible)
                                continue;

                            int faceKey = EncodeReferenceFace(
                                x,
                                y,
                                z,
                                direction,
                                maxY,
                                maxZ);
                            if (!actualFaces.Remove(faceKey))
                            {
                                throw new InvalidOperationException(
                                    $"Reference render data omitted a {GetReferencePassName(opaque)} face " +
                                    $"for chunk {key} at ({x}, {y}, {z}) in direction {direction}.");
                            }
                        }
                    }
                }
            }

            ThrowIfExtraReferenceFace(
                opaqueFaces,
                key,
                "opaque",
                maxY,
                maxZ);
            ThrowIfExtraReferenceFace(
                transparentFaces,
                key,
                "transparent",
                maxY,
                maxZ);
        }

        private static void ReadActualReferenceFaces(
            ReadOnlySpan<uint> rectangles,
            int count,
            HashSet<int> destination,
            (int cx, int cy, int cz) chunkKey,
            string renderPass)
        {
            if (PackedFaceRectangle.CountLogicalFaces(rectangles) != count)
            {
                throw new InvalidOperationException(
                    $"Reference {renderPass} arrays have invalid lengths for chunk {chunkKey}.");
            }

            int maxX = GameManager.settings.chunkMaxX;
            int maxY = GameManager.settings.chunkMaxY;
            int maxZ = GameManager.settings.chunkMaxZ;
            var reader = new PackedFaceRectangleReader(rectangles);
            int index = 0;
            while (reader.MoveNext())
            {
                int x = reader.X;
                int y = reader.Y;
                int z = reader.Z;
                byte direction = reader.Direction;
                if ((uint)x >= (uint)maxX ||
                    (uint)y >= (uint)maxY ||
                    (uint)z >= (uint)maxZ ||
                    direction >= NeighborDirs.Length)
                {
                    throw new InvalidOperationException(
                        $"Reference {renderPass} data has an invalid face for chunk {chunkKey}.");
                }

                int faceKey = EncodeReferenceFace(x, y, z, direction, maxY, maxZ);
                if (!destination.Add(faceKey))
                {
                    throw new InvalidOperationException(
                        $"Reference {renderPass} data has a duplicate face for chunk {chunkKey}.");
                }
                index++;
            }

            if (index != count)
                throw new InvalidOperationException(
                    $"Reference {renderPass} data has an invalid face count for chunk {chunkKey}.");
        }

        private static int EncodeReferenceFace(
            int x,
            int y,
            int z,
            byte direction,
            int maxY,
            int maxZ)
        {
            return checked((((x * maxY) + y) * maxZ + z) * 6 + direction);
        }

        private static void ThrowIfExtraReferenceFace(
            HashSet<int> faces,
            (int cx, int cy, int cz) chunkKey,
            string renderPass,
            int maxY,
            int maxZ)
        {
            if (faces.Count == 0)
                return;

            int faceKey = faces.First();
            byte direction = (byte)(faceKey % 6);
            int voxelKey = faceKey / 6;
            int z = voxelKey % maxZ;
            voxelKey /= maxZ;
            int y = voxelKey % maxY;
            int x = voxelKey / maxY;
            throw new InvalidOperationException(
                $"Reference render data added an invalid {renderPass} face for chunk " +
                $"{chunkKey} at ({x}, {y}, {z}) in direction {direction}.");
        }

        private static string GetReferencePassName(bool opaque)
        {
            return opaque ? "opaque" : "transparent";
        }

        private void RecordMeshBuildFailure(
            (int cx, int cy, int cz) key,
            Exception cause)
        {
            var failure = new InvalidOperationException(
                $"{faceGenerationMode} mesh build failed for chunk {key}.",
                cause);
            Interlocked.CompareExchange(
                ref meshBuildFailure,
                failure,
                null);
        }

        private void ThrowIfMeshBuildFailed()
        {
            Exception? failure = Volatile.Read(ref meshBuildFailure);
            if (failure is not null)
                throw failure;
        }
    }
}

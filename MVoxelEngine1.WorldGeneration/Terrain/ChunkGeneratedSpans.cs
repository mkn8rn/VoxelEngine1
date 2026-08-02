using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Terrain
{
    public partial class Chunk
    {
        private void BuildGeneratedSpanBoundaryPlanes()
        {
            GeneratedChunkSpanData source = generatedSpans ??
                throw new InvalidOperationException("Generated span data is not available.");

            EnsurePlaneArrays();
            Array.Clear(PlaneNegX);
            Array.Clear(PlanePosX);
            Array.Clear(PlaneNegY);
            Array.Clear(PlanePosY);
            Array.Clear(PlaneNegZ);
            Array.Clear(PlanePosZ);
            TransparentPlaneNegX = null;
            TransparentPlanePosX = null;
            TransparentPlaneNegY = null;
            TransparentPlanePosY = null;
            TransparentPlaneNegZ = null;
            TransparentPlanePosZ = null;

            for (int z = 0; z < dimZ; z++)
            {
                WriteGeneratedBoundaryColumn(
                    source,
                    source.Columns[z],
                    PlaneNegX,
                    ref TransparentPlaneNegX,
                    z * dimY);
                WriteGeneratedBoundaryColumn(
                    source,
                    source.Columns[(dimX - 1) * dimZ + z],
                    PlanePosX,
                    ref TransparentPlanePosX,
                    z * dimY);
            }

            for (int x = 0; x < dimX; x++)
            {
                WriteGeneratedBoundaryColumn(
                    source,
                    source.Columns[x * dimZ],
                    PlaneNegZ,
                    ref TransparentPlaneNegZ,
                    x * dimY);
                WriteGeneratedBoundaryColumn(
                    source,
                    source.Columns[x * dimZ + dimZ - 1],
                    PlanePosZ,
                    ref TransparentPlanePosZ,
                    x * dimY);

                for (int z = 0; z < dimZ; z++)
                {
                    ref readonly BlockColumnProfile column =
                        ref source.Columns[x * dimZ + z];
                    int index = x * dimZ + z;
                    WriteGeneratedBoundaryCell(
                        source.GetBlockWorld(column, source.ChunkBaseY),
                        PlaneNegY,
                        ref TransparentPlaneNegY,
                        index);
                    WriteGeneratedBoundaryCell(
                        source.GetBlockWorld(
                            column,
                            source.ChunkBaseY + source.Height - 1),
                        PlanePosY,
                        ref TransparentPlanePosY,
                        index);
                }
            }

            SetFaceSolidFromPlanes();
        }

        private void WriteGeneratedBoundaryColumn(
            GeneratedChunkSpanData source,
            in BlockColumnProfile column,
            ulong[] opaquePlane,
            ref ushort[] transparentPlane,
            int localBaseIndex)
        {
            WriteGeneratedBoundaryRange(
                source,
                column.StoneStart,
                column.StoneEnd,
                source.StoneBlockId,
                opaquePlane,
                ref transparentPlane,
                localBaseIndex);
            WriteGeneratedBoundaryRange(
                source,
                column.SoilStart,
                column.SoilEnd,
                source.SoilBlockId,
                opaquePlane,
                ref transparentPlane,
                localBaseIndex);
            WriteGeneratedBoundaryRange(
                source,
                column.WaterStart,
                column.WaterEnd,
                source.WaterBlockId,
                opaquePlane,
                ref transparentPlane,
                localBaseIndex);
        }

        private void WriteGeneratedBoundaryRange(
            GeneratedChunkSpanData source,
            int worldStart,
            int worldEnd,
            ushort blockId,
            ulong[] opaquePlane,
            ref ushort[] transparentPlane,
            int localBaseIndex)
        {
            if (worldStart < 0 || worldEnd < worldStart)
                return;

            int chunkEnd = source.ChunkBaseY + source.Height - 1;
            int clippedStart = Math.Max(worldStart, source.ChunkBaseY);
            int clippedEnd = Math.Min(worldEnd, chunkEnd);
            if (clippedStart > clippedEnd)
                return;

            int startIndex = localBaseIndex + clippedStart - source.ChunkBaseY;
            int length = clippedEnd - clippedStart + 1;
            if (TerrainLoader.IsOpaque(blockId))
            {
                SetPlaneBitRange(opaquePlane, startIndex, length);
                return;
            }

            EnsureTransparentPlaneArrays();
            Array.Fill(transparentPlane, blockId, startIndex, length);
        }

        private void WriteGeneratedBoundaryCell(
            ushort blockId,
            ulong[] opaquePlane,
            ref ushort[] transparentPlane,
            int index)
        {
            if (blockId == 0)
                return;
            if (TerrainLoader.IsOpaque(blockId))
            {
                SetPlaneBit(opaquePlane, index);
                return;
            }

            EnsureTransparentPlaneArrays();
            transparentPlane[index] = blockId;
        }

        private static void SetPlaneBitRange(
            ulong[] plane,
            int startIndex,
            int length)
        {
            int endIndex = startIndex + length - 1;
            int firstWord = startIndex >> 6;
            int lastWord = endIndex >> 6;
            int firstBit = startIndex & 63;
            int lastBit = endIndex & 63;

            if (firstWord == lastWord)
            {
                ulong mask = (ulong.MaxValue << firstBit) &
                    (ulong.MaxValue >> (63 - lastBit));
                plane[firstWord] |= mask;
                return;
            }

            plane[firstWord] |= ulong.MaxValue << firstBit;
            if (lastWord - firstWord > 1)
                Array.Fill(plane, ulong.MaxValue, firstWord + 1, lastWord - firstWord - 1);
            plane[lastWord] |= ulong.MaxValue >> (63 - lastBit);
        }
    }
}

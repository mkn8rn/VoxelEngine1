using System.Runtime.CompilerServices;

namespace MVoxelEngine1.Infrastructure.Models.Generation
{
    public struct BlockColumnProfile
    {
        public int StoneStart;
        public int StoneEnd;
        public int SoilStart;
        public int SoilEnd;
        public int WaterStart;
        public int WaterEnd;
    }

    public sealed class GeneratedChunkSpanData
    {
        public GeneratedChunkSpanData(
            BlockColumnProfile[] columns,
            int width,
            int height,
            int depth,
            int chunkBaseY,
            ushort stoneBlockId,
            ushort soilBlockId,
            ushort waterBlockId,
            byte materialMask = 0b111,
            bool orderedContiguousSpans = false)
        {
            ArgumentNullException.ThrowIfNull(columns);
            if (width <= 0 || height <= 0 || depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (columns.Length != checked(width * depth))
                throw new ArgumentException("Column count does not match chunk dimensions.", nameof(columns));
            if ((materialMask & ~0b111) != 0)
                throw new ArgumentOutOfRangeException(nameof(materialMask));

            Columns = columns;
            Width = width;
            Height = height;
            Depth = depth;
            ChunkBaseY = chunkBaseY;
            StoneBlockId = stoneBlockId;
            SoilBlockId = soilBlockId;
            WaterBlockId = waterBlockId;
            MaterialMask = materialMask;
            OrderedContiguousSpans = orderedContiguousSpans;
        }

        public BlockColumnProfile[] Columns { get; }

        public int Width { get; }

        public int Height { get; }

        public int Depth { get; }

        public int ChunkBaseY { get; }

        public ushort StoneBlockId { get; }

        public ushort SoilBlockId { get; }

        public ushort WaterBlockId { get; }

        public byte MaterialMask { get; }

        public bool OrderedContiguousSpans { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetBlockLocal(int x, int y, int z)
        {
            if ((uint)x >= (uint)Width ||
                (uint)y >= (uint)Height ||
                (uint)z >= (uint)Depth)
            {
                return 0;
            }

            return GetBlockWorld(Columns[x * Depth + z], ChunkBaseY + y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetBlockWorld(in BlockColumnProfile column, int worldY)
        {
            if (column.StoneStart >= 0 &&
                column.StoneEnd >= column.StoneStart &&
                worldY >= column.StoneStart &&
                worldY <= column.StoneEnd)
                return StoneBlockId;
            if (column.SoilStart >= 0 &&
                column.SoilEnd >= column.SoilStart &&
                worldY >= column.SoilStart &&
                worldY <= column.SoilEnd)
                return SoilBlockId;
            if (column.WaterStart >= 0 &&
                column.WaterEnd >= column.WaterStart &&
                worldY >= column.WaterStart &&
                worldY <= column.WaterEnd)
                return WaterBlockId;
            return 0;
        }
    }
}

using System;
using System.Runtime.CompilerServices;

namespace MVoxelEngine1.Infrastructure.Models.Generation
{
    internal struct ColumnData
    {
        public byte RunCount; // 0,1,2 or 255 for escalated
        public ushort Id0, Id1;
        public byte Y0Start, Y0End, Y1Start, Y1End; // second run only if RunCount==2
        public ushort[] Escalated; // length 16 when escalated (RunCount==255)
        // Incrementally maintained fast-path metadata
        public ushort OccMask; // 16-bit occupancy (bit y)
        public byte NonAir; // number of solid voxels in this column (<=16)
        public byte AdjY; // vertical adjacency pairs inside column (sum over runs len-1)
    }
}

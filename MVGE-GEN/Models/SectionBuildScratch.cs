using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Models
{
    // Scratch structures for two-phase build
    public sealed class SectionBuildScratch
    {
        private const int COLUMN_COUNT = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE; // 256 columns of 16 voxels each
        public ColumnData[] Columns = new ColumnData[COLUMN_COUNT];
        public ushort[] Distinct = new ushort[8]; // up to 8 distinct ids before we give up and escalate densely
        public int DistinctCount;
        public bool AnyEscalated;
        public bool AnyNonAir;
        public bool DistinctDirty; // when true rebuild distinct list at finalize
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            if (DistinctCount > 0)
                Array.Clear(Distinct, 0, DistinctCount);
            DistinctCount = 0; AnyEscalated = false; AnyNonAir = false; DistinctDirty = false;
            for (int i = 0; i < COLUMN_COUNT; i++) Columns[i].RunCount = 0;
        }
    }
    public struct ColumnData
    {
        public byte RunCount; // 0,1,2 or 255 for escalated
        public ushort Id0, Id1;
        public byte Y0Start, Y0End, Y1Start, Y1End; // second run only if RunCount==2
        public ushort[] Escalated; // length 16 when escalated (RunCount==255)
    }
}

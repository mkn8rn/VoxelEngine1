using System;
using System.Runtime.CompilerServices;

namespace MVGE_INF.Generation.Models
{
    // Scratch structures for two-phase build
    public sealed class SectionBuildScratch
    {
        private const int COLUMN_COUNT = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE; // 256 columns
        public ColumnData[] Columns = new ColumnData[COLUMN_COUNT];
        // Per-column stamps so we can lazily treat stale columns as empty without writing RunCount=0 every reset.
        private ushort[] _columnStamps = new ushort[COLUMN_COUNT];
        internal ushort ActiveStamp; // current stamp value for this scratch instance

        public ushort[] Distinct = new ushort[8]; // up to 8 distinct ids
        public int DistinctCount;
        public bool AnyEscalated;
        public bool AnyNonAir;
        public bool DistinctDirty; // when true rebuild distinct list at finalize

        public ulong[] NonEmptyColumnBits = new ulong[4]; // 256 columns -> 4 words
        public int NonEmptyCount;

        // Per-id column membership bitsets (DistinctCount <=8). Each row = 256 bits -> 4 ulongs.
        // A bit set means the column *currently* (prior to replacement batch) contains that id in at least one run/voxel.
        public ulong[,] IdColumnBits = new ulong[8,4];

        // Global rolling stamp (ushort wrap). Managed externally via SectionUtils when renting.
        internal static ushort GlobalStamp; // not thread-safe increment; we accept rare collision (wrap) => fallback to full clear

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            // Reset distinct list
            if (DistinctCount > 0)
                Array.Clear(Distinct, 0, DistinctCount);
            DistinctCount = 0; AnyEscalated = false; AnyNonAir = false; DistinctDirty = false;
            // Clear bitsets
            Array.Clear(IdColumnBits, 0, IdColumnBits.Length);
            // Advance stamp; on wrap we perform a full manual clear of stamps + columns to re-sync.
            ushort next = (ushort)(GlobalStamp + 1);
            GlobalStamp = next;
            ActiveStamp = next;
            if (next == 0)
            {
                // Rare wrap (every 65k resets). Do a full clear to avoid stale columns misread.
                Array.Clear(_columnStamps, 0, _columnStamps.Length);
                for (int i = 0; i < COLUMN_COUNT; i++)
                {
                    Columns[i].RunCount = 0; // ensure consistent
                    Columns[i].Escalated = null;
                    Columns[i].OccMask = 0;
                    Columns[i].NonAir = 0;
                    Columns[i].AdjY = 0;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref ColumnData GetWritableColumn(int columnIndex)
        {
            if (_columnStamps[columnIndex] != ActiveStamp)
            {
                // Treat as empty; reset minimal fields
                _columnStamps[columnIndex] = ActiveStamp;
                Columns[columnIndex].RunCount = 0;
                Columns[columnIndex].Escalated = null; // release escalated reference (GC) if any
                Columns[columnIndex].OccMask = 0;
                Columns[columnIndex].NonAir = 0;
                Columns[columnIndex].AdjY = 0;
            }
            return ref Columns[columnIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref ColumnData GetReadonlyColumn(int columnIndex)
        {
            if (_columnStamps[columnIndex] != ActiveStamp)
            {
                // Return a ref to a zeroed temp static if stale? Instead, ensure stamp and zero RunCount lazily.
                _columnStamps[columnIndex] = ActiveStamp;
                Columns[columnIndex].RunCount = 0;
                Columns[columnIndex].Escalated = null;
                Columns[columnIndex].OccMask = 0;
                Columns[columnIndex].NonAir = 0;
                Columns[columnIndex].AdjY = 0;
            }
            return ref Columns[columnIndex];
        }
    }

    public struct ColumnData
    {
        public byte RunCount; // 0,1,2 or 255 for escalated
        public ushort Id0, Id1;
        public byte Y0Start, Y0End, Y1Start, Y1End; // second run only if RunCount==2
        public ushort[] Escalated; // length 16 when escalated (RunCount==255)
        // Incrementally maintained fast-path metadata
        public ushort OccMask;   // 16-bit occupancy (bit y)
        public byte NonAir;      // number of solid voxels in this column (<=16)
        public byte AdjY;        // vertical adjacency pairs inside column (sum over runs len-1)
    }
}

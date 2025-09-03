using MVGE_INF.Generation.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Utils
{
    internal partial class SectionUtils
    {
        // -------------------------------------------------------------------------------------------------
        // EscalatedAddRun:
        // Inserts a vertical span [yStart, yEnd] (inclusive) with blockId into the column at (localX, localZ).
        // The column can be in one of these states:
        //   Empty  (RunCount == 0)
        //   Single run (RunCount == 1)
        //   Two runs (RunCount == 2)
        //   Escalated per‑voxel (RunCount == 255)
        // Flow:
        //   1. Reject invalid / AIR input.
        //   2. Acquire scratch column.
        //   3. If full-height span:
        //        * For empty / single / two-run columns build or replace a single full run.
        //        * For escalated column: downgrade to single run overwriting data.
        //   4. If escalated (per‑voxel) column (non full-height case): write ids directly into the 16‑entry array and update metadata bits.
        //   5. If empty column: create first run and set metadata.
        //   6. If single run:
        //        * Same id and overlapping / touching → merge (simple boundary extension if purely above/below).
        //        * Strictly above (disjoint) → become two runs.
        //        * Overlap with different id → escalate to per‑voxel.
        //   7. If two runs:
        //        * Extend second run when contiguous.
        //        * If inserting span that bridges two runs of same id producing a single contiguous run → collapse to one run.
        //        * If after applying span both runs of same id become fully contiguous → collapse.
        //        * Any overlap / fragmentation not handled by the above → escalate.
        //   8. Escalation path: materialize/augment 16‑cell array, copy prior runs if first escalation, apply new span, update metadata.
        //   Metadata per column (OccMask, NonAir, AdjY) is updated incrementally whenever new bits are added.
        // -------------------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EscalatedAddRun(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (blockId == AIR || (uint)yStart > 15 || (uint)yEnd > 15 || yEnd < yStart) return;

            var scratch = GetScratch(sec);
            scratch.AnyNonAir = true;
            sec.StructuralDirty = true; // any added run changes occupancy / geometry

            int ci = localZ * S + localX;
            ref var col = ref scratch.GetWritableColumn(ci);

            // Full-height span handling for non-escalated columns (RunCount <= 2)
            if (yStart == 0 && yEnd == 15 && col.RunCount <= 2)
            {
                if (col.RunCount == 0)
                {
                    col.RunCount = 1;
                    col.Id0 = blockId;
                    col.Y0Start = 0; col.Y0End = 15;
                    col.OccMask = 0xFFFF;
                    col.NonAir = 16;
                    col.AdjY = 15;
                    TrackDistinct(scratch, blockId, ci);
                    return;
                }
                if (col.RunCount == 1 && col.Y0Start == 0 && col.Y0End == 15)
                {
                    if (col.Id0 != blockId)
                    {
                        col.Id0 = blockId;
                        TrackDistinct(scratch, blockId, ci);
                    }
                    return;
                }
                if (col.RunCount == 2)
                {
                    // Overwrite both runs with a single full run
                    col.RunCount = 1;
                    col.Id0 = blockId;
                    col.Y0Start = 0; col.Y0End = 15;
                    col.OccMask = 0xFFFF; col.NonAir = 16; col.AdjY = 15;
                    TrackDistinct(scratch, blockId, ci);
                    col.Id1 = 0; col.Y1Start = col.Y1End = 0;
                    return;
                }
            }
            // Escalated full overwrite (separate so above guard can stay cheap)
            if (yStart == 0 && yEnd == 15 && col.RunCount == 255)
            {
                if (col.Escalated != null)
                {
                    ReturnEscalatedColumn(col.Escalated);
                    col.Escalated = null;
                }
                col.RunCount = 1;
                col.Id0 = blockId;
                col.Y0Start = 0; col.Y0End = 15;
                col.OccMask = 0xFFFF; col.NonAir = 16; col.AdjY = 15;
                TrackDistinct(scratch, blockId, ci);
                return;
            }

            ushort segMask = MaskRange(yStart, yEnd);

            // Escalated path (non full-height)
            if (col.RunCount == 255)
            {
                var arr = col.Escalated ??= RentEscalatedColumn();
                for (int y = yStart; y <= yEnd; y++) arr[y] = blockId;
                // Inline mask update
                ushort prev = col.OccMask;
                ushort added = (ushort)(segMask & ~prev);
                if (added != 0)
                {
                    col.OccMask = (ushort)(prev | segMask);
                    col.NonAir += (byte)BitOperations.PopCount(added);
                    int internalPairs = BitOperations.PopCount((uint)(added & (added << 1)));
                    int bridging = BitOperations.PopCount((uint)((added << 1) & prev)) + BitOperations.PopCount((uint)((added >> 1) & prev));
                    col.AdjY = (byte)(col.AdjY + internalPairs + bridging);
                    TrackDistinct(scratch, blockId, ci);
                }
                else if (blockId != col.Id0 && blockId != col.Id1)
                {
                    TrackDistinct(scratch, blockId, ci);
                }
                return;
            }

            // Empty column fast path (occ mask still zero)
            if (col.RunCount == 0)
            {
                col.RunCount = 1;
                col.Id0 = blockId;
                col.Y0Start = (byte)yStart; col.Y0End = (byte)yEnd;
                // Direct metadata without popcount
                int spanLen = yEnd - yStart + 1;
                col.OccMask = segMask;
                col.NonAir = (byte)spanLen;
                col.AdjY = (byte)(spanLen - 1);
                TrackDistinct(scratch, blockId, ci);
                return;
            }

            // Single-run modifications
            if (col.RunCount == 1)
            {
                if (col.Id0 == blockId)
                {
                    // Contiguous pure top extension
                    if (yStart == col.Y0End + 1)
                    {
                        int addLen = yEnd - yStart + 1;
                        col.Y0End = (byte)yEnd;
                        col.NonAir += (byte)addLen;
                        col.AdjY += (byte)addLen; // (addLen-1 internal) + 1 bridging
                        col.OccMask = MaskRange(col.Y0Start, col.Y0End);
                        return;
                    }
                    // Contiguous pure bottom extension
                    if (yEnd == col.Y0Start - 1)
                    {
                        int addLen = yEnd - yStart + 1;
                        col.Y0Start = (byte)yStart;
                        col.NonAir += (byte)addLen;
                        col.AdjY += (byte)addLen; // (addLen-1 internal) + 1 bridging
                        col.OccMask = MaskRange(col.Y0Start, col.Y0End);
                        return;
                    }
                    // General overlap / touching merge
                    if (!(yEnd < col.Y0Start - 1 || yStart > col.Y0End + 1))
                    {
                        int newStart = Math.Min(col.Y0Start, yStart);
                        int newEnd = Math.Max(col.Y0End, yEnd);
                        ushort mergedMask = MaskRange(newStart, newEnd);
                        // Inline mask update
                        ushort prev2 = col.OccMask;
                        ushort added2 = (ushort)(mergedMask & ~prev2);
                        if (added2 != 0)
                        {
                            col.OccMask = mergedMask;
                            col.NonAir += (byte)BitOperations.PopCount(added2);
                            int internalPairs = BitOperations.PopCount((uint)(added2 & (added2 << 1)));
                            int bridging = BitOperations.PopCount((uint)((added2 << 1) & prev2)) + BitOperations.PopCount((uint)((added2 >> 1) & prev2));
                            col.AdjY = (byte)(col.AdjY + internalPairs + bridging);
                        }
                        col.Y0Start = (byte)newStart;
                        col.Y0End = (byte)newEnd;
                        return;
                    }
                }
                if (yStart > col.Y0End) // strictly above existing run without touching
                {
                    col.RunCount = 2;
                    col.Id1 = blockId;
                    col.Y1Start = (byte)yStart; col.Y1End = (byte)yEnd;
                    // Mask update (previous mask unchanged scenario)
                    ushort prev = col.OccMask;
                    ushort added = (ushort)(segMask & ~prev);
                    if (added != 0)
                    {
                        col.OccMask = (ushort)(prev | segMask);
                        col.NonAir += (byte)(yEnd - yStart + 1);
                        // Internal + bridging pairs
                        int internalPairs = (yEnd - yStart); // contiguous segment
                        int bridging = BitOperations.PopCount((uint)((added << 1) & prev)) + BitOperations.PopCount((uint)((added >> 1) & prev));
                        col.AdjY = (byte)(col.AdjY + internalPairs + bridging);
                    }
                    TrackDistinct(scratch, blockId, ci);
                    return;
                }
                // Overlap with different id -> escalate
            }
            else if (col.RunCount == 2)
            {
                // Extend second run (contiguous top extension)
                if (blockId == col.Id1 && yStart == col.Y1End + 1)
                {
                    int addLen = yEnd - yStart + 1;
                    col.Y1End = (byte)yEnd;
                    col.NonAir += (byte)addLen;
                    col.AdjY += (byte)addLen;
                    col.OccMask |= (ushort)(segMask); // segMask contiguous above existing second run
                    return;
                }
                // Bridging insertion between both runs with same id -> collapse to single run
                if (col.Id0 == blockId && col.Id1 == blockId && yStart == col.Y0End + 1 && yEnd == col.Y1Start - 1)
                {
                    int newStart = col.Y0Start;
                    int newEnd = col.Y1End;
                    ushort mergedMask = MaskRange(newStart, newEnd);
                    ushort prev = col.OccMask;
                    ushort added = (ushort)(mergedMask & ~prev);
                    if (added != 0)
                    {
                        col.OccMask = mergedMask;
                        col.NonAir += (byte)BitOperations.PopCount(added);
                        int internalPairs = BitOperations.PopCount((uint)(added & (added << 1)));
                        int bridging = BitOperations.PopCount((uint)((added << 1) & prev)) + BitOperations.PopCount((uint)((added >> 1) & prev));
                        col.AdjY = (byte)(col.AdjY + internalPairs + bridging);
                    }
                    col.RunCount = 1;
                    col.Id0 = blockId;
                    col.Y0Start = (byte)newStart; col.Y0End = (byte)newEnd;
                    col.Id1 = 0; col.Y1Start = col.Y1End = 0;
                    return;
                }
                // Attempt bridging fill that makes combined mask contiguous & same id -> collapse
                if (col.Id0 == blockId && col.Id1 == blockId)
                {
                    int minStart = Math.Min(col.Y0Start, Math.Min(yStart, col.Y1Start));
                    int maxEnd = Math.Max(col.Y1End, Math.Max(yEnd, col.Y0End));
                    ushort spanMask = MaskRange(minStart, maxEnd);
                    ushort futureOcc = (ushort)(col.OccMask | segMask);
                    if ((futureOcc & spanMask) == spanMask)
                    {
                        ushort prev = col.OccMask;
                        ushort added = (ushort)(spanMask & ~prev);
                        if (added != 0)
                        {
                            col.OccMask = spanMask;
                            col.NonAir += (byte)BitOperations.PopCount(added);
                            int internalPairs = BitOperations.PopCount((uint)(added & (added << 1)));
                            int bridging = BitOperations.PopCount((uint)((added << 1) & prev)) + BitOperations.PopCount((uint)((added >> 1) & prev));
                            col.AdjY = (byte)(col.AdjY + internalPairs + bridging);
                        }
                        col.RunCount = 1;
                        col.Id0 = blockId;
                        col.Y0Start = (byte)minStart; col.Y0End = (byte)maxEnd;
                        col.Id1 = 0; col.Y1Start = col.Y1End = 0;
                        return;
                    }
                }
                // Any overlap with either run or fragmentation -> escalate
            }

            // Escalate (overlap / interior insertion / fragmentation)
            scratch.AnyEscalated = true;
            var full = col.Escalated ?? RentEscalatedColumn();

            if (col.RunCount != 255 && col.Escalated == null)
            {
                if (col.RunCount >= 1)
                {
                    for (int y = col.Y0Start; y <= col.Y0End; y++) full[y] = col.Id0;
                }
                if (col.RunCount == 2)
                {
                    for (int y = col.Y1Start; y <= col.Y1End; y++) full[y] = col.Id1;
                }
            }
            for (int y = yStart; y <= yEnd; y++) full[y] = blockId;
            col.Escalated = full;
            col.RunCount = 255;
            // Inline mask update
            {
                ushort prev = col.OccMask;
                ushort added = (ushort)(segMask & ~prev);
                if (added != 0)
                {
                    col.OccMask = (ushort)(prev | segMask);
                    col.NonAir += (byte)BitOperations.PopCount(added);
                    int internalPairs = BitOperations.PopCount((uint)(added & (added << 1)));
                    int bridging = BitOperations.PopCount((uint)((added << 1) & prev)) + BitOperations.PopCount((uint)((added >> 1) & prev));
                    col.AdjY = (byte)(col.AdjY + internalPairs + bridging);
                    TrackDistinct(scratch, blockId, ci);
                }
                else if (blockId == col.Id0 || blockId == col.Id1) { /* already tracked */ }
                else TrackDistinct(scratch, blockId, ci);
            }
        }


    }
}

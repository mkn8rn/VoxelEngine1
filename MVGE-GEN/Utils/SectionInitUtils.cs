using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MVGE_GEN.Models;
using MVGE_INF.Models.Terrain;
using static MVGE_GEN.Models.ChunkSection; // added for BaseBlockType

namespace MVGE_GEN.Utils
{
    public static partial class SectionUtils
    {
        public static bool EnableFastSectionClassification = true;
        private const int S = ChunkSection.SECTION_SIZE;
        private const int AIR = ChunkSection.AIR;
        private const int COLUMN_COUNT = S * S; // 256

        // ---------------- Scratch pooling ----------------
        private static readonly ConcurrentBag<SectionBuildScratch> _scratchPool = new();
        private static SectionBuildScratch RentScratch()
        {
            if (_scratchPool.TryTake(out var sc)) { sc.Reset(); return sc; }
            return new SectionBuildScratch();
        }
        private static void ReturnScratch(SectionBuildScratch sc)
        {
            if (sc == null) return;
            _scratchPool.Add(sc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SectionBuildScratch GetScratch(ChunkSection sec)
        {
            return sec.BuildScratch as SectionBuildScratch ?? (SectionBuildScratch)(sec.BuildScratch = RentScratch());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void TrackDistinct(SectionBuildScratch sc, ushort id)
        {
            if (id == AIR) return;
            int dc = sc.DistinctCount;
            for (int i = 0; i < dc; i++) if (sc.Distinct[i] == id) return;
            if (dc < sc.Distinct.Length) sc.Distinct[dc] = id;
            sc.DistinctCount = dc < sc.Distinct.Length ? dc + 1 : dc; // clamp when full (we do not expand here)
        }

        // Public helper to convert a uniform section into scratch runs (used when partial replacement rules break uniformity)
        public static void ConvertUniformSectionToScratch(ChunkSection sec)
        {
            if (sec == null || sec.Kind != ChunkSection.RepresentationKind.Uniform) return;
            var scratch = GetScratch(sec);
            ushort id = sec.UniformBlockId;
            for (int z = 0; z < S; z++)
            for (int x = 0; x < S; x++)
            {
                int ci = z * S + x; ref var col = ref scratch.Columns[ci];
                col.RunCount = 1; col.Id0 = id; col.Y0Start = 0; col.Y0End = 15;
            }
            TrackDistinct(scratch, id);
            scratch.AnyNonAir = true;
            sec.Kind = ChunkSection.RepresentationKind.Empty; // will be decided at finalize
            sec.MetadataBuilt = false;
            sec.UniformBlockId = 0;
            sec.CompletelyFull = false;
        }

        // New lightweight run append used during generation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddRun(ChunkSection sec, int localX, int localZ, int yStart, int yEnd, ushort blockId)
        {
            if (blockId == AIR || yEnd < yStart) return;
            var scratch = GetScratch(sec);
            scratch.AnyNonAir = true;
            int ci = localZ * S + localX;
            ref var col = ref scratch.Columns[ci];

            if (col.RunCount == 255)
            {
                var arr = col.Escalated ??= new ushort[S];
                for (int y = yStart; y <= yEnd; y++) arr[y] = blockId;
                TrackDistinct(scratch, blockId);
                return;
            }
            if (col.RunCount == 0)
            {
                col.Id0 = blockId; col.Y0Start = (byte)yStart; col.Y0End = (byte)yEnd; col.RunCount = 1;
                TrackDistinct(scratch, blockId); return;
            }
            if (col.RunCount == 1)
            {
                // merge or add second
                if (blockId == col.Id0 && yStart == col.Y0End + 1)
                { col.Y0End = (byte)yEnd; return; }
                if (yStart > col.Y0End)
                { col.Id1 = blockId; col.Y1Start = (byte)yStart; col.Y1End = (byte)yEnd; col.RunCount = 2; TrackDistinct(scratch, blockId); return; }
            }
            if (col.RunCount == 2)
            {
                // try extend last
                if (blockId == col.Id1 && yStart == col.Y1End + 1) { col.Y1End = (byte)yEnd; return; }
            }
            // escalate (rare path)
            scratch.AnyEscalated = true; var full = col.Escalated ?? new ushort[S];
            // replay existing (only once on first escalation)
            if (col.RunCount != 255)
            {
                if (col.RunCount >= 1)
                {
                    for (int y = col.Y0Start; y <= col.Y0End; y++) full[y] = col.Id0;
                    if (col.RunCount == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) full[y] = col.Id1;
                }
            }
            for (int y = yStart; y <= yEnd; y++) full[y] = blockId;
            col.Escalated = full; col.RunCount = 255; TrackDistinct(scratch, blockId);
        }

        // Apply replacement rule against scratch columns for a vertical slice [lyStart, lyEnd] (inclusive)
        // Optimized to avoid per-column escalation when a run is fully contained in the slice even if not full section cover.
        public static void ApplyReplacement(ChunkSection sec, int lyStart, int lyEnd, bool fullCover, Func<ushort, BaseBlockType> baseTypeGetter, Func<ushort, BaseBlockType, bool> predicate, ushort replacementId)
        {
            if (sec == null) return;
            var scratch = GetScratch(sec);
            int distinctCount = scratch.DistinctCount;
            if (distinctCount == 0) return;

            // Precompute targeted distinct ids ONCE (avoid repeated baseTypeGetter + predicate calls per voxel).
            // DistinctCount <= 8 => use stackalloc for zero GC.
            Span<ushort> ids = stackalloc ushort[distinctCount];
            Span<bool> targeted = stackalloc bool[distinctCount];
            bool anyTarget = false;
            for (int i = 0; i < distinctCount; i++)
            {
                ushort id = scratch.Distinct[i];
                ids[i] = id;
                if (id == replacementId) { targeted[i] = false; continue; }
                var bt = baseTypeGetter(id);
                bool t = predicate(id, bt);
                targeted[i] = t;
                if (t) anyTarget = true;
            }
            if (!anyTarget) return; // nothing to do

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IdMatches(ushort id, Span<ushort> idsLocal, Span<bool> targetedLocal)
            {
                for (int i = 0; i < idsLocal.Length; i++)
                {
                    if (idsLocal[i] == id) return targetedLocal[i];
                }
                return false;
            }

            bool anyChange = false;

            // Fast full-cover path: no need to escalate, we replace whole runs directly.
            if (fullCover)
            {
                for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                {
                    ref var col = ref scratch.Columns[z * S + x];
                    byte rc = col.RunCount; if (rc == 0) continue;
                    if (rc == 255)
                    {
                        var arr = col.Escalated; // length 16
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y]; if (id == AIR || id == replacementId) continue;
                            if (IdMatches(id, ids, targeted)) { arr[y] = replacementId; anyChange = true; }
                        }
                        continue;
                    }
                    if (rc >= 1 && col.Id0 != replacementId && IdMatches(col.Id0, ids, targeted)) { col.Id0 = replacementId; anyChange = true; }
                    if (rc == 2 && col.Id1 != replacementId && IdMatches(col.Id1, ids, targeted)) { col.Id1 = replacementId; anyChange = true; }
                }
                if (anyChange) scratch.DistinctDirty = true;
                return;
            }

            // Partial-cover path
            for (int z = 0; z < S; z++)
            for (int x = 0; x < S; x++)
            {
                ref var col = ref scratch.Columns[z * S + x];
                byte rc = col.RunCount; if (rc == 0) continue;
                if (rc == 255)
                {
                    var arr = col.Escalated;
                    for (int y = lyStart; y <= lyEnd; y++)
                    {
                        ushort id = arr[y]; if (id == AIR || id == replacementId) continue;
                        if (IdMatches(id, ids, targeted)) { arr[y] = replacementId; anyChange = true; }
                    }
                    continue;
                }
                // First run
                if (rc >= 1 && RangesOverlap(col.Y0Start, col.Y0End, lyStart, lyEnd) && col.Id0 != replacementId && IdMatches(col.Id0, ids, targeted))
                {
                    bool runContained = lyStart <= col.Y0Start && lyEnd >= col.Y0End;
                    if (runContained)
                    {
                        col.Id0 = replacementId; anyChange = true;
                    }
                    else
                    {
                        // Need escalation since only part of the run is replaced
                        scratch.AnyEscalated = true; var arr = col.Escalated ?? new ushort[S];
                        if (col.Escalated == null)
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                            if (rc == 2) for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                        }
                        int ys = Math.Max(col.Y0Start, lyStart); int ye = Math.Min(col.Y0End, lyEnd);
                        for (int y = ys; y <= ye; y++) arr[y] = replacementId;
                        col.Escalated = arr; col.RunCount = 255; anyChange = true; continue; // skip second run (copied already if existed)
                    }
                }
                // Second run
                if (rc == 2 && RangesOverlap(col.Y1Start, col.Y1End, lyStart, lyEnd) && col.Id1 != replacementId && IdMatches(col.Id1, ids, targeted))
                {
                    bool runContained = lyStart <= col.Y1Start && lyEnd >= col.Y1End;
                    if (runContained)
                    {
                        col.Id1 = replacementId; anyChange = true;
                    }
                    else
                    {
                        scratch.AnyEscalated = true; var arr = col.Escalated ?? new ushort[S];
                        if (col.Escalated == null)
                        {
                            for (int y = col.Y0Start; y <= col.Y0End; y++) arr[y] = col.Id0;
                            for (int y = col.Y1Start; y <= col.Y1End; y++) arr[y] = col.Id1;
                        }
                        int ys = Math.Max(col.Y1Start, lyStart); int ye = Math.Min(col.Y1End, lyEnd);
                        for (int y = ys; y <= ye; y++) arr[y] = replacementId;
                        col.Escalated = arr; col.RunCount = 255; anyChange = true;
                    }
                }
            }
            if (anyChange) scratch.DistinctDirty = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RangesOverlap(int a0, int a1, int b0, int b1) => a0 <= b1 && b0 <= a1;

        // Finalize after generation + replacements
        public static void FinalizeSection(ChunkSection sec)
        {
            if (sec == null) return;
            var scratch = sec.BuildScratch as SectionBuildScratch;
            if (scratch == null || !scratch.AnyNonAir)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty;
                sec.IsAllAir = true;
                sec.MetadataBuilt = true;
                sec.VoxelCount = S * S * S;
                if (scratch != null) { sec.BuildScratch = null; ReturnScratch(scratch); }
                return;
            }
            if (scratch.DistinctDirty)
            {
                // Rebuild distinct list (cheap: at most 8 kept)
                Span<ushort> tmp = stackalloc ushort[8]; int count = 0;
                for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                {
                    ref var col = ref scratch.Columns[z * S + x];
                    byte rc = col.RunCount; if (rc == 0) continue;
                    if (rc == 255)
                    {
                        var arr = col.Escalated;
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y]; if (id == AIR) continue; bool found=false; for (int i=0;i<count;i++) if (tmp[i]==id){found=true;break;} if (!found && count<8) tmp[count++]=id;
                        }
                        continue;
                    }
                    if (rc >=1 && col.Id0 != AIR)
                    { bool f=false; for(int i=0;i<count;i++) if (tmp[i]==col.Id0){f=true;break;} if(!f && count<8) tmp[count++]=col.Id0; }
                    if (rc==2 && col.Id1 != AIR)
                    { bool f=false; for(int i=0;i<count;i++) if (tmp[i]==col.Id1){f=true;break;} if(!f && count<8) tmp[count++]=col.Id1; }
                }
                scratch.DistinctCount = count;
                for (int i=0;i<count;i++) scratch.Distinct[i] = tmp[i];
                scratch.DistinctDirty = false;
            }
            sec.VoxelCount = S * S * S;
            // First pass build occupancy + count + bounds
            ulong[] occ = sec.OccupancyBits = new ulong[64]; // 4096 bits
            int nonAir = 0;
            bool boundsInit = false;
            byte minX=0, maxX=0, minY=0, maxY=0, minZ=0, maxZ=0;
            int adjX=0, adjY=0, adjZ=0;

            for (int z = 0; z < S; z++)
            {
                int zOffset = z * S;
                for (int x = 0; x < S; x++)
                {
                    ref var col = ref scratch.Columns[zOffset + x];
                    byte rc = col.RunCount; if (rc == 0) continue;
                    if (rc == 255)
                    {
                        var arr = col.Escalated;
                        int lastY = -1;
                        for (int y = 0; y < S; y++)
                        {
                            ushort id = arr[y]; if (id == AIR) continue;
                            int li = y * 256 + z * S + x; occ[li >> 6] |= 1UL << (li & 63); nonAir++;
                            if (!boundsInit){minX=maxX=(byte)x;minY=maxY=(byte)y;minZ=maxZ=(byte)z;boundsInit=true;} else { if (x<minX)minX=(byte)x; if (x>maxX)maxX=(byte)x; if (y<minY)minY=(byte)y; if (y>maxY)maxY=(byte)y; if (z<minZ)minZ=(byte)z; if (z>maxZ)maxZ=(byte)z; }
                            if (lastY>=0 && y==lastY+1) adjY++; lastY=y;
                        }
                        continue;
                    }
                    // run0
                    if (rc >= 1)
                    {
                        for (int y = col.Y0Start; y <= col.Y0End; y++)
                        {
                            int li = y * 256 + z * S + x; occ[li >> 6] |= 1UL << (li & 63); nonAir++;
                            if (!boundsInit){minX=maxX=(byte)x;minY=maxY=(byte)y;minZ=maxZ=(byte)z;boundsInit=true;} else { if (x<minX)minX=(byte)x; if (x>maxX)maxX=(byte)x; if (y<minY)minY=(byte)y; if (y>maxY)maxY=(byte)y; if (z<minZ)minZ=(byte)z; if (z>maxZ)maxZ=(byte)z; }
                            if (y>col.Y0Start) adjY++;
                        }
                    }
                    if (rc == 2)
                    {
                        for (int y = col.Y1Start; y <= col.Y1End; y++)
                        {
                            int li = y * 256 + z * S + x; occ[li >> 6] |= 1UL << (li & 63); nonAir++;
                            if (!boundsInit){minX=maxX=(byte)x;minY=maxY=(byte)y;minZ=maxZ=(byte)z;boundsInit=true;} else { if (x<minX)minX=(byte)x; if (x>maxX)maxX=(byte)x; if (y<minY)minY=(byte)y; if (y>maxY)maxY=(byte)y; if (z<minZ)minZ=(byte)z; if (z>maxZ)maxZ=(byte)z; }
                            if (y>col.Y1Start) adjY++;
                        }
                    }
                }
            }

            sec.NonAirCount = nonAir;
            if (!boundsInit)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty; sec.IsAllAir = true; sec.MetadataBuilt = true; ReturnScratch(scratch); sec.BuildScratch = null; return;
            }
            sec.HasBounds = true; sec.MinLX=minX; sec.MaxLX=maxX; sec.MinLY=minY; sec.MaxLY=maxY; sec.MinLZ=minZ; sec.MaxLZ=maxZ;

            // X and Z adjacency via occupancy scan of neighbors
            for (int y = 0; y < S; y++)
            {
                int yBase = y * 256;
                for (int z = 0; z < S; z++)
                {
                    int rowBase = yBase + z * S;
                    for (int x = 0; x < S; x++)
                    {
                        int li = rowBase + x;
                        bool filled = (occ[li >> 6] & (1UL << (li & 63))) != 0;
                        if (!filled) continue;
                        if (x>0)
                        {
                            int leftLi = li - 1; if ((occ[leftLi >> 6] & (1UL << (leftLi & 63))) != 0) adjX++;
                        }
                        if (z>0)
                        {
                            int backLi = li - S; if ((occ[backLi >> 6] & (1UL << (backLi & 63))) != 0) adjZ++;
                        }
                    }
                }
            }

            int exposure = 6 * nonAir - 2 * (adjX + adjY + adjZ);
            sec.InternalExposure = exposure;

            // Decide representation
            if (nonAir == S*S*S && scratch.DistinctCount == 1)
            {
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = scratch.Distinct[0];
                sec.IsAllAir = false; sec.CompletelyFull = true;
            }
            else if (nonAir <= 128)
            {
                int[] idx = new int[nonAir]; ushort[] blocks = new ushort[nonAir];
                int p = 0; ushort singleId = scratch.DistinctCount==1 ? scratch.Distinct[0] : (ushort)0;
                for (int word = 0; word < occ.Length; word++)
                {
                    ulong w = occ[word];
                    while (w!=0){int bit = System.Numerics.BitOperations.TrailingZeroCount(w); int li = (word<<6)+bit; idx[p]=li; blocks[p]= singleId!=0? singleId : ResolveBlockId(ref scratch.Columns[(li & 255)], li); p++; w&=w-1;}
                }
                sec.Kind = ChunkSection.RepresentationKind.Sparse;
                sec.SparseIndices = idx; sec.SparseBlocks = blocks; sec.IsAllAir=false;
            }
            else
            {
                if (scratch.DistinctCount == 1)
                {
                    sec.Kind = ChunkSection.RepresentationKind.Packed;
                    sec.Palette = new List<ushort>{AIR, scratch.Distinct[0]};
                    sec.PaletteLookup = new Dictionary<ushort,int>{{AIR,0},{scratch.Distinct[0],1}};
                    sec.BitsPerIndex = 1; sec.BitData = new uint[ (4096 +31)/32 ];
                    for (int word=0; word<occ.Length; word++)
                    {
                        ulong w = occ[word];
                        while (w!=0){int bit = System.Numerics.BitOperations.TrailingZeroCount(w); int li=(word<<6)+bit; int bw = li>>5; int bo = li &31; sec.BitData[bw] |= 1u<<bo; w&=w-1;}
                    }
                    sec.IsAllAir=false;
                }
                else
                {
                    sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
                    var dense = sec.ExpandedDense = new ushort[4096];
                    for (int z=0; z<S; z++)
                    for (int x=0; x<S; x++)
                    {
                        ref var col = ref scratch.Columns[z*S + x];
                        byte rc = col.RunCount; if (rc==0) continue;
                        if (rc==255){var arr=col.Escalated; for (int y=0;y<S;y++){ushort id=arr[y]; if (id!=AIR) dense[(y*256)+(z*S)+x]=id;}}
                        else
                        {
                            for (int y=col.Y0Start; y<=col.Y0End; y++) dense[(y*256)+(z*S)+x]=col.Id0;
                            if (rc==2) for (int y=col.Y1Start; y<=col.Y1End; y++) dense[(y*256)+(z*S)+x]=col.Id1;
                        }
                    }
                    sec.IsAllAir=false;
                }
            }
            sec.MetadataBuilt = true;
            sec.BuildScratch = null; ReturnScratch(scratch); // release back to pool
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ResolveBlockId(ref ColumnData col, int li)
        {
            int y = li / 256; // since li = y*256 + baseXZ
            if (col.RunCount==255){ return col.Escalated[y]; }
            if (col.RunCount==0) return AIR;
            if (col.RunCount>=1 && y>=col.Y0Start && y<=col.Y0End) return col.Id0;
            if (col.RunCount==2 && y>=col.Y1Start && y<=col.Y1End) return col.Id1;
            return AIR;
        }
    }
}

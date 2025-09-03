using System;
using System.IO;
using MVGE_INF.Managers;
using MVGE_GEN.Terrain;
using MVGE_INF.Generation.Models;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MVGE_GEN
{
    public partial class WorldResources
    {
        // ===========================
        //  CHUNK / BATCH SERIALIZATION
        // ===========================
        // Each batch file contains all generated chunks for a 32x32 horizontal footprint (bx,bz)
        // covering every generated vertical layer present during initial generation (currently LoD1+1 vertical span). 
        //
        // A batch file layout:
        //   Header:
        //     0  : 4 bytes  BATCH_MAGIC ("MVBH")
        //     4  : 2 bytes  BATCH_FILE_VERSION
        //     6  : 2 bytes  reserved/padding
        //     8  : 4 bytes  batchX (int)
        //    12  : 4 bytes  batchZ (int)
        //    16  : 4 bytes  chunkRecordCount (int)
        //   Repeated chunkRecordCount times:
        //     ChunkRecordHeader:
        //       0 : 4 bytes chunkCx (int)
        //       4 : 4 bytes chunkCy (int)
        //       8 : 4 bytes chunkCz (int)
        //      12 : 4 bytes payloadLength (int)
        //     Followed by payloadLength bytes containing single-chunk binary format
        // ===========================
        // Single-chunk binary format
        // Header:
        //  0  : 4  bytes  Magic "MVCH"
        //  4  : 2  bytes  Version (ushort)
        //  6  : 2  bytes  Reserved / padding
        //  8  : 4  bytes  chunkIndexX (int)
        // 12  : 4  bytes  chunkIndexY (int)
        // 16  : 4  bytes  chunkIndexZ (int)
        // 20  : 4  bytes  sectionsX (int)
        // 24  : 4  bytes  sectionsY (int)
        // 28  : 4  bytes  sectionsZ (int)
        // 32  : 4  bytes  sectionCount (int)
        // 36  : sectionCount * 4 bytes : uint offsets (from file start) to each section record
        // Section Record at offset:
        //  [0]   : 1 byte  kind (ChunkSection.RepresentationKind)
        //  [1]   : 2 bytes payloadLength (ushort) (length after this field)
        //  [3+]  : payload bytes:
        //          Metadata (present for all kinds):
        //              NonAirCount (ushort)
        //              InternalExposure (int)
        //              MetaFlags (byte) bit0 HasBounds, bit1 HasOccupancy, bit2 CompletelyFull, bit3 MetadataBuilt, bit4 IsAllAir, bit5 StructuralDirty, bit6 IdMapDirty, bit7 BoundingBoxDirty (version >=2)
        //              (if HasBounds) 6 bytes: MinLX,MinLY,MinLZ,MaxLX,MaxLY,MaxLZ
        //              (if HasOccupancy)  (OccupancyBits 4*8) + 6 faces * (4*8) = 25 * 8 = 200 bytes (each preceded by length byte per array)
        //          Representation-specific data appended after metadata:
        //              Empty: (no additional)
        //              Uniform: blockId (ushort)
        //              Sparse: count (ushort) then count * (index ushort, blockId ushort)
        //              DenseExpanded: 4096 * 2 bytes raw voxel ids
        //              Packed / MultiPacked: bitsPerIndex (byte), paletteCount (ushort), palette ids (ushort*count), bitDataWordCount (int), bitData words (uint*wordCount)
        //                                    palette lookup mapping present flag (byte 0/1) then if 1: ushort mappingCount then mappingCount*(blockId ushort, index ushort)
        // Footer (version >=2 only, appended AFTER all section payloads & after offset table has been backfilled):
        //  0  : 4 bytes CHUNK_FOOTER_MAGIC (CMD2)
        //  4  : float temperature
        //  8  : float humidity
        // 12  : uint FlagsA bitfield: 0 AllAirChunk,1 AllStoneChunk,2 AllSoilChunk,3 AllOneBlockChunk,4 FullyBuried,5 BuriedByNeighbors
        // 16  : byte FaceFlags bit0..5 = FaceSolidNegX..FaceSolidPosZ
        // 17  : byte OcclusionStatus
        // 18  : reserved / alignment (2 bytes)
        // 20  : ushort AllOneBlockBlockId
        // 22  : byte MeshStatsPresent (0/1)
        // 23  : reserved
        // 24+ : if MeshStatsPresent -> 11 * 4-byte ints (SolidCount, ExposureEstimate, MinX,MinY,MinZ,MaxX,MaxY,MaxZ, XNonEmpty,YNonEmpty,ZNonEmpty) + 1 byte HasStats flag (for clarity)
        //  ... : 6 * (int length + ulong[length] data) for PlaneNegX,PlanePosX,PlaneNegY,PlanePosY,PlaneNegZ,PlanePosZ

        private const ushort CHUNK_SAVE_VERSION = 1; // legacy chunk format version retained
        private static readonly byte[] CHUNK_MAGIC = new byte[] { (byte)'M', (byte)'V', (byte)'C', (byte)'H' }; // "MVCH"
        private static readonly byte[] CHUNK_FOOTER_MAGIC = new byte[] { (byte)'C', (byte)'M', (byte)'D'};      // "CMD" footer marker

        private const ushort BATCH_FILE_VERSION = 1;
        private static readonly byte[] BATCH_MAGIC = new byte[] { (byte)'M', (byte)'V', (byte)'B', (byte)'H' }; // "MVBH" batch header

        // --------------------- Batch File Path Helpers ---------------------
        private string GetBatchFilePath(int bx, int bz)
        {
            var settings = GameManager.settings;
            string worldRoot = Path.Combine(settings.savesWorldDirectory, loader.ID.ToString());
            string regionRoot = Path.Combine(worldRoot, loader.RegionID.ToString());
            string batchesDir = Path.Combine(regionRoot, "batches");
            Directory.CreateDirectory(batchesDir);
            return Path.Combine(batchesDir, $"batch{bx}x{bz}.bin");
        }
        private bool BatchFileExists(int bx,int bz) => File.Exists(GetBatchFilePath(bx,bz));

        // Lightweight probe: chunk considered saved if its batch file exists.
        private bool ChunkFileExists(int cx,int cy,int cz)
        {
            var (bx,bz) = BatchKeyFromChunk(cx, cz);
            return BatchFileExists(bx, bz);
        }

        // --------------------- Batch Save ---------------------
        // Writes an entire batch file with inline legacy chunk payloads. Any existing file is replaced.
        private void SaveBatch(int bx,int bz)
        {
            if (!loadedBatches.TryGetValue((bx,bz), out var batch)) return;
            var chunkList = batch.Chunks; // snapshot under lock
            string path = GetBatchFilePath(bx,bz);
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 256*1024, FileOptions.None);
                using var bw = new BinaryWriter(fs);

                // Batch header
                bw.Write(BATCH_MAGIC);
                bw.Write(BATCH_FILE_VERSION);
                bw.Write((ushort)0); // padding
                bw.Write(bx);
                bw.Write(bz);
                int count = 0;
                foreach (var _ in chunkList) count++;
                bw.Write(count);

                // Serialize each chunk using legacy layout into an in-memory buffer then emit
                foreach (var ch in chunkList)
                {
                    int sizeX = GameManager.settings.chunkMaxX;
                    int sizeY = GameManager.settings.chunkMaxY;
                    int sizeZ = GameManager.settings.chunkMaxZ;
                    int cx = (int)Math.Floor(ch.position.X / sizeX);
                    int cy = (int)Math.Floor(ch.position.Y / sizeY);
                    int cz = (int)Math.Floor(ch.position.Z / sizeZ);

                    using var cms = new MemoryStream(128*1024);
                    using (var cbw = new BinaryWriter(cms, System.Text.Encoding.UTF8, leaveOpen:true))
                    {
                        try
                        {
                            WriteSingleChunkBinary(cbw, ch);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[World] Failed serializing chunk ({cx},{cy},{cz}) into batch ({bx},{bz}): {ex.Message}");
                            continue; // skip this chunk record
                        }
                    }
                    byte[] payload = cms.ToArray();
                    bw.Write(cx); bw.Write(cy); bw.Write(cz);
                    bw.Write(payload.Length);
                    bw.Write(payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Failed to save batch ({bx},{bz}): {ex.Message}");
            }
        }

        // Individual chunk serialization into binary format
        private void WriteSingleChunkBinary(BinaryWriter bw, Chunk chunk)
        {
            int dimX = GameManager.settings.chunkMaxX;
            int dimY = GameManager.settings.chunkMaxY;
            int dimZ = GameManager.settings.chunkMaxZ;
            int cix = (int)Math.Floor(chunk.position.X / dimX);
            int ciy = (int)Math.Floor(chunk.position.Y / dimY);
            int ciz = (int)Math.Floor(chunk.position.Z / dimZ);

            int sectionCount = chunk.sectionsX * chunk.sectionsY * chunk.sectionsZ;
            bw.Write(CHUNK_MAGIC);
            bw.Write(CHUNK_SAVE_VERSION);
            bw.Write((ushort)0); // padding
            bw.Write(cix); bw.Write(ciy); bw.Write(ciz);
            bw.Write(chunk.sectionsX); bw.Write(chunk.sectionsY); bw.Write(chunk.sectionsZ);
            bw.Write(sectionCount);

            long offsetsStart = bw.BaseStream.Position;
            for (int i = 0; i < sectionCount; i++) bw.Write(0u); // placeholder offsets

            Span<uint> offsets = sectionCount <= 1024 ? stackalloc uint[sectionCount] : new uint[sectionCount];
            int S = ChunkSection.SECTION_SIZE;
            int denseVoxelCount = S * S * S;
            Span<ushort> denseBuffer = stackalloc ushort[denseVoxelCount];

            int sectionLinear = 0;
            for (int sx = 0; sx < chunk.sectionsX; sx++)
            {
                for (int sy = 0; sy < chunk.sectionsY; sy++)
                {
                    for (int sz = 0; sz < chunk.sectionsZ; sz++, sectionLinear++)
                    {
                        var sec = chunk.sections[sx, sy, sz];
                        long sectionOffset = bw.BaseStream.Position;
                        if (sectionOffset > uint.MaxValue)
                        {
                            Console.WriteLine("[World] Chunk inline payload exceeded 4GB offset range; aborting chunk write.");
                            return; // abort entire chunk write (corrupt prevention)
                        }
                        offsets[sectionLinear] = (uint)sectionOffset;

                        var kind = sec?.Kind ?? ChunkSection.RepresentationKind.Empty;
                        bw.Write((byte)kind);

                        using var ms = new MemoryStream(512);
                        using (var ps = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                        {
                            ushort nonAir = (ushort)(sec?.NonAirCount ?? 0);
                            ps.Write(nonAir);
                            ps.Write(sec?.InternalExposure ?? 0);
                            bool hasBounds = sec?.HasBounds == true;
                            bool hasOcc = sec != null && sec.OccupancyBits != null && sec.FaceNegXBits != null && sec.FacePosXBits != null && sec.FaceNegYBits != null && sec.FacePosYBits != null && sec.FaceNegZBits != null && sec.FacePosZBits != null;
                            byte metaFlags = 0;
                            if (hasBounds) metaFlags |= 1;              // bit0
                            if (hasOcc) metaFlags |= 1 << 1;            // bit1
                            if (sec != null && sec.CompletelyFull) metaFlags |= 1 << 2;      // bit2
                            if (sec != null && sec.MetadataBuilt) metaFlags |= 1 << 3;       // bit3
                            if (sec != null && sec.IsAllAir) metaFlags |= 1 << 4;            // bit4
                            if (sec != null && sec.StructuralDirty) metaFlags |= 1 << 5;     // bit5
                            if (sec != null && sec.IdMapDirty) metaFlags |= 1 << 6;          // bit6
                            if (sec != null && sec.BoundingBoxDirty) metaFlags |= 1 << 7;    // bit7
                            ps.Write(metaFlags);
                            if (hasBounds && sec != null)
                            {
                                ps.Write(sec.MinLX);
                                ps.Write(sec.MinLY);
                                ps.Write(sec.MinLZ);
                                ps.Write(sec.MaxLX);
                                ps.Write(sec.MaxLY);
                                ps.Write(sec.MaxLZ);
                            }
                            if (hasOcc && sec != null)
                            {
                                void WriteUlongArray(ulong[] arr)
                                {
                                    ps.Write((byte)arr.Length);
                                    for (int i = 0; i < arr.Length; i++) ps.Write(arr[i]);
                                }
                                WriteUlongArray(sec.OccupancyBits);
                                WriteUlongArray(sec.FaceNegXBits);
                                WriteUlongArray(sec.FacePosXBits);
                                WriteUlongArray(sec.FaceNegYBits);
                                WriteUlongArray(sec.FacePosYBits);
                                WriteUlongArray(sec.FaceNegZBits);
                                WriteUlongArray(sec.FacePosZBits);
                            }

                            switch (kind)
                            {
                                case ChunkSection.RepresentationKind.Empty:
                                    break;
                                case ChunkSection.RepresentationKind.Uniform when sec != null:
                                    ps.Write(sec.UniformBlockId);
                                    break;
                                case ChunkSection.RepresentationKind.Sparse when sec != null:
                                    if (sec.SparseIndices == null || sec.SparseBlocks == null)
                                    {
                                        ps.Write((ushort)0);
                                    }
                                    else
                                    {
                                        int scount = sec.SparseIndices.Length;
                                        if (scount > ushort.MaxValue) scount = ushort.MaxValue;
                                        ps.Write((ushort)scount);
                                        for (int i = 0; i < scount; i++)
                                        {
                                            int idx = sec.SparseIndices[i];
                                            if ((uint)idx <= 4095)
                                            {
                                                ps.Write((ushort)idx);
                                                ps.Write(sec.SparseBlocks[i]);
                                            }
                                        }
                                    }
                                    break;
                                case ChunkSection.RepresentationKind.DenseExpanded when sec != null:
                                    if (sec.ExpandedDense != null && sec.ExpandedDense.Length == denseVoxelCount)
                                    {
                                        var raw = MemoryMarshal.AsBytes(sec.ExpandedDense.AsSpan());
                                        ps.Write(raw);
                                    }
                                    else
                                    {
                                        denseBuffer.Clear();
                                        var raw = MemoryMarshal.AsBytes(denseBuffer);
                                        ps.Write(raw);
                                    }
                                    break;
                                case ChunkSection.RepresentationKind.Packed when sec != null:
                                case ChunkSection.RepresentationKind.MultiPacked when sec != null:
                                    ps.Write((byte)sec.BitsPerIndex);
                                    ushort paletteCount = (ushort)(sec.Palette?.Count ?? 0);
                                    ps.Write(paletteCount);
                                    if (paletteCount > 0 && sec.Palette != null)
                                    {
                                        for (int i = 0; i < paletteCount; i++) ps.Write(sec.Palette[i]);
                                    }
                                    int wordCount = sec.BitData?.Length ?? 0;
                                    ps.Write(wordCount);
                                    if (wordCount > 0 && sec.BitData != null)
                                    {
                                        for (int i = 0; i < wordCount; i++) ps.Write(sec.BitData[i]);
                                    }
                                    if (sec.PaletteLookup != null)
                                    {
                                        ps.Write((byte)1); // present flag
                                        ps.Write((ushort)sec.PaletteLookup.Count);
                                        foreach (var kv in sec.PaletteLookup)
                                        {
                                            ps.Write(kv.Key);
                                            ps.Write((ushort)kv.Value);
                                        }
                                    }
                                    else
                                    {
                                        ps.Write((byte)0); // no mapping
                                    }
                                    break;
                            }
                        }
                        ushort payloadLen = (ushort)Math.Min(ms.Length, ushort.MaxValue);
                        bw.Write(payloadLen);
                        bw.Write(ms.GetBuffer(), 0, payloadLen);
                    }
                }

                // Back-fill offsets
                long endPos = bw.BaseStream.Position;
                bw.BaseStream.Position = offsetsStart;
                for (int i = 0; i < sectionCount; i++) bw.Write(offsets[i]);
                bw.BaseStream.Position = endPos;

                // Footer (legacy exact format)
                bw.Write(CHUNK_FOOTER_MAGIC);
                bw.Write((float)chunk.chunkData.temperature);
                bw.Write((float)chunk.chunkData.humidity);
                uint flagsA = 0;
                if (chunk.AllAirChunk) flagsA |= 1u << 0;
                if (chunk.AllStoneChunk) flagsA |= 1u << 1;
                if (chunk.AllSoilChunk) flagsA |= 1u << 2;
                if (chunk.AllOneBlockChunk) flagsA |= 1u << 3;
                if (chunk.FullyBuried) flagsA |= 1u << 4;
                if (chunk.BuriedByNeighbors) flagsA |= 1u << 5;
                bw.Write(flagsA);
                byte faceFlags = 0;
                if (chunk.FaceSolidNegX) faceFlags |= 1 << 0;
                if (chunk.FaceSolidPosX) faceFlags |= 1 << 1;
                if (chunk.FaceSolidNegY) faceFlags |= 1 << 2;
                if (chunk.FaceSolidPosY) faceFlags |= 1 << 3;
                if (chunk.FaceSolidNegZ) faceFlags |= 1 << 4;
                if (chunk.FaceSolidPosZ) faceFlags |= 1 << 5;
                bw.Write(faceFlags);
                bw.Write((byte)chunk.OcclusionStatus);
                bw.Write((ushort)0); // padding
                bw.Write(chunk.AllOneBlockBlockId);
                bool statsPresent = chunk.MeshPrepassStats.HasStats;
                bw.Write((byte)(statsPresent ? 1 : 0));
                bw.Write((byte)0); // padding
                if (statsPresent)
                {
                    var s = chunk.MeshPrepassStats;
                    bw.Write(s.SolidCount);
                    bw.Write(s.ExposureEstimate);
                    bw.Write(s.MinX); bw.Write(s.MinY); bw.Write(s.MinZ);
                    bw.Write(s.MaxX); bw.Write(s.MaxY); bw.Write(s.MaxZ);
                    bw.Write(s.XNonEmpty); bw.Write(s.YNonEmpty); bw.Write(s.ZNonEmpty);
                    bw.Write((byte)(s.HasStats ? 1 : 0));
                }
                void WritePlane(ulong[] plane)
                {
                    if (plane == null) { bw.Write(0); return; }
                    bw.Write(plane.Length);
                    for (int i = 0; i < plane.Length; i++) bw.Write(plane[i]);
                }
                WritePlane(chunk.PlaneNegX);
                WritePlane(chunk.PlanePosX);
                WritePlane(chunk.PlaneNegY);
                WritePlane(chunk.PlanePosY);
                WritePlane(chunk.PlaneNegZ);
                WritePlane(chunk.PlanePosZ);
            }
        }

        // --------------------- Batch Load ---------------------
        // Loads a batch file (if present) and materializes all chunk payloads into unbuiltChunks dictionary.
        // Returns the specific requested chunk if present, else null. Granular error handling mirrors legacy style.
        private Chunk LoadBatchForChunk(int targetCx,int targetCy,int targetCz)
        {
            var (bx,bz) = BatchKeyFromChunk(targetCx, targetCz);
            string path = GetBatchFilePath(bx,bz);
            if (!File.Exists(path)) return null;
            Chunk requested = null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256*1024, FileOptions.SequentialScan);
                using var br = new BinaryReader(fs);

                // Batch header
                var magic = br.ReadBytes(4);
                if (magic.Length != 4 || magic[0]!=BATCH_MAGIC[0] || magic[1]!=BATCH_MAGIC[1] || magic[2]!=BATCH_MAGIC[2] || magic[3]!=BATCH_MAGIC[3]) return null;
                ushort ver = br.ReadUInt16(); br.ReadUInt16(); // padding
                int fileBx = br.ReadInt32(); int fileBz = br.ReadInt32();
                if (fileBx != bx || fileBz != bz) return null;
                int chunkCount = br.ReadInt32();
                var batch = GetOrCreateBatch(bx,bz);

                // Iterate chunk records
                for (int i=0;i<chunkCount;i++)
                {
                    if (fs.Position + 16 > fs.Length) break; // insufficient header bytes for next record
                    int cx = br.ReadInt32(); int cy = br.ReadInt32(); int cz = br.ReadInt32();
                    int payloadLen = br.ReadInt32();
                    if (payloadLen <= 0 || fs.Position + payloadLen > fs.Length)
                    {
                        Console.WriteLine($"[World] Batch ({bx},{bz}) corrupt chunk payload length at index {i} (len={payloadLen}). Aborting remaining.");
                        break;
                    }
                    byte[] payload = br.ReadBytes(payloadLen);
                    var key = (cx, cy, cz);
                    if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key))
                    {
                        if (cx==targetCx && cy==targetCy && cz==targetCz)
                            requested = unbuiltChunks.GetValueOrDefault(key) ?? activeChunks.GetValueOrDefault(key);
                        continue; // already loaded
                    }
                    var chunk = ParseInlineChunkPayload(cx, cy, cz, payload);
                    if (chunk != null)
                    {
                        unbuiltChunks[key] = chunk;
                        batch.AddOrReplaceChunk(chunk, cx, cy, cz);
                        if (cx==targetCx && cy==targetCy && cz==targetCz) requested = chunk;
                    }
                }
                // After load schedule any visible ones
                ScheduleVisibleChunksInBatch(bx,bz);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Failed to load batch ({bx},{bz}): {ex.Message}");
            }
            return requested;
        }

        // Parses one inline chunk payload (legacy format) with granular defensive checks mirroring original loader.
        private Chunk ParseInlineChunkPayload(int cx,int cy,int cz, byte[] payload)
        {
            string phase = "Start";
            try
            {
                using var ms = new MemoryStream(payload, false);
                using var br = new BinaryReader(ms);
                if (ms.Length < 40) return null; // minimal header + small offset table

                phase = "ReadMagic";
                var magic = br.ReadBytes(4);
                if (magic.Length != 4 || magic[0]!=CHUNK_MAGIC[0] || magic[1]!=CHUNK_MAGIC[1] || magic[2]!=CHUNK_MAGIC[2] || magic[3]!=CHUNK_MAGIC[3]) return null;

                phase = "ReadHeaderFields";
                ushort ver = br.ReadUInt16(); br.ReadUInt16();
                int fileCx = br.ReadInt32(); int fileCy = br.ReadInt32(); int fileCz = br.ReadInt32();
                int sxCount = br.ReadInt32(); int syCount = br.ReadInt32(); int szCount = br.ReadInt32(); int sectionCount = br.ReadInt32();

                phase = "ValidateSectionGrid";
                int expectedSX = GameManager.settings.chunkMaxX / ChunkSection.SECTION_SIZE;
                int expectedSY = GameManager.settings.chunkMaxY / ChunkSection.SECTION_SIZE;
                int expectedSZ = GameManager.settings.chunkMaxZ / ChunkSection.SECTION_SIZE;
                if (fileCx!=cx || fileCy!=cy || fileCz!=cz) return null;
                if (sxCount!=expectedSX || syCount!=expectedSY || szCount!=expectedSZ) return null;
                if (sectionCount != sxCount*syCount*szCount) return null;

                phase = "ReadOffsets";
                long offsetsStart = ms.Position;
                if (offsetsStart + (long)sectionCount*4 > ms.Length) return null;
                uint[] offsets = new uint[sectionCount];
                for (int i=0;i<sectionCount;i++) offsets[i] = br.ReadUInt32();

                phase = "ConstructChunk";
                int baseX = cx * GameManager.settings.chunkMaxX;
                int baseY = cy * GameManager.settings.chunkMaxY;
                int baseZ = cz * GameManager.settings.chunkMaxZ;
                Chunk chunk;
                try { chunk = new Chunk(new Vector3(baseX, baseY, baseZ), loader.seed, loader.currentWorldSaveDirectory, null, autoGenerate:false); }
                catch (Exception ctorEx)
                {
                    Console.WriteLine($"[World] Chunk ctor failed ({cx},{cy},{cz}) phase={phase}: {ctorEx.Message}");
                    return null;
                }
                if (chunk.sections == null || chunk.sections.GetLength(0)!=sxCount)
                    chunk.sections = new ChunkSection[sxCount, syCount, szCount];

                phase = "ParseSections";
                int linear=0;
                for (int sx=0; sx<sxCount; sx++)
                {
                    for (int sy=0; sy<syCount; sy++)
                    {
                        for (int sz=0; sz<szCount; sz++, linear++)
                        {
                            uint off = offsets[linear];
                            string sectionTag = $"sec({sx},{sy},{sz})";
                            if (off==0) { chunk.sections[sx,sy,sz] = new ChunkSection(); continue; }
                            try
                            {
                                phase = $"SeekSection:{sectionTag}";
                                if (off + 3 > ms.Length) { chunk.sections[sx,sy,sz] = new ChunkSection(); continue; }
                                ms.Position = off;
                                byte kindByte = br.ReadByte();
                                var kind = (ChunkSection.RepresentationKind)kindByte;
                                ushort payloadLen = br.ReadUInt16();
                                if (payloadLen==0 || ms.Position + payloadLen > ms.Length) { chunk.sections[sx,sy,sz] = new ChunkSection(); continue; }
                                phase = $"ReadSectionPayload:{sectionTag}";
                                byte[] spayload = br.ReadBytes(payloadLen);
                                if (spayload.Length != payloadLen) { chunk.sections[sx,sy,sz] = new ChunkSection(); continue; }
                                phase = $"ParseSectionPayload:{sectionTag}";
                                var sec = ParseSectionFromPayload(kind, spayload);
                                chunk.sections[sx,sy,sz] = sec ?? new ChunkSection();
                            }
                            catch (Exception exSection)
                            {
                                Console.WriteLine($"[World] Exception parsing {sectionTag} c=({cx},{cy},{cz}) phase={phase}: {exSection.Message}");
                                chunk.sections[sx,sy,sz] = new ChunkSection();
                            }
                        }
                    }
                }

                phase = "Footer";
                if (ms.Position + CHUNK_FOOTER_MAGIC.Length <= ms.Length)
                {
                    byte[] fmagic = br.ReadBytes(CHUNK_FOOTER_MAGIC.Length);
                    bool footerOk = fmagic.Length==CHUNK_FOOTER_MAGIC.Length && fmagic[0]==CHUNK_FOOTER_MAGIC[0] && fmagic[1]==CHUNK_FOOTER_MAGIC[1] && fmagic[2]==CHUNK_FOOTER_MAGIC[2];
                    long minimalFooterRemainder = 4+4+4+1+1+2+2+1+1; // temperature..flags.. etc.
                    if (footerOk && ms.Position + minimalFooterRemainder <= ms.Length)
                    {
                        try
                        {
                            phase = "FooterParse";
                            float tempF = br.ReadSingle();
                            float humF = br.ReadSingle();
                            chunk.chunkData.temperature = (byte)Math.Clamp((int)Math.Round(tempF),0,255);
                            chunk.chunkData.humidity = (byte)Math.Clamp((int)Math.Round(humF),0,255);
                            uint flagsA = br.ReadUInt32();
                            byte faceFlags = br.ReadByte();
                            byte occStatus = br.ReadByte();
                            br.ReadUInt16(); // padding
                            chunk.AllOneBlockBlockId = br.ReadUInt16();
                            byte statsPresent = br.ReadByte();
                            br.ReadByte(); // padding
                            chunk.AllAirChunk = (flagsA & (1u<<0))!=0;
                            chunk.AllStoneChunk = (flagsA & (1u<<1))!=0;
                            chunk.AllSoilChunk = (flagsA & (1u<<2))!=0;
                            chunk.AllOneBlockChunk = (flagsA & (1u<<3))!=0;
                            chunk.FullyBuried = (flagsA & (1u<<4))!=0;
                            chunk.BuriedByNeighbors = (flagsA & (1u<<5))!=0;
                            chunk.OcclusionStatus = (Chunk.OcclusionClass)occStatus;
                            chunk.FaceSolidNegX = (faceFlags & (1<<0))!=0;
                            chunk.FaceSolidPosX = (faceFlags & (1<<1))!=0;
                            chunk.FaceSolidNegY = (faceFlags & (1<<2))!=0;
                            chunk.FaceSolidPosY = (faceFlags & (1<<3))!=0;
                            chunk.FaceSolidNegZ = (faceFlags & (1<<4))!=0;
                            chunk.FaceSolidPosZ = (faceFlags & (1<<5))!=0;
                            if (statsPresent!=0 && ms.Position + (11*4)+1 <= ms.Length)
                            {
                                phase = "FooterStats";
                                var stats = chunk.MeshPrepassStats;
                                stats.SolidCount = br.ReadInt32();
                                stats.ExposureEstimate = br.ReadInt32();
                                stats.MinX = br.ReadInt32(); stats.MinY = br.ReadInt32(); stats.MinZ = br.ReadInt32();
                                stats.MaxX = br.ReadInt32(); stats.MaxY = br.ReadInt32(); stats.MaxZ = br.ReadInt32();
                                stats.XNonEmpty = br.ReadInt32(); stats.YNonEmpty = br.ReadInt32(); stats.ZNonEmpty = br.ReadInt32();
                                byte hasStats = br.ReadByte(); stats.HasStats = hasStats!=0; chunk.MeshPrepassStats = stats;
                            }
                            phase = "FooterPlanes";
                            ulong[] ReadPlane(){ if (ms.Position + 4 > ms.Length) return null; int len = br.ReadInt32(); if (len<=0 || ms.Position + len*8 > ms.Length) return null; var arr=new ulong[len]; for(int i=0;i<len;i++) arr[i]=br.ReadUInt64(); return arr; }
                            chunk.PlaneNegX = ReadPlane();
                            chunk.PlanePosX = ReadPlane();
                            chunk.PlaneNegY = ReadPlane();
                            chunk.PlanePosY = ReadPlane();
                            chunk.PlaneNegZ = ReadPlane();
                            chunk.PlanePosZ = ReadPlane();
                        }
                        catch (Exception fex)
                        {
                            Console.WriteLine($"[World] Footer parse error chunk ({cx},{cy},{cz}) phase={phase}: {fex.Message}");
                        }
                    }
                }
                return chunk;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Failed parsing inline chunk ({cx},{cy},{cz}) phase={phase}: {ex.Message}");
                return null;
            }
        }

        // --------------------- ChunkSection Payload Parser ---------------------
        // Reused section payload parser (unchanged from inline adaptation earlier)
        private ChunkSection ParseSectionFromPayload(ChunkSection.RepresentationKind kind, byte[] payload)
        {
            try
            {
                using var ms = new MemoryStream(payload,false);
                using var br = new BinaryReader(ms);
                if (payload.Length < 7) return new ChunkSection();
                var sec = new ChunkSection(); sec.Kind = kind; sec.VoxelCount = ChunkSection.SECTION_SIZE*ChunkSection.SECTION_SIZE*ChunkSection.SECTION_SIZE;
                sec.NonAirCount = br.ReadUInt16(); sec.InternalExposure = br.ReadInt32(); byte metaFlags = br.ReadByte();
                bool hasBounds = (metaFlags & 1)!=0; bool hasOcc = (metaFlags & 2)!=0;
                if (hasBounds && ms.Position + 6 <= ms.Length){ sec.HasBounds=true; sec.MinLX=br.ReadByte(); sec.MinLY=br.ReadByte(); sec.MinLZ=br.ReadByte(); sec.MaxLX=br.ReadByte(); sec.MaxLY=br.ReadByte(); sec.MaxLZ=br.ReadByte(); }
                if (hasOcc){ ulong[] ReadOcc(){ if (ms.Position>=ms.Length) return null; int len = br.ReadByte(); if (len<=0 || ms.Position + len*8 > ms.Length) return null; var arr=new ulong[len]; for(int i=0;i<len;i++) arr[i]=br.ReadUInt64(); return arr; } sec.OccupancyBits=ReadOcc(); sec.FaceNegXBits=ReadOcc(); sec.FacePosXBits=ReadOcc(); sec.FaceNegYBits=ReadOcc(); sec.FacePosYBits=ReadOcc(); sec.FaceNegZBits=ReadOcc(); sec.FacePosZBits=ReadOcc(); }
                sec.CompletelyFull = (metaFlags & (1<<2))!=0; sec.MetadataBuilt = (metaFlags & (1<<3))!=0; sec.IsAllAir = (metaFlags & (1<<4))!=0; sec.StructuralDirty = (metaFlags & (1<<5))!=0; sec.IdMapDirty = (metaFlags & (1<<6))!=0; sec.BoundingBoxDirty = (metaFlags & (1<<7))!=0;
                switch(kind)
                {
                    case ChunkSection.RepresentationKind.Empty: sec.IsAllAir = true; break;
                    case ChunkSection.RepresentationKind.Uniform: if (ms.Position + 2 <= ms.Length) sec.UniformBlockId = br.ReadUInt16(); break;
                    case ChunkSection.RepresentationKind.Sparse:
                        if (ms.Position + 2 <= ms.Length){ int scount = br.ReadUInt16(); if (scount>=0 && scount<=4096 && ms.Position + scount*4 <= ms.Length){ sec.SparseIndices = new int[scount]; sec.SparseBlocks = new ushort[scount]; for(int i=0;i<scount;i++){ sec.SparseIndices[i]=br.ReadUInt16(); sec.SparseBlocks[i]=br.ReadUInt16(); } } }
                        break;
                    case ChunkSection.RepresentationKind.DenseExpanded:
                        int expectedBytes = sec.VoxelCount * 2; if (ms.Position + expectedBytes <= ms.Length){ sec.ExpandedDense = new ushort[sec.VoxelCount]; var raw = br.ReadBytes(expectedBytes); MemoryMarshal.Cast<byte,ushort>(raw).CopyTo(sec.ExpandedDense); }
                        break;
                    case ChunkSection.RepresentationKind.Packed:
                    case ChunkSection.RepresentationKind.MultiPacked:
                        if (ms.Position < ms.Length){ sec.BitsPerIndex = br.ReadByte(); if (ms.Position + 2 <= ms.Length){ int paletteCount = br.ReadUInt16(); if (paletteCount>=0 && paletteCount<=4096 && ms.Position + paletteCount*2 <= ms.Length){ sec.Palette = new List<ushort>(paletteCount); for(int i=0;i<paletteCount;i++) sec.Palette.Add(br.ReadUInt16()); if (ms.Position +4 <= ms.Length){ int wordCount = br.ReadInt32(); if (wordCount>=0 && ms.Position + wordCount*4 <= ms.Length){ if (wordCount>0){ sec.BitData = new uint[wordCount]; for(int i=0;i<wordCount;i++) sec.BitData[i]=br.ReadUInt32(); } } if (ms.Position < ms.Length){ byte mapPresent = br.ReadByte(); if (mapPresent!=0 && ms.Position + 2 <= ms.Length){ int mapCount = br.ReadUInt16(); if (mapCount>=0 && mapCount<=4096 && ms.Position + mapCount*4 <= ms.Length){ sec.PaletteLookup = new Dictionary<ushort,int>(mapCount); for(int mi=0;mi<mapCount;mi++){ ushort bid=br.ReadUInt16(); ushort idx=br.ReadUInt16(); sec.PaletteLookup[bid]=idx; } } } else if (sec.Palette!=null){ sec.PaletteLookup = new Dictionary<ushort,int>(sec.Palette.Count); for(int ii=0;ii<sec.Palette.Count;ii++) sec.PaletteLookup[sec.Palette[ii]]=ii; } } } } } }
                        break;
                }
                if ((kind==ChunkSection.RepresentationKind.Packed || kind==ChunkSection.RepresentationKind.MultiPacked) && sec.Palette!=null && sec.PaletteLookup==null){ sec.PaletteLookup = new Dictionary<ushort,int>(sec.Palette.Count); for(int i=0;i<sec.Palette.Count;i++) sec.PaletteLookup[sec.Palette[i]]=i; }
                return sec;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Section payload parse error kind={kind}: {ex.Message}");
                return new ChunkSection();
            }
        }
    }
}

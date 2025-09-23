using System;
using System.IO;
using MVGE_INF.Managers;
using MVGE_GEN.Terrain;
using MVGE_INF.Generation.Models;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using OpenTK.Mathematics;
using System.Security.Cryptography.X509Certificates;

namespace MVGE_GEN
{
    public partial class World
    {
        // ===========================
        //  CHUNK / QUAD SERIALIZATION
        // ===========================
        // Each quad file contains all generated chunks for a 16x16 horizontal footprint (bx,bz)
        // covering every generated vertical layer present during initial generation (currently LoD1+1 vertical span). 
        //
        // A quad file layout:
        //   Header:
        //     0  : 4 bytes  QUAD_MAGIC ("MVQH")
        //     4  : 2 bytes  QUAD_FILE_VERSION
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
        //              OpaqueCount (ushort)
        //              TransparentCount (ushort)
        //              EmptyCount (ushort)
        //              InternalExposure (int)
        //              MetaFlags (byte) bit layout:
        //                   bit0 HasBounds
        //                   bit1 HasOpaqueMasks
        //                   bit2 CompletelyFull
        //                   bit3 MetadataBuilt
        //                   bit4 IsAllAir
        //                   bit5 HasTransparentMasks
        //                   bit6 HasEmptyBits
        //                   bit7 reserved
        //              (if HasBounds) 6 bytes: MinLX,MinLY,MinLZ,MaxLX,MaxLY,MaxLZ
        //              (if HasOpaqueMasks)    OpaqueBits (len:1 byte, then ulong[len])
        //                                       FaceNegXBits, FacePosXBits, FaceNegYBits, FacePosYBits, FaceNegZBits, FacePosZBits (each as len:1 byte + ulong[len])
        //              (if HasTransparentMasks) TransparentBits (len:1 byte + ulong[len])
        //                                         TransparentFaceNegXBits ... TransparentFacePosZBits (each as len:1 byte + ulong[len])
        //              (if HasEmptyBits)       EmptyBits (len:1 byte + ulong[len])
        //          Representation-specific data appended after metadata:
        //              Empty: (no additional)
        //              Uniform: blockId (ushort)
        //              Expanded: 4096 * 2 bytes raw voxel ids
        //              Packed / MultiPacked: bitsPerIndex (byte), paletteCount (ushort), palette ids (ushort*count), bitDataWordCount (int), bitData words (uint*wordCount)
        // ===========================
        // Footer (appended AFTER all section payloads & after offset table has been backfilled):
        //  0  : 3 bytes CHUNK_FOOTER_MAGIC ("CMD")
        //  3  : float temperature
        //  7  : float humidity
        //  11 : uint FlagsA bitfield: 0 AllAirChunk,1 AllStoneChunk,2 AllSoilChunk,3 AllOneBlockChunk,4 FullyBuried,5 BuriedByNeighbors,6 AllWaterChunk
        //  15 : byte FaceFlags bit0..5 = FaceSolidNegX..FaceSolidPosZ
        //  16 : byte OcclusionStatus
        //  17 : reserved / alignment (1 byte)
        //  18 : ushort AllOneBlockBlockId
        //  ... : 6 * (int length + ulong[length] data) for PlaneNegX,PlanePosX,PlaneNegY,PlanePosY,PlaneNegZ,PlanePosZ
        //  ... : 6 * (int length + ushort[length] data) for TransparentPlaneNegX,TransparentPlanePosX,TransparentPlaneNegY,TransparentPlanePosY,TransparentPlaneNegZ,TransparentPlanePosZ

        private const ushort CHUNK_SAVE_VERSION = 1; // chunk format version retained
        private static readonly byte[] CHUNK_MAGIC = new byte[] { (byte)'M', (byte)'V', (byte)'C', (byte)'H' }; // "MVCH"
        private static readonly byte[] CHUNK_FOOTER_MAGIC = new byte[] { (byte)'C', (byte)'M', (byte)'D'};      // "CMD" footer marker

        private const ushort QUAD_FILE_VERSION = 1;
        private static readonly byte[] QUAD_MAGIC = new byte[] { (byte)'M', (byte)'V', (byte)'Q', (byte)'H' }; // "MVQH" quad header

        private DateTime lastFullWorldSave = DateTime.UtcNow;
        private int worldSaveIntervalMinutes = 12;

        // --------------------- Quad File Path Helpers ---------------------
        private string GetBatchFilePath(int bx, int bz)
        {
            var settings = GameManager.settings;
            string worldRoot = Path.Combine(settings.savesWorldDirectory, loader.ID.ToString());
            string regionRoot = Path.Combine(worldRoot, loader.RegionID.ToString());
            string quadsDir = Path.Combine(regionRoot, "quads");
            Directory.CreateDirectory(quadsDir);
            return Path.Combine(quadsDir, $"quad{bx}x{bz}.bin");
        }
        private bool BatchFileExists(int bx,int bz) => File.Exists(GetBatchFilePath(bx,bz));

        // Lightweight probe: chunk considered saved if its quad file exists.
        private bool ChunkFileExists(int cx,int cy,int cz)
        {
            var (bx,bz) = BatchKeyFromChunk(cx, cz);
            return BatchFileExists(bx, bz);
        }

        // --------------------- Quad Save ---------------------
        // Writes an entire quad file with inline chunk payloads. Any existing file is replaced.
        private void SaveBatch(int bx,int bz)
        {
            if (!loadedBatches.TryGetValue((bx,bz), out var batch)) return;
            var chunkList = batch.Chunks; // snapshot under lock
            string path = GetBatchFilePath(bx,bz);
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 256*1024, FileOptions.None);
                using var bw = new BinaryWriter(fs);

                // Quad header
                bw.Write(QUAD_MAGIC);
                bw.Write(QUAD_FILE_VERSION);
                bw.Write((ushort)0); // padding
                bw.Write(bx);
                bw.Write(bz);
                int count = 0;
                foreach (var _ in chunkList) count++;
                bw.Write(count);

                // Serialize each chunk into an in-memory buffer then emit
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
                            Console.WriteLine($"[World] Failed serializing chunk ({cx},{cy},{cz}) into quad ({bx},{bz}): {ex.Message}");
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
                Console.WriteLine($"[World] Failed to save quad ({bx},{bz}): {ex.Message}");
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
            int S = Section.SECTION_SIZE;
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
                            return; // abort entire chunk write
                        }
                        offsets[sectionLinear] = (uint)sectionOffset;

                        var kind = sec?.Kind ?? Section.RepresentationKind.Empty;
                        // Coerce unsupported kinds to Expanded
                        if (kind == (Section.RepresentationKind)2) kind = Section.RepresentationKind.Expanded;
                        bw.Write((byte)kind);

                        using var ms = new MemoryStream(512);
                        using (var ps = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                        {
                            // Counts
                            ushort opaqueCount = (ushort)(sec?.OpaqueVoxelCount ?? 0);
                            ushort transparentCount = (ushort)(sec?.TransparentCount ?? 0);
                            ushort emptyCount = (ushort)(sec?.EmptyCount ?? 0);
                            ps.Write(opaqueCount);
                            ps.Write(transparentCount);
                            ps.Write(emptyCount);
                            // Exposure
                            ps.Write(sec?.InternalExposure ?? 0);

                            // Meta flags
                            bool hasBounds = sec?.HasBounds == true;
                            bool hasOpaqueMasks = sec != null && sec.OpaqueBits != null &&
                                                  sec.FaceNegXBits != null && sec.FacePosXBits != null &&
                                                  sec.FaceNegYBits != null && sec.FacePosYBits != null &&
                                                  sec.FaceNegZBits != null && sec.FacePosZBits != null;
                            bool hasTransparentMasks = sec != null && (sec.TransparentBits != null ||
                                                     sec.TransparentFaceNegXBits != null || sec.TransparentFacePosXBits != null ||
                                                     sec.TransparentFaceNegYBits != null || sec.TransparentFacePosYBits != null ||
                                                     sec.TransparentFaceNegZBits != null || sec.TransparentFacePosZBits != null);
                            bool hasEmptyBits = sec != null && sec.EmptyBits != null;

                            byte metaFlags = 0;
                            if (hasBounds) metaFlags |= 1;              // bit0
                            if (hasOpaqueMasks) metaFlags |= 1 << 1;    // bit1
                            if (sec != null && sec.CompletelyFull) metaFlags |= 1 << 2; // bit2
                            if (sec != null && sec.MetadataBuilt) metaFlags |= 1 << 3;   // bit3
                            if (sec != null && sec.IsAllAir) metaFlags |= 1 << 4;        // bit4
                            if (hasTransparentMasks) metaFlags |= 1 << 5;                // bit5
                            if (hasEmptyBits) metaFlags |= 1 << 6;                       // bit6
                            ps.Write(metaFlags);

                            // Bounds
                            if (hasBounds && sec != null)
                            {
                                ps.Write(sec.MinLX);
                                ps.Write(sec.MinLY);
                                ps.Write(sec.MinLZ);
                                ps.Write(sec.MaxLX);
                                ps.Write(sec.MaxLY);
                                ps.Write(sec.MaxLZ);
                            }

                            // Helper to write ulong[] with a byte count prefix
                            static void WriteUlongArray(BinaryWriter w, ulong[] arr)
                            {
                                w.Write((byte)arr.Length);
                                for (int i = 0; i < arr.Length; i++) w.Write(arr[i]);
                            }

                            // Opaque masks
                            if (hasOpaqueMasks && sec != null)
                            {
                                WriteUlongArray(ps, sec.OpaqueBits);
                                WriteUlongArray(ps, sec.FaceNegXBits);
                                WriteUlongArray(ps, sec.FacePosXBits);
                                WriteUlongArray(ps, sec.FaceNegYBits);
                                WriteUlongArray(ps, sec.FacePosYBits);
                                WriteUlongArray(ps, sec.FaceNegZBits);
                                WriteUlongArray(ps, sec.FacePosZBits);
                            }

                            // Transparent masks
                            if (hasTransparentMasks && sec != null)
                            {
                                // Some faces may be null while TransparentBits is present (or vice-versa).
                                void MaybeWriteUlongArray(ulong[] arr)
                                {
                                    if (arr == null) { ps.Write((byte)0); return; }
                                    WriteUlongArray(ps, arr);
                                }
                                MaybeWriteUlongArray(sec.TransparentBits);
                                MaybeWriteUlongArray(sec.TransparentFaceNegXBits);
                                MaybeWriteUlongArray(sec.TransparentFacePosXBits);
                                MaybeWriteUlongArray(sec.TransparentFaceNegYBits);
                                MaybeWriteUlongArray(sec.TransparentFacePosYBits);
                                MaybeWriteUlongArray(sec.TransparentFaceNegZBits);
                                MaybeWriteUlongArray(sec.TransparentFacePosZBits);
                            }

                            // Empty bits
                            if (hasEmptyBits && sec != null)
                            {
                                WriteUlongArray(ps, sec.EmptyBits);
                            }

                            // Representation payload
                            switch (kind)
                            {
                                case Section.RepresentationKind.Empty:
                                    break;
                                case Section.RepresentationKind.Uniform when sec != null:
                                    ps.Write(sec.UniformBlockId);
                                    break;
                                case Section.RepresentationKind.Expanded when sec != null:
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
                                case Section.RepresentationKind.Packed when sec != null:
                                case Section.RepresentationKind.MultiPacked when sec != null:
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
                                    // PaletteLookup is a runtime convenience; not serialized.
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

                // Footer
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
                if (chunk.AllWaterChunk) flagsA |= 1u << 6;
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
                bw.Write((byte)0); // padding
                bw.Write(chunk.AllOneBlockBlockId);

                // Opaque boundary planes
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

                // Transparent boundary planes (ids)
                void WriteTransparentPlane(ushort[] plane)
                {
                    if (plane == null) { bw.Write(0); return; }
                    bw.Write(plane.Length);
                    for (int i = 0; i < plane.Length; i++) bw.Write(plane[i]);
                }
                WriteTransparentPlane(chunk.TransparentPlaneNegX);
                WriteTransparentPlane(chunk.TransparentPlanePosX);
                WriteTransparentPlane(chunk.TransparentPlaneNegY);
                WriteTransparentPlane(chunk.TransparentPlanePosY);
                WriteTransparentPlane(chunk.TransparentPlaneNegZ);
                WriteTransparentPlane(chunk.TransparentPlanePosZ);
            }
        }

        // --------------------- Quad Load ---------------------
        // Loads a quad file (if present) and materializes all chunk payloads into unbuiltChunks dictionary.
        // Returns the specific requested chunk if present, else null.
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

                // Quad header
                var magic = br.ReadBytes(4);
                if (magic.Length != 4 || magic[0]!=QUAD_MAGIC[0] || magic[1]!=QUAD_MAGIC[1] || magic[2]!=QUAD_MAGIC[2] || magic[3]!=QUAD_MAGIC[3]) return null;
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
                        Console.WriteLine($"[World] Quad ({bx},{bz}) corrupt chunk payload length at index {i} (len={payloadLen}). Aborting remaining.");
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
                Console.WriteLine($"[World] Failed to load quad ({bx},{bz}): {ex.Message}");
            }
            return requested;
        }

        // Parses one inline chunk payload with defensive checks.
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
                int expectedSX = GameManager.settings.chunkMaxX / Section.SECTION_SIZE;
                int expectedSY = GameManager.settings.chunkMaxY / Section.SECTION_SIZE;
                int expectedSZ = GameManager.settings.chunkMaxZ / Section.SECTION_SIZE;
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
                try { chunk = new Chunk(new Vector3(baseX, baseY, baseZ), loader.seed, loader.currentWorldSaveDirectory, autoGenerate:false); }
                catch (Exception ctorEx)
                {
                    Console.WriteLine($"[World] Chunk ctor failed ({cx},{cy},{cz}) phase={phase}: {ctorEx.Message}");
                    return null;
                }
                if (chunk.sections == null || chunk.sections.GetLength(0)!=sxCount)
                    chunk.sections = new Section[sxCount, syCount, szCount];

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
                            if (off==0) { chunk.sections[sx,sy,sz] = new Section(); continue; }
                            try
                            {
                                phase = $"SeekSection:{sectionTag}";
                                if (off + 3 > ms.Length) { chunk.sections[sx,sy,sz] = new Section(); continue; }
                                ms.Position = off;
                                byte kindByte = br.ReadByte();
                                var kind = (Section.RepresentationKind)kindByte;
                                if (kind == (Section.RepresentationKind)2) kind = Section.RepresentationKind.Expanded; // coerce unsupported
                                ushort payloadLen = br.ReadUInt16();
                                if (payloadLen==0 || ms.Position + payloadLen > ms.Length) { chunk.sections[sx,sy,sz] = new Section(); continue; }
                                phase = $"ReadSectionPayload:{sectionTag}";
                                byte[] spayload = br.ReadBytes(payloadLen);
                                if (spayload.Length != payloadLen) { chunk.sections[sx,sy,sz] = new Section(); continue; }
                                phase = $"ParseSectionPayload:{sectionTag}";
                                var sec = ParseSectionFromPayload(kind, spayload);
                                chunk.sections[sx,sy,sz] = sec ?? new Section();
                            }
                            catch (Exception exSection)
                            {
                                Console.WriteLine($"[World] Exception parsing {sectionTag} c=({cx},{cy},{cz}) phase={phase}: {exSection.Message}");
                                chunk.sections[sx,sy,sz] = new Section();
                            }
                        }
                    }
                }

                phase = "Footer";
                if (ms.Position + CHUNK_FOOTER_MAGIC.Length <= ms.Length)
                {
                    byte[] fmagic = br.ReadBytes(CHUNK_FOOTER_MAGIC.Length);
                    bool footerOk = fmagic.Length==CHUNK_FOOTER_MAGIC.Length && fmagic[0]==CHUNK_FOOTER_MAGIC[0] && fmagic[1]==CHUNK_FOOTER_MAGIC[1] && fmagic[2]==CHUNK_FOOTER_MAGIC[2];
                    if (footerOk && ms.Position + 4 + 4 + 4 + 1 + 1 + 2 <= ms.Length)
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
                            br.ReadByte(); // padding
                            chunk.AllOneBlockBlockId = br.ReadUInt16();
                            // Flags
                            chunk.AllAirChunk = (flagsA & (1u<<0))!=0;
                            chunk.AllStoneChunk = (flagsA & (1u<<1))!=0;
                            chunk.AllSoilChunk = (flagsA & (1u<<2))!=0;
                            chunk.AllOneBlockChunk = (flagsA & (1u<<3))!=0;
                            chunk.FullyBuried = (flagsA & (1u<<4))!=0;
                            chunk.BuriedByNeighbors = (flagsA & (1u<<5))!=0;
                            chunk.AllWaterChunk = (flagsA & (1u<<6))!=0;
                            chunk.OcclusionStatus = (Chunk.OcclusionClass)occStatus;
                            // Face flags
                            chunk.FaceSolidNegX = (faceFlags & (1<<0))!=0;
                            chunk.FaceSolidPosX = (faceFlags & (1<<1))!=0;
                            chunk.FaceSolidNegY = (faceFlags & (1<<2))!=0;
                            chunk.FaceSolidPosY = (faceFlags & (1<<3))!=0;
                            chunk.FaceSolidNegZ = (faceFlags & (1<<4))!=0;
                            chunk.FaceSolidPosZ = (faceFlags & (1<<5))!=0;

                            phase = "FooterPlanes";
                            ulong[] ReadPlane(){ if (ms.Position + 4 > ms.Length) return null; int len = br.ReadInt32(); if (len<=0 || ms.Position + len*8 > ms.Length) return null; var arr=new ulong[len]; for(int i=0;i<len;i++) arr[i]=br.ReadUInt64(); return arr; }
                            chunk.PlaneNegX = ReadPlane();
                            chunk.PlanePosX = ReadPlane();
                            chunk.PlaneNegY = ReadPlane();
                            chunk.PlanePosY = ReadPlane();
                            chunk.PlaneNegZ = ReadPlane();
                            chunk.PlanePosZ = ReadPlane();

                            // Transparent boundary planes
                            ushort[] ReadTransparentPlane(){ if (ms.Position + 4 > ms.Length) return null; int len = br.ReadInt32(); if (len<=0 || ms.Position + len*2 > ms.Length) return null; var arr=new ushort[len]; for(int i=0;i<len;i++) arr[i]=br.ReadUInt16(); return arr; }
                            chunk.TransparentPlaneNegX = ReadTransparentPlane();
                            chunk.TransparentPlanePosX = ReadTransparentPlane();
                            chunk.TransparentPlaneNegY = ReadTransparentPlane();
                            chunk.TransparentPlanePosY = ReadTransparentPlane();
                            chunk.TransparentPlaneNegZ = ReadTransparentPlane();
                            chunk.TransparentPlanePosZ = ReadTransparentPlane();
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
        // Reused section payload parser
        private Section ParseSectionFromPayload(Section.RepresentationKind kind, byte[] payload)
        {
            try
            {
                using var ms = new MemoryStream(payload,false);
                using var br = new BinaryReader(ms);
                if (payload.Length < 9) return new Section();
                var sec = new Section(); sec.Kind = kind; sec.VoxelCount = Section.SECTION_SIZE*Section.SECTION_SIZE*Section.SECTION_SIZE;
                // Counts
                sec.OpaqueVoxelCount = br.ReadUInt16();
                sec.TransparentCount = br.ReadUInt16();
                sec.EmptyCount = br.ReadUInt16();
                // Exposure
                sec.InternalExposure = br.ReadInt32();
                byte metaFlags = br.ReadByte();
                bool hasBounds = (metaFlags & 1)!=0;
                bool hasOpaqueMasks = (metaFlags & 2)!=0;
                bool completelyFull = (metaFlags & 4)!=0;
                bool metadataBuilt = (metaFlags & 8)!=0;
                bool isAllAir = (metaFlags & 16)!=0;
                bool hasTransparentMasks = (metaFlags & 32)!=0;
                bool hasEmptyBits = (metaFlags & 64)!=0;
                if (hasBounds && ms.Position + 6 <= ms.Length)
                {
                    sec.HasBounds=true; sec.MinLX=br.ReadByte(); sec.MinLY=br.ReadByte(); sec.MinLZ=br.ReadByte(); sec.MaxLX=br.ReadByte(); sec.MaxLY=br.ReadByte(); sec.MaxLZ=br.ReadByte();
                }

                ulong[] ReadUlongArr(){ if (ms.Position>=ms.Length) return null; int len = br.ReadByte(); if (len<=0 || ms.Position + len*8 > ms.Length) return null; var arr=new ulong[len]; for(int i=0;i<len;i++) arr[i]=br.ReadUInt64(); return arr; }

                if (hasOpaqueMasks)
                {
                    sec.OpaqueBits = ReadUlongArr();
                    sec.FaceNegXBits = ReadUlongArr();
                    sec.FacePosXBits = ReadUlongArr();
                    sec.FaceNegYBits = ReadUlongArr();
                    sec.FacePosYBits = ReadUlongArr();
                    sec.FaceNegZBits = ReadUlongArr();
                    sec.FacePosZBits = ReadUlongArr();
                }

                if (hasTransparentMasks)
                {
                    sec.TransparentBits = ReadUlongArr();
                    sec.TransparentFaceNegXBits = ReadUlongArr();
                    sec.TransparentFacePosXBits = ReadUlongArr();
                    sec.TransparentFaceNegYBits = ReadUlongArr();
                    sec.TransparentFacePosYBits = ReadUlongArr();
                    sec.TransparentFaceNegZBits = ReadUlongArr();
                    sec.TransparentFacePosZBits = ReadUlongArr();
                }

                if (hasEmptyBits)
                {
                    sec.EmptyBits = ReadUlongArr();
                }

                sec.CompletelyFull = completelyFull; sec.MetadataBuilt = metadataBuilt; sec.IsAllAir = isAllAir;
                sec.HasTransparent = sec.TransparentCount > 0; sec.HasAir = sec.EmptyCount > 0;

                switch(kind)
                {
                    case Section.RepresentationKind.Empty:
                        sec.IsAllAir = true;
                        break;
                    case Section.RepresentationKind.Uniform:
                        if (ms.Position + 2 <= ms.Length) sec.UniformBlockId = br.ReadUInt16();
                        break;
                    case Section.RepresentationKind.Expanded:
                        int expectedBytes = sec.VoxelCount * 2; if (ms.Position + expectedBytes <= ms.Length){ sec.ExpandedDense = new ushort[sec.VoxelCount]; var raw = br.ReadBytes(expectedBytes); MemoryMarshal.Cast<byte,ushort>(raw).CopyTo(sec.ExpandedDense); }
                        break;
                    case Section.RepresentationKind.Packed:
                    case Section.RepresentationKind.MultiPacked:
                        if (ms.Position < ms.Length)
                        {
                            sec.BitsPerIndex = br.ReadByte();
                            if (ms.Position + 2 <= ms.Length)
                            {
                                int paletteCount = br.ReadUInt16();
                                if (paletteCount>=0 && paletteCount<=4096 && ms.Position + paletteCount*2 <= ms.Length)
                                {
                                    sec.Palette = new List<ushort>(paletteCount);
                                    for(int i=0;i<paletteCount;i++) sec.Palette.Add(br.ReadUInt16());
                                    if (ms.Position +4 <= ms.Length)
                                    {
                                        int wordCount = br.ReadInt32();
                                        if (wordCount>=0 && ms.Position + wordCount*4 <= ms.Length)
                                        {
                                            if (wordCount>0){ sec.BitData = new uint[wordCount]; for(int i=0;i<wordCount;i++) sec.BitData[i]=br.ReadUInt32(); }
                                        }
                                    }
                                }
                            }
                        }
                        // Build PaletteLookup at runtime for convenience
                        if (sec.Palette!=null)
                        {
                            sec.PaletteLookup = new Dictionary<ushort,int>(sec.Palette.Count);
                            for (int i=0;i<sec.Palette.Count;i++) sec.PaletteLookup[sec.Palette[i]] = i;
                        }
                        break;
                }
                return sec;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Section payload parse error kind={kind}: {ex.Message}");
                return new Section();
            }
        }

        internal void SaveAllBatches()
        {
            bool any = false;
            foreach (var kv in loadedBatches)
            {
                var (bx,bz) = kv.Key;
                var batch = kv.Value;
                if (batch == null) continue;
                if (!batch.Dirty) continue;
                SaveBatch(bx,bz);
                batch.Dirty = false;
                any = true;
            }
            if (any)
            {
                lastFullWorldSave = DateTime.UtcNow;
                Console.WriteLine("[World] Saved dirty quads.");
            }
        }

        // when force==true save ALL quads regardless of Dirty flag.
        private void SaveAllBatches(bool force)
        {
            if (!force)
            {
                SaveAllBatches();
                return;
            }
            bool any = false;
            foreach (var kv in loadedBatches)
            {
                var (bx,bz) = kv.Key;
                var batch = kv.Value;
                if (batch == null) continue;
                SaveBatch(bx,bz);
                batch.Dirty = false;
                any = true;
            }
            if (any)
            {
                lastFullWorldSave = DateTime.UtcNow;
                Console.WriteLine("[World] Forced world save completed.");
            }
        }

        internal void TrySaveAndRemoveBatch(int bx, int bz)
        {
            if (!loadedBatches.TryGetValue((bx,bz), out var batch)) return;
            if (!batch.IsEmpty) return; // still has chunks
            if (batch.Dirty)
            {
                SaveBatch(bx,bz);
                batch.Dirty = false;
            }
            loadedBatches.TryRemove((bx,bz), out _);
        }
    }
}

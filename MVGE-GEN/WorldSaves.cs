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
        private const ushort CHUNK_SAVE_VERSION = 1;
        private static readonly byte[] CHUNK_MAGIC = new byte[] { (byte)'M', (byte)'V', (byte)'C', (byte)'H' }; // "MVCH" (MVGE Chunk)
        private static readonly byte[] CHUNK_FOOTER_MAGIC = new byte[] { (byte)'C', (byte)'M', (byte)'D'}; // Chunk Meta Data

        // File Layout (little endian)
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
        //              Packed: bitsPerIndex (byte), paletteCount (ushort), palette ids (ushort*count), bitDataWordCount (int), bitData words (uint*wordCount)
        //                      (version >=2) palette lookup mapping present flag (byte 0/1) then if 1: ushort mappingCount then mappingCount*(blockId ushort, index ushort)
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

        private void SaveChunkToFile(Chunk chunk)
        {
            try
            {
                var settings = GameManager.settings;
                string worldRoot = Path.Combine(settings.savesWorldDirectory, loader.ID.ToString());
                string regionRoot = Path.Combine(worldRoot, loader.RegionID.ToString());
                string chunksDir = Path.Combine(regionRoot, loader.currentWorldSavedChunksSubDirectory);
                Directory.CreateDirectory(chunksDir);

                int dimX = settings.chunkMaxX;
                int dimY = settings.chunkMaxY;
                int dimZ = settings.chunkMaxZ;
                int cix = (int)Math.Floor(chunk.position.X / dimX);
                int ciy = (int)Math.Floor(chunk.position.Y / dimY);
                int ciz = (int)Math.Floor(chunk.position.Z / dimZ);

                string fileName = $"chunk{cix}x{ciy}y{ciz}z.bin";
                string path = Path.Combine(chunksDir, fileName);

                int sectionCount = chunk.sectionsX * chunk.sectionsY * chunk.sectionsZ;
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None);
                using var bw = new BinaryWriter(fs);

                bw.Write(CHUNK_MAGIC);
                bw.Write(CHUNK_SAVE_VERSION);
                bw.Write((ushort)0);
                bw.Write(cix);
                bw.Write(ciy);
                bw.Write(ciz);
                bw.Write(chunk.sectionsX);
                bw.Write(chunk.sectionsY);
                bw.Write(chunk.sectionsZ);
                bw.Write(sectionCount);

                long offsetsStart = fs.Position;
                for (int i = 0; i < sectionCount; i++) bw.Write(0u);

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
                            long sectionOffset = fs.Position;
                            if (sectionOffset > uint.MaxValue)
                            {
                                Console.WriteLine("[World] Chunk file exceeded 4GB offset range; aborting save.");
                                return;
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
                                            ps.Write((byte)1); // present flag (persist from v1+ since we always wrote it)
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
                }

                long endPos = fs.Position;
                fs.Position = offsetsStart;
                for (int i = 0; i < sectionCount; i++) bw.Write(offsets[i]);
                fs.Position = endPos;

                // ---- Footer (chunk-level metadata) ----
                bw.Write(CHUNK_FOOTER_MAGIC);
                // temperature / humidity (stored as floats for historical compatibility)
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
                // boundary planes (variable length). If null => length 0
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
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Failed to save chunk: {ex.Message}");
            }
        }

        private string GetChunkFilePath(int cx, int cy, int cz)
        {
            var settings = GameManager.settings;
            string worldRoot = Path.Combine(settings.savesWorldDirectory, loader.ID.ToString());
            string regionRoot = Path.Combine(worldRoot, loader.RegionID.ToString());
            string chunksDir = Path.Combine(regionRoot, loader.currentWorldSavedChunksSubDirectory);
            Directory.CreateDirectory(chunksDir);
            return Path.Combine(chunksDir, $"chunk{cx}x{cy}y{cz}z.bin");
        }

        private Chunk LoadChunkFromFile(int cx, int cy, int cz)
        {
            string path = GetChunkFilePath(cx, cy, cz);
            if (!File.Exists(path)) return null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                using var br = new BinaryReader(fs);
                var magic = br.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != CHUNK_MAGIC[0] || magic[1] != CHUNK_MAGIC[1] || magic[2] != CHUNK_MAGIC[2] || magic[3] != CHUNK_MAGIC[3])
                    return null; // invalid
                ushort ver = br.ReadUInt16();
                br.ReadUInt16(); // padding
                int fileCx = br.ReadInt32();
                int fileCy = br.ReadInt32();
                int fileCz = br.ReadInt32();
                int sxCount = br.ReadInt32();
                int syCount = br.ReadInt32();
                int szCount = br.ReadInt32();
                int sectionCount = br.ReadInt32();
                if (sectionCount != sxCount * syCount * szCount) return null;

                // Read offsets
                uint[] offsets = new uint[sectionCount];
                for (int i = 0; i < sectionCount; i++) offsets[i] = br.ReadUInt32();

                // Construct chunk at world position (convert chunk indices back to world base coords)
                int baseX = fileCx * GameManager.settings.chunkMaxX;
                int baseY = fileCy * GameManager.settings.chunkMaxY;
                int baseZ = fileCz * GameManager.settings.chunkMaxZ;
                var chunk = new Chunk(new Vector3(baseX, baseY, baseZ), loader.seed, loader.currentWorldSaveDirectory, null);
                // Ensure section grid
                if (chunk.sections == null || chunk.sections.GetLength(0) != sxCount || chunk.sections.GetLength(1) != syCount || chunk.sections.GetLength(2) != szCount)
                {
                    chunk.sections = new ChunkSection[sxCount, syCount, szCount];
                }

                int linear = 0;
                for (int sx = 0; sx < sxCount; sx++)
                {
                    for (int sy = 0; sy < syCount; sy++)
                    {
                        for (int sz = 0; sz < szCount; sz++, linear++)
                        {
                            uint off = offsets[linear];
                            if (off == 0 || off >= fs.Length) { chunk.sections[sx, sy, sz] = new ChunkSection(); continue; }
                            fs.Position = off;
                            byte kindByte = br.ReadByte();
                            ChunkSection.RepresentationKind kind = (ChunkSection.RepresentationKind)kindByte;
                            ushort payloadLen = br.ReadUInt16();
                            if (payloadLen > fs.Length - fs.Position) payloadLen = (ushort)(fs.Length - fs.Position);
                            byte[] payload = br.ReadBytes(payloadLen);
                            var sec = new ChunkSection();
                            sec.Kind = kind;
                            sec.VoxelCount = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE;
                            using var ms = new MemoryStream(payload);
                            using var prs = new BinaryReader(ms);
                            if (payloadLen >= 7) // minimum metadata length
                            {
                                sec.NonAirCount = prs.ReadUInt16();
                                sec.InternalExposure = prs.ReadInt32();
                                byte metaFlags = prs.ReadByte();
                                bool hasBounds = (metaFlags & 1) != 0;
                                bool hasOcc = (metaFlags & 2) != 0;
                                if (hasBounds && ms.Position + 6 <= ms.Length)
                                {
                                    sec.HasBounds = true;
                                    sec.MinLX = prs.ReadByte();
                                    sec.MinLY = prs.ReadByte();
                                    sec.MinLZ = prs.ReadByte();
                                    sec.MaxLX = prs.ReadByte();
                                    sec.MaxLY = prs.ReadByte();
                                    sec.MaxLZ = prs.ReadByte();
                                }
                                if (hasOcc)
                                {
                                    ulong[] ReadOcc()
                                    {
                                        if (ms.Position >= ms.Length) return null;
                                        int len = prs.ReadByte();
                                        if (len <= 0 || ms.Position + len * 8 > ms.Length) return null;
                                        var arr = new ulong[len];
                                        for (int i = 0; i < len; i++) arr[i] = prs.ReadUInt64();
                                        return arr;
                                    }
                                    sec.OccupancyBits = ReadOcc();
                                    sec.FaceNegXBits = ReadOcc();
                                    sec.FacePosXBits = ReadOcc();
                                    sec.FaceNegYBits = ReadOcc();
                                    sec.FacePosYBits = ReadOcc();
                                    sec.FaceNegZBits = ReadOcc();
                                    sec.FacePosZBits = ReadOcc();
                                }
                                sec.CompletelyFull = (metaFlags & (1 << 2)) != 0;
                                sec.MetadataBuilt = (metaFlags & (1 << 3)) != 0;
                                sec.IsAllAir = (metaFlags & (1 << 4)) != 0;
                                sec.StructuralDirty = (metaFlags & (1 << 5)) != 0;
                                sec.IdMapDirty = (metaFlags & (1 << 6)) != 0;
                                sec.BoundingBoxDirty = (metaFlags & (1 << 7)) != 0;
                            }
                            // Representation specific
                            switch (kind)
                            {
                                case ChunkSection.RepresentationKind.Empty:
                                    sec.IsAllAir = true;
                                    break;
                                case ChunkSection.RepresentationKind.Uniform:
                                    if (ms.Position + 2 <= ms.Length) sec.UniformBlockId = prs.ReadUInt16();
                                    if (!sec.MetadataBuilt) sec.IsAllAir = (sec.NonAirCount == 0);
                                    break;
                                case ChunkSection.RepresentationKind.Sparse:
                                    if (ms.Position + 2 <= ms.Length)
                                    {
                                        int scount = prs.ReadUInt16();
                                        sec.SparseIndices = new int[scount];
                                        sec.SparseBlocks = new ushort[scount];
                                        for (int i = 0; i < scount && ms.Position + 4 <= ms.Length; i++)
                                        {
                                            sec.SparseIndices[i] = prs.ReadUInt16();
                                            sec.SparseBlocks[i] = prs.ReadUInt16();
                                        }
                                    }
                                    break;
                                case ChunkSection.RepresentationKind.DenseExpanded:
                                    int expectedBytes = sec.VoxelCount * 2;
                                    sec.ExpandedDense = new ushort[sec.VoxelCount];
                                    int toRead = Math.Min(expectedBytes, (int)(ms.Length - ms.Position));
                                    byte[] dense = prs.ReadBytes(toRead);
                                    MemoryMarshal.Cast<byte, ushort>(dense).CopyTo(sec.ExpandedDense.AsSpan());
                                    break;
                                case ChunkSection.RepresentationKind.Packed:
                                    if (ms.Position < ms.Length)
                                    {
                                        sec.BitsPerIndex = prs.ReadByte();
                                        if (ms.Position + 2 <= ms.Length)
                                        {
                                            int paletteCount = prs.ReadUInt16();
                                            sec.Palette = new List<ushort>(paletteCount);
                                            for (int i = 0; i < paletteCount && ms.Position + 2 <= ms.Length; i++)
                                                sec.Palette.Add(prs.ReadUInt16());
                                            if (ms.Position + 4 <= ms.Length)
                                            {
                                                int wordCount = prs.ReadInt32();
                                                if (wordCount > 0 && ms.Position + wordCount * 4 <= ms.Length)
                                                {
                                                    sec.BitData = new uint[wordCount];
                                                    for (int i = 0; i < wordCount; i++) sec.BitData[i] = prs.ReadUInt32();
                                                }
                                            }
                                            if (ms.Position < ms.Length)
                                            {
                                                byte mapPresent = prs.ReadByte();
                                                if (mapPresent != 0 && ms.Position + 2 <= ms.Length)
                                                {
                                                    int mapCount = prs.ReadUInt16();
                                                    sec.PaletteLookup = new Dictionary<ushort, int>(mapCount);
                                                    for (int mi = 0; mi < mapCount && ms.Position + 4 <= ms.Length; mi++)
                                                    {
                                                        ushort blockId = prs.ReadUInt16();
                                                        ushort index = prs.ReadUInt16();
                                                        sec.PaletteLookup[blockId] = index;
                                                    }
                                                }
                                                else if (sec.Palette != null)
                                                {
                                                    sec.PaletteLookup = new Dictionary<ushort, int>(sec.Palette.Count);
                                                    for (int i = 0; i < sec.Palette.Count; i++) sec.PaletteLookup[sec.Palette[i]] = i;
                                                }
                                            }
                                            else if (sec.Palette != null)
                                            {
                                                sec.PaletteLookup = new Dictionary<ushort, int>(sec.Palette.Count);
                                                for (int i = 0; i < sec.Palette.Count; i++) sec.PaletteLookup[sec.Palette[i]] = i;
                                            }
                                        }
                                    }
                                    break;
                            }
                            if (sec.Kind == ChunkSection.RepresentationKind.Packed && sec.Palette != null && sec.PaletteLookup == null)
                            {
                                sec.PaletteLookup = new Dictionary<ushort, int>(sec.Palette.Count);
                                for (int i = 0; i < sec.Palette.Count; i++) sec.PaletteLookup[sec.Palette[i]] = i;
                            }
                            chunk.sections[sx, sy, sz] = sec;
                        }
                    }
                }

                // Footer attempt. We always try to read if bytes remain.
                if (fs.Position + 4 <= fs.Length)
                {
                    long footerPos = fs.Position;
                    byte[] fmagic = br.ReadBytes(4);
                    bool footerOk = fmagic.Length == 4 && fmagic[0] == CHUNK_FOOTER_MAGIC[0] && fmagic[1] == CHUNK_FOOTER_MAGIC[1] && fmagic[2] == CHUNK_FOOTER_MAGIC[2] && fmagic[3] == CHUNK_FOOTER_MAGIC[3];
                    if (footerOk)
                    {
                        float tempF = br.ReadSingle();
                        float humF = br.ReadSingle();
                        chunk.chunkData.temperature = (byte)Math.Clamp((int)Math.Round(tempF), 0, 255);
                        chunk.chunkData.humidity = (byte)Math.Clamp((int)Math.Round(humF), 0, 255);
                        uint flagsA = br.ReadUInt32();
                        byte faceFlags = br.ReadByte();
                        byte occStatus = br.ReadByte();
                        br.ReadUInt16(); // padding
                        chunk.AllOneBlockBlockId = br.ReadUInt16();
                        byte statsPresent = br.ReadByte();
                        br.ReadByte(); // padding
                        chunk.AllAirChunk = (flagsA & (1u << 0)) != 0;
                        chunk.AllStoneChunk = (flagsA & (1u << 1)) != 0;
                        chunk.AllSoilChunk = (flagsA & (1u << 2)) != 0;
                        chunk.AllOneBlockChunk = (flagsA & (1u << 3)) != 0;
                        chunk.FullyBuried = (flagsA & (1u << 4)) != 0;
                        chunk.BuriedByNeighbors = (flagsA & (1u << 5)) != 0;
                        chunk.OcclusionStatus = (Chunk.OcclusionClass)occStatus;
                        chunk.FaceSolidNegX = (faceFlags & (1 << 0)) != 0;
                        chunk.FaceSolidPosX = (faceFlags & (1 << 1)) != 0;
                        chunk.FaceSolidNegY = (faceFlags & (1 << 2)) != 0;
                        chunk.FaceSolidPosY = (faceFlags & (1 << 3)) != 0;
                        chunk.FaceSolidNegZ = (faceFlags & (1 << 4)) != 0;
                        chunk.FaceSolidPosZ = (faceFlags & (1 << 5)) != 0;
                        if (statsPresent != 0)
                        {
                            var stats = chunk.MeshPrepassStats;
                            stats.SolidCount = br.ReadInt32();
                            stats.ExposureEstimate = br.ReadInt32();
                            stats.MinX = br.ReadInt32(); stats.MinY = br.ReadInt32(); stats.MinZ = br.ReadInt32();
                            stats.MaxX = br.ReadInt32(); stats.MaxY = br.ReadInt32(); stats.MaxZ = br.ReadInt32();
                            stats.XNonEmpty = br.ReadInt32(); stats.YNonEmpty = br.ReadInt32(); stats.ZNonEmpty = br.ReadInt32();
                            byte hasStats = br.ReadByte();
                            stats.HasStats = hasStats != 0;
                            chunk.MeshPrepassStats = stats;
                        }
                        // planes
                        ulong[] ReadPlane()
                        {
                            if (br.BaseStream.Position + 4 > br.BaseStream.Length) return null;
                            int len = br.ReadInt32();
                            if (len <= 0 || br.BaseStream.Position + (long)len * 8 > br.BaseStream.Length) return null;
                            var arr = new ulong[len];
                            for (int i = 0; i < len; i++) arr[i] = br.ReadUInt64();
                            return arr;
                        }
                        chunk.PlaneNegX = ReadPlane();
                        chunk.PlanePosX = ReadPlane();
                        chunk.PlaneNegY = ReadPlane();
                        chunk.PlanePosY = ReadPlane();
                        chunk.PlaneNegZ = ReadPlane();
                        chunk.PlanePosZ = ReadPlane();
                    }
                    else
                    {
                        fs.Position = footerPos; // revert if not footer
                    }
                }

                return chunk;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Failed to load chunk ({cx},{cy},{cz}): {ex.Message}");
                return null;
            }
        }
    }
}

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

        // File Layout (little endian)
        // Header:
        //  0  : 4  bytes  Magic "MVCH"
        //  4  : 2  bytes  Version (ushort) (==2 for this format)
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
        //              MetaFlags (byte) bit0 HasBounds, bit1 HasOccupancy
        //              (if HasBounds) 6 bytes: MinLX,MinLY,MinLZ,MaxLX,MaxLY,MaxLZ
        //              (if HasOccupancy)  (OccupancyBits 4*8) + 6 faces * (4*8) = 25 * 8 = 200 bytes
        //          Representation-specific data appended after metadata:
        //              Empty: (no additional)
        //              Uniform: blockId (ushort)
        //              Sparse: count (ushort) then count * (index ushort, blockId ushort)
        //              DenseExpanded: 4096 * 2 bytes raw voxel ids
        //              Packed: bitsPerIndex (byte), paletteCount (ushort), palette ids (ushort*count), bitDataWordCount (int), bitData words (uint*wordCount)
        // Rationale: dramatically smaller + less CPU versus naive 8KB per section expansion.

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
                                if (hasBounds) metaFlags |= 1;
                                if (hasOcc) metaFlags |= 1 << 1;
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
                                    // Write lengths to make decoding robust
                                    void WriteUlongArray(ulong[] arr)
                                    {
                                        ps.Write((byte)arr.Length); // length (fits in byte for expected small arrays, fallback if larger)
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
                            }
                            // Representation specific
                            switch (kind)
                            {
                                case ChunkSection.RepresentationKind.Empty:
                                    sec.IsAllAir = true;
                                    break;
                                case ChunkSection.RepresentationKind.Uniform:
                                    if (ms.Position + 2 <= ms.Length) sec.UniformBlockId = prs.ReadUInt16();
                                    sec.IsAllAir = (sec.NonAirCount == 0);
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
                                        }
                                    }
                                    break;
                            }
                            chunk.sections[sx, sy, sz] = sec;
                        }
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

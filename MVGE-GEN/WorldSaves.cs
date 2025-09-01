using System;
using System.IO;
using MVGE_INF.Managers;
using MVGE_GEN.Terrain;
using MVGE_INF.Generation.Models;
using System.Runtime.InteropServices;

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
    }
}

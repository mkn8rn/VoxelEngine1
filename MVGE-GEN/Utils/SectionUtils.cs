using MVGE_INF.Generation.Models;
using MVGE_INF.Models.Terrain;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MVGE_GEN.Utils
{
    internal static partial class SectionUtils
    {
        private static uint[] RentBitData(int uintCount) => ArrayPool<uint>.Shared.Rent(uintCount);
        private static void ReturnBitData(uint[] data) { if (data != null) ArrayPool<uint>.Shared.Return(data, clearArray: false); }

        public const int SPARSE_THRESHOLD = 512; // currently unused in classification (optimization gate only)
        private const int SECTION_SIZE = ChunkSection.SECTION_SIZE;
        private const int VOXELS_PER_SECTION = SECTION_SIZE * SECTION_SIZE * SECTION_SIZE; // 4096
        private const int PACKED_MULTI_ID_MAX = 64; // max distinct ids (including AIR) to qualify for MultiPacked finalize path


        // ---------------------------------------------------------------------
        // Unified entry point: finalize a section either from scratch run data
        // (when BuildScratch present) or refresh metadata from existing
        // representation when no scratch is available.
        // ---------------------------------------------------------------------
        public static void FinalizeOrRefreshSection(ChunkSection sec)
        {
            if (sec == null) return;
            var scratch = sec.BuildScratch as SectionBuildScratch;
            if (scratch != null)
            {
                // Reuse existing run-length based finalize path.
                FinalizeSection(sec);
                return;
            }
            // No scratch: refresh metadata from current representation.
            // If already built and caller does not require a forced rebuild, we could early return.
            // For safety always rebuild to ensure consistency after external mutations.
            switch (sec.Kind)
            {
                case ChunkSection.RepresentationKind.Empty:
                    sec.IsAllAir = true;
                    sec.NonAirCount = 0;
                    sec.MetadataBuilt = true;
                    break;
                case ChunkSection.RepresentationKind.Uniform:
                    BuildMetadataUniform(sec);
                    break;
                case ChunkSection.RepresentationKind.Sparse:
                    BuildMetadataSparse(sec);
                    break;
                case ChunkSection.RepresentationKind.DenseExpanded:
                    BuildMetadataDense(sec);
                    break;
                case ChunkSection.RepresentationKind.Packed:
                case ChunkSection.RepresentationKind.MultiPacked: // treat MultiPacked same for metadata rebuild
                default:
                    BuildMetadataPacked(sec);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort GetBlock(ChunkSection sec, int x, int y, int z)
        {
            if (sec == null || sec.IsAllAir || sec.Kind == ChunkSection.RepresentationKind.Empty) return ChunkSection.AIR;
            int linear = LinearIndex(x, y, z);
            switch (sec.Kind)
            {
                case ChunkSection.RepresentationKind.Uniform:
                    return sec.UniformBlockId;
                case ChunkSection.RepresentationKind.DenseExpanded:
                    return sec.ExpandedDense[linear];
                case ChunkSection.RepresentationKind.Sparse:
                    // simple linear search (sparse rarely queried per-voxel outside flatten). Could optimize with dictionary if needed.
                    var idxArr = sec.SparseIndices;
                    if (idxArr != null)
                    {
                        for (int i = 0; i < idxArr.Length; i++) if (idxArr[i] == linear) return sec.SparseBlocks[i];
                    }
                    return ChunkSection.AIR;
                case ChunkSection.RepresentationKind.Packed:
                case ChunkSection.RepresentationKind.MultiPacked:
                default:
                    int paletteIndex = ReadBits(sec, linear); return sec.Palette[paletteIndex];
            }
        }

        public static void SetBlock(ChunkSection sec, int x, int y, int z, ushort blockId)
        {
            if (sec == null) return;
            // Any mutation invalidates metadata
            sec.MetadataBuilt = false;

            if (sec.Kind == ChunkSection.RepresentationKind.Uniform && blockId != sec.UniformBlockId)
            {
                EnsurePacked(sec); // convert uniform to packed to allow mutation
            }
            if (sec.Kind != ChunkSection.RepresentationKind.Packed && sec.Kind != ChunkSection.RepresentationKind.MultiPacked && sec.Kind != ChunkSection.RepresentationKind.Empty)
            {
                EnsurePacked(sec);
            }

            int linear = LinearIndex(x, y, z);

            if (blockId == ChunkSection.AIR)
            {
                if (sec.IsAllAir) return;
                int oldIdx = ReadBits(sec, linear);
                if (oldIdx == 0) return; // already air
                WriteBits(sec, linear, 0);
                sec.NonAirCount--;
                sec.CompletelyFull = false; // no longer full
                if (sec.NonAirCount == 0)
                {
                    Collapse(sec);
                    sec.Kind = ChunkSection.RepresentationKind.Empty;
                }
                return;
            }

            if (sec.IsAllAir)
            {
                Initialize(sec);
                sec.Kind = ChunkSection.RepresentationKind.Packed; // start as packed (may later promote to MultiPacked)
            }

            int existingPaletteIndex = ReadBits(sec, linear);
            ushort existingBlock = sec.Palette[existingPaletteIndex];
            if (existingBlock == blockId) return;

            int newPaletteIndex = GetOrAddPaletteIndex(sec, blockId);
            if (newPaletteIndex >= (1 << sec.BitsPerIndex))
            {
                GrowBits(sec);
                newPaletteIndex = GetOrAddPaletteIndex(sec, blockId);
            }

            if (existingBlock == ChunkSection.AIR) sec.NonAirCount++;
            WriteBits(sec, linear, newPaletteIndex);
            if (sec.NonAirCount == sec.VoxelCount && sec.VoxelCount != 0)
            {
                sec.CompletelyFull = true;
            }
        }

        public static void ClassifyRepresentation(ChunkSection sec)
        {
            if (sec == null) return;
            if (sec.NonAirCount == 0 || sec.IsAllAir)
            {
                sec.Kind = ChunkSection.RepresentationKind.Empty;
                sec.MetadataBuilt = true; // trivial
                return;
            }
            int total = sec.VoxelCount;
            if (total == 0) total = VOXELS_PER_SECTION;

            // Uniform (full volume single id)
            if (sec.CompletelyFull && sec.Palette != null && sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR)
            {
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = sec.Palette[1];
                BuildMetadataUniform(sec);
                return;
            }
            if (sec.Palette != null && sec.Palette.Count == 2 && sec.Palette[0] == ChunkSection.AIR && sec.NonAirCount == total)
            {
                sec.CompletelyFull = true;
                sec.Kind = ChunkSection.RepresentationKind.Uniform;
                sec.UniformBlockId = sec.Palette[1];
                BuildMetadataUniform(sec);
                return;
            }

            // Sparse threshold (kept)
            if (sec.NonAirCount <= 128)
            {
                ExpandToSparse(sec);
                BuildMetadataSparse(sec);
                return;
            }

            // Multi-id (more than one distinct non-air id)
            // Palette layout: [AIR, id1, id2, ...]; so >2 means at least 2 distinct non-air ids.
            int distinctNonAir = (sec.Palette?.Count ?? 0) - 1; // exclude air
            if (distinctNonAir > 1)
            {
                // If still within multi-packed limit choose MultiPacked else DenseExpanded
                if (distinctNonAir + 1 /*include AIR*/ <= PACKED_MULTI_ID_MAX)
                {
                    // Keep as general packed form (MultiPacked) – metadata builder identical
                    sec.Kind = ChunkSection.RepresentationKind.MultiPacked;
                    BuildMetadataPacked(sec);
                }
                else
                {
                    ExpandToDense(sec);
                    BuildMetadataDense(sec);
                }
                return;
            }

            // Single non-air id but partial occupancy
            sec.Kind = ChunkSection.RepresentationKind.Packed;
            BuildMetadataPacked(sec);
        }

        private static void ExpandToSparse(ChunkSection sec)
        {
            if (sec.Kind == ChunkSection.RepresentationKind.Sparse) return;
            int count = sec.NonAirCount;
            var idx = new int[count];
            var blocks = new ushort[count];
            int write = 0;
            for (int z = 0; z < SECTION_SIZE; z++)
            {
                for (int x = 0; x < SECTION_SIZE; x++)
                {
                    for (int y = 0; y < SECTION_SIZE; y++)
                    {
                        int li = LinearIndex(x, y, z);
                        int pi = ReadBits(sec, li);
                        if (pi == 0) continue;
                        idx[write] = li;
                        blocks[write] = sec.Palette[pi];
                        write++;
                    }
                }
            }
            sec.SparseIndices = idx;
            sec.SparseBlocks = blocks;
            sec.Kind = ChunkSection.RepresentationKind.Sparse;
        }

        private static void ExpandToDense(ChunkSection sec)
        {
            if (sec.Kind == ChunkSection.RepresentationKind.DenseExpanded) return;
            var arr = new ushort[VOXELS_PER_SECTION];
            for (int z = 0; z < SECTION_SIZE; z++)
                for (int x = 0; x < SECTION_SIZE; x++)
                    for (int y = 0; y < SECTION_SIZE; y++)
                    {
                        int li = LinearIndex(x, y, z);
                        int pi = ReadBits(sec, li);
                        if (pi != 0) arr[li] = sec.Palette[pi];
                    }
            sec.ExpandedDense = arr;
            sec.Kind = ChunkSection.RepresentationKind.DenseExpanded;
        }

        private static void BuildMetadataUniform(ChunkSection sec)
        {
            sec.OccupancyBits = null; // not needed
            sec.FaceNegXBits = sec.FacePosXBits = sec.FaceNegYBits = sec.FacePosYBits = sec.FaceNegZBits = sec.FacePosZBits = null; // implicit full
            int N = VOXELS_PER_SECTION;
            // Internal adjacency for full 16^3 block
            int len = SECTION_SIZE;
            long lenL = len;
            long internalAdj = (lenL - 1) * lenL * lenL + lenL * (lenL - 1) * lenL + lenL * lenL * (lenL - 1);
            sec.InternalExposure = (int)(6L * N - 2L * internalAdj);
            sec.HasBounds = true; sec.MinLX = sec.MinLY = sec.MinLZ = 0; sec.MaxLX = sec.MaxLY = sec.MaxLZ = (byte)(SECTION_SIZE - 1);
            sec.MetadataBuilt = true;
        }

        private static void BuildMetadataSparse(ChunkSection sec)
        {
            // Build bounds + internal exposure from scratch using sparse indices.
            var idx = sec.SparseIndices; var blocks = sec.SparseBlocks; int count = idx.Length;
            if (count == 0) { sec.InternalExposure = 0; sec.MetadataBuilt = true; return; }
            byte minx = 255, miny = 255, minz = 255, maxx = 0, maxy = 0, maxz = 0;
            // Build a temporary occupancy bitset to compute adjacency cheaply
            ulong[] bits = new ulong[64];
            for (int i = 0; i < count; i++)
            {
                int li = idx[i];
                bits[li >> 6] |= 1UL << (li & 63);
                DecodeLinear(li, out int x, out int y, out int z);
                if (x < minx) minx = (byte)x; if (x > maxx) maxx = (byte)x;
                if (y < miny) miny = (byte)y; if (y > maxy) maxy = (byte)y;
                if (z < minz) minz = (byte)z; if (z > maxz) maxz = (byte)z;
            }
            ComputeInternalExposure(bits, out int exposure);
            sec.InternalExposure = exposure;
            sec.OccupancyBits = bits; // we can retain for cross-section faces
            BuildFaceMasks(sec, bits);
            sec.HasBounds = true; sec.MinLX = minx; sec.MinLY = miny; sec.MinLZ = minz; sec.MaxLX = maxx; sec.MaxLY = maxy; sec.MaxLZ = maxz; sec.MetadataBuilt = true;
        }

        private static void BuildMetadataDense(ChunkSection sec)
        {
            ulong[] bits = new ulong[64];
            byte minx = 255, miny = 255, minz = 255, maxx = 0, maxy = 0, maxz = 0;
            var arr = sec.ExpandedDense;
            for (int li = 0; li < VOXELS_PER_SECTION; li++)
            {
                if (arr[li] == ChunkSection.AIR) continue;
                bits[li >> 6] |= 1UL << (li & 63);
                DecodeLinear(li, out int x, out int y, out int z);
                if (x < minx) minx = (byte)x; if (x > maxx) maxx = (byte)x;
                if (y < miny) miny = (byte)y; if (y > maxy) maxy = (byte)y;
                if (z < minz) minz = (byte)z; if (z > maxz) maxz = (byte)z;
            }
            ComputeInternalExposure(bits, out int exposure);
            sec.InternalExposure = exposure;
            sec.OccupancyBits = bits;
            BuildFaceMasks(sec, bits);
            sec.HasBounds = true; sec.MinLX = minx; sec.MinLY = miny; sec.MinLZ = minz; sec.MaxLX = maxx; sec.MaxLY = maxy; sec.MaxLZ = maxz; sec.MetadataBuilt = true;
        }

        private static void BuildMetadataPacked(ChunkSection sec)
        {
            ulong[] bits = new ulong[64];
            byte minx = 255, miny = 255, minz = 255, maxx = 0, maxy = 0, maxz = 0;
            for (int li = 0; li < VOXELS_PER_SECTION; li++)
            {
                int pi = ReadBits(sec, li);
                if (pi == 0) continue;
                bits[li >> 6] |= 1UL << (li & 63);
                DecodeLinear(li, out int x, out int y, out int z);
                if (x < minx) minx = (byte)x; if (x > maxx) maxx = (byte)x;
                if (y < miny) miny = (byte)y; if (y > maxy) maxy = (byte)y;
                if (z < minz) minz = (byte)z; if (z > maxz) maxz = (byte)z;
            }
            ComputeInternalExposure(bits, out int exposure);
            sec.InternalExposure = exposure;
            sec.OccupancyBits = bits;
            BuildFaceMasks(sec, bits);
            sec.HasBounds = minx != 255; // if any solid
            if (sec.HasBounds)
            {
                sec.MinLX = minx; sec.MinLY = miny; sec.MinLZ = minz; sec.MaxLX = maxx; sec.MaxLY = maxy; sec.MaxLZ = maxz;
            }
            sec.MetadataBuilt = true;
        }

        private static void ComputeInternalExposure(ulong[] bits, out int exposure)
        {
            long adjX = 0, adjZ = 0, adjY = 0;
            for (int li = 0; li < VOXELS_PER_SECTION; li++)
            {
                if ((bits[li >> 6] & (1UL << (li & 63))) == 0) continue;
                // decode cheaply without full DecodeLinear where possible
                int y = li & 15;               // low 4 bits
                int columnIndex = li >> 4;     // 0..255
                int x = columnIndex & 15;
                int z = columnIndex >> 4;
                // neighbors
                // vertical y+1 contiguous +1
                if (y < 15)
                {
                    int liY = li + 1;
                    if ((bits[liY >> 6] & (1UL << (liY & 63))) != 0) adjY++;
                }
                // x+1 => next column (columnIndex+1) if x<15
                if (x < 15)
                {
                    int liX = li + 16; // add 16 (one column * 16 y entries)
                    if ((bits[liX >> 6] & (1UL << (liX & 63))) != 0) adjX++;
                }
                // z+1 => columnIndex +16 => +256 linear indices
                if (z < 15)
                {
                    int liZ = li + 256; // 16 columns per z *16 y
                    if ((bits[liZ >> 6] & (1UL << (liZ & 63))) != 0) adjZ++;
                }
            }
            int N = 0;
            for (int i = 0; i < 64; i++) N += BitOperations.PopCount(bits[i]);
            long internalAdj = adjX + adjZ + adjY;
            exposure = (int)(6L * N - 2L * internalAdj);
        }

        // Builds 256‑bit (4 * ulong) masks for each boundary face of a 16^3 section
        // using the section's occupancy bitset (column‑major: li = ((z*16 + x)*16)+y).
        // Layouts must match renderer expectations:
        //  Neg/Pos X: YZ plane  index = z*16 + y
        //  Neg/Pos Y: XZ plane  index = x*16 + z
        //  Neg/Pos Z: XY plane  index = x*16 + y
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildFaceMasks(ChunkSection sec, ulong[] occ)
        {
            const int S = 16;
            if (occ == null) return;

            static void EnsureAndClear(ref ulong[] arr)
            {
                if (arr == null) arr = new ulong[4];
                else Array.Clear(arr);
            }

            EnsureAndClear(ref sec.FaceNegXBits);
            EnsureAndClear(ref sec.FacePosXBits);
            EnsureAndClear(ref sec.FaceNegYBits);
            EnsureAndClear(ref sec.FacePosYBits);
            EnsureAndClear(ref sec.FaceNegZBits);
            EnsureAndClear(ref sec.FacePosZBits);

            // X faces (x = 0 and x = 15)  -> YZ plane (z,y)
            for (int z = 0; z < S; z++)
            {
                int zBase256 = z * 256; // z * 16 * 16
                for (int y = 0; y < S; y++)
                {
                    int liNeg = zBase256 + y;            // x=0
                    int liPos = zBase256 + 240 + y;      // x=15 -> (15 * 16) = 240
                    int idxYZ = z * S + y;               // plane index
                    int w = idxYZ >> 6; int b = idxYZ & 63;

                    if ((occ[liNeg >> 6] & (1UL << (liNeg & 63))) != 0UL)
                        sec.FaceNegXBits[w] |= 1UL << b;
                    if ((occ[liPos >> 6] & (1UL << (liPos & 63))) != 0UL)
                        sec.FacePosXBits[w] |= 1UL << b;
                }
            }

            // Y faces (y = 0 and y = 15) -> XZ plane (x,z)
            for (int x = 0; x < S; x++)
            {
                int xOffset16 = x * 16;
                for (int z = 0; z < S; z++)
                {
                    int ci = z * S + x;          // (z,x) pair inside XZ iteration for convenience
                    // Reconstruct li base for (z,x,y) ordering:
                    // li = ((z*16 + x)*16)+y = (z*256) + (x*16) + y
                    int baseZX = z * 256 + xOffset16;

                    int liNeg = baseZX + 0;      // y=0
                    int liPos = baseZX + 15;     // y=15
                    int idxXZ = x * S + z;       // plane index mapping (x,z)
                    int w = idxXZ >> 6; int b = idxXZ & 63;

                    if ((occ[liNeg >> 6] & (1UL << (liNeg & 63))) != 0UL)
                        sec.FaceNegYBits[w] |= 1UL << b;
                    if ((occ[liPos >> 6] & (1UL << (liPos & 63))) != 0UL)
                        sec.FacePosYBits[w] |= 1UL << b;
                }
            }

            // Z faces (z = 0 and z = 15) -> XY plane (x,y)
            for (int x = 0; x < S; x++)
            {
                int xOffset16 = x * 16;
                for (int y = 0; y < S; y++)
                {
                    int liNeg = xOffset16 + y;                 // z=0
                    int liPos = 15 * 256 + xOffset16 + y;      // z=15 -> 15*256 = 3840
                    int idxXY = x * S + y;                     // plane index (x,y)
                    int w = idxXY >> 6; int b = idxXY & 63;

                    if ((occ[liNeg >> 6] & (1UL << (liNeg & 63))) != 0UL)
                        sec.FaceNegZBits[w] |= 1UL << b;
                    if ((occ[liPos >> 6] & (1UL << (liPos & 63))) != 0UL)
                        sec.FacePosZBits[w] |= 1UL << b;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LinearIndex(int x, int y, int z)
            => ((z * SECTION_SIZE) + x) * SECTION_SIZE + y; // column-major: columnIndex*16 + y

        public static void DecodeLinear(int li, out int x, out int y, out int z)
        {
            y = li & 15; // low 4 bits
            int columnIndex = li >> 4; // 0..255
            x = columnIndex & 15;
            z = columnIndex >> 4;
        }

        private static int ReadBits(ChunkSection sec, int voxelIndex)
            => ReadBits(sec.BitData, sec.BitsPerIndex, voxelIndex);

        private static void GrowBits(ChunkSection sec)
        {
            int paletteCountMinusOne = sec.Palette.Count - 1;
            int needed = paletteCountMinusOne <= 0 ? 1 : (int)BitOperations.Log2((uint)paletteCountMinusOne) + 1;
            if (needed <= sec.BitsPerIndex) return;

            int oldBits = sec.BitsPerIndex;
            var oldData = sec.BitData;
            sec.BitsPerIndex = needed;
            AllocateBitData(sec);

            for (int i = 0; i < sec.VoxelCount; i++)
            {
                int pi = ReadBits(oldData, oldBits, i);
                WriteBits(sec, i, pi);
            }
        }

        private static void AllocateBitData(ChunkSection sec)
        {
            if (sec.BitsPerIndex <= 0)
            {
                sec.BitData = null;
                return;
            }
            long totalBits = (long)sec.VoxelCount * sec.BitsPerIndex;
            int uintCount = (int)((totalBits + 31) / 32);
            sec.BitData = RentBitData(uintCount);
            Array.Clear(sec.BitData, 0, uintCount);
        }

        private static int ReadBits(uint[] data, int bpi, int voxelIndex)
        {
            if (bpi == 0) return 0;
            long bitPos = (long)voxelIndex * bpi;
            int dataIndex = (int)(bitPos >> 5);
            int bitOffset = (int)(bitPos & 31);
            uint value = data[dataIndex] >> bitOffset;
            int mask = (1 << bpi) - 1;
            int remaining = 32 - bitOffset;
            if (remaining < bpi)
            {
                value |= data[dataIndex + 1] << remaining;
            }
            return (int)(value & (uint)mask);
        }

        private static void WriteBits(ChunkSection sec, int voxelIndex, int paletteIndex)
        {
            long bitPos = (long)voxelIndex * sec.BitsPerIndex;
            int dataIndex = (int)(bitPos >> 5);
            int bitOffset = (int)(bitPos & 31);
            uint mask = (uint)((1 << sec.BitsPerIndex) - 1);

            sec.BitData[dataIndex] &= ~(mask << bitOffset);
            sec.BitData[dataIndex] |= (uint)paletteIndex << bitOffset;

            int remaining = 32 - bitOffset;
            if (remaining < sec.BitsPerIndex)
            {
                int bitsInNext = sec.BitsPerIndex - remaining;
                uint nextMask = (uint)((1 << bitsInNext) - 1);
                sec.BitData[dataIndex + 1] &= ~nextMask;
                sec.BitData[dataIndex + 1] |= (uint)paletteIndex >> remaining;
            }
        }

        private static void Collapse(ChunkSection sec)
        {
            sec.IsAllAir = true;
            sec.Palette = null;
            sec.PaletteLookup = null;
            ReturnBitData(sec.BitData);
            sec.BitData = null;
            sec.BitsPerIndex = 0;
            sec.NonAirCount = 0;
            sec.CompletelyFull = false;
        }

        private static int GetOrAddPaletteIndex(ChunkSection sec, ushort blockId)
        {
            if (sec.PaletteLookup.TryGetValue(blockId, out int idx))
                return idx;
            idx = sec.Palette.Count;
            sec.Palette.Add(blockId);
            sec.PaletteLookup[blockId] = idx;
            return idx;
        }

        private static void Initialize(ChunkSection sec)
        {
            sec.IsAllAir = false;
            sec.VoxelCount = VOXELS_PER_SECTION;
            sec.Palette = new List<ushort>(8) { ChunkSection.AIR };
            sec.PaletteLookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 } };
            sec.BitsPerIndex = 1;
            AllocateBitData(sec);
            sec.NonAirCount = 0;
            sec.CompletelyFull = false;
            sec.MetadataBuilt = false;
        }

        private static void EnsurePacked(ChunkSection sec)
        {
            if (sec.Kind == ChunkSection.RepresentationKind.Packed || sec.Kind == ChunkSection.RepresentationKind.MultiPacked) return;
            if (sec.Kind == ChunkSection.RepresentationKind.Empty)
            {
                sec.IsAllAir = true; sec.Palette = null; sec.PaletteLookup = null; sec.BitData = null; sec.BitsPerIndex = 0; sec.NonAirCount = 0; sec.CompletelyFull = false; return;
            }
            if (sec.Kind == ChunkSection.RepresentationKind.Uniform)
            {
                ushort blockId = sec.UniformBlockId;
                sec.IsAllAir = false;
                sec.VoxelCount = ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE * ChunkSection.SECTION_SIZE;
                sec.Palette = new List<ushort> { ChunkSection.AIR, blockId };
                sec.PaletteLookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 }, { blockId, 1 } };
                sec.BitsPerIndex = 1;
                long totalBits = (long)sec.VoxelCount * sec.BitsPerIndex;
                int uintCount = (int)((totalBits + 31) / 32);
                sec.BitData = ArrayPool<uint>.Shared.Rent(uintCount);
                for (int i = 0; i < uintCount; i++) sec.BitData[i] = 0xFFFFFFFFu;
                sec.NonAirCount = sec.VoxelCount;
                sec.CompletelyFull = true;
                sec.Kind = ChunkSection.RepresentationKind.Packed;
                sec.MetadataBuilt = false;
                return;
            }
            // For Sparse / DenseExpanded fallback: rebuild packed from existing data
            if (sec.Kind == ChunkSection.RepresentationKind.DenseExpanded)
            {
                // Repack dense expanded
                ushort[] dense = sec.ExpandedDense;
                // Build palette anew
                var palette = new List<ushort> { ChunkSection.AIR };
                var lookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 } };
                for (int i = 0; i < dense.Length; i++)
                {
                    ushort id = dense[i]; if (id == ChunkSection.AIR) continue;
                    if (!lookup.ContainsKey(id)) { lookup[id] = palette.Count; palette.Add(id); }
                }
                sec.Palette = palette; sec.PaletteLookup = lookup;
                int paletteCountMinusOne = palette.Count - 1;
                int bpi = paletteCountMinusOne <= 0 ? 1 : (int)BitOperations.Log2((uint)paletteCountMinusOne) + 1;
                sec.BitsPerIndex = bpi;
                long totalBits2 = (long)sec.VoxelCount * bpi;
                int uintCount2 = (int)((totalBits2 + 31) / 32);
                sec.BitData = ArrayPool<uint>.Shared.Rent(uintCount2); Array.Clear(sec.BitData, 0, uintCount2);
                for (int i = 0; i < dense.Length; i++)
                {
                    ushort id = dense[i];
                    if (id == ChunkSection.AIR) continue;
                    int pi = lookup[id];
                    WriteBits(sec, i, pi);
                }
                sec.Kind = ChunkSection.RepresentationKind.Packed; sec.MetadataBuilt = false;
                return;
            }
            if (sec.Kind == ChunkSection.RepresentationKind.Sparse)
            {
                // Repack sparse
                var palette = new List<ushort> { ChunkSection.AIR };
                var lookup = new Dictionary<ushort, int> { { ChunkSection.AIR, 0 } };
                var idx = sec.SparseIndices; var blocks = sec.SparseBlocks;
                for (int i = 0; i < blocks.Length; i++)
                {
                    ushort id = blocks[i]; if (id == ChunkSection.AIR) continue;
                    if (!lookup.ContainsKey(id)) { lookup[id] = palette.Count; palette.Add(id); }
                }
                sec.Palette = palette; sec.PaletteLookup = lookup;
                int paletteCountMinusOne = palette.Count - 1;
                int bpi = paletteCountMinusOne <= 0 ? 1 : (int)BitOperations.Log2((uint)paletteCountMinusOne) + 1;
                sec.BitsPerIndex = bpi;
                long totalBits2 = (long)sec.VoxelCount * bpi;
                int uintCount2 = (int)((totalBits2 + 31) / 32);
                sec.BitData = ArrayPool<uint>.Shared.Rent(uintCount2); Array.Clear(sec.BitData, 0, uintCount2);
                for (int i = 0; i < idx.Length; i++)
                {
                    int li = idx[i]; ushort id = blocks[i]; int pi = lookup[id]; WriteBits(sec, li, pi);
                }
                sec.Kind = ChunkSection.RepresentationKind.Packed; sec.MetadataBuilt = false;
            }
        }
    }
}

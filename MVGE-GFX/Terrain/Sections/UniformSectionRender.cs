using MVGE_INF.Models.Generation;
using MVGE_GFX.Models;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Optimized emission for uniform (completely solid) section.
        private void EmitUniformSection(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            int S = data.sectionSize; // usually 16
            ushort block = desc.UniformBlockId;
            if (block == 0)
            {
                // Kind reported uniform but block id is air – treat as empty (defensive)
                return;
            }

            // Guard against vertex coordinate overflow (since we pack into bytes)
            Debug.Assert(data.maxX <= 256 && data.maxY <= 256 && data.maxZ <= 256,
                "Chunk dimension exceeds 255 – packed byte vertices will overflow.");

            // UV cache – fixed 6 faces
            var uvCache = new List<ByteVector2>[6];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            List<ByteVector2> GetUV(Faces f)
            {
                int i = (int)f;
                return uvCache[i] ??= atlas.GetBlockUVs(block, f);
            }

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;
            int maxX = data.maxX;
            int maxY = data.maxY;
            int maxZ = data.maxZ;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            SectionPrerenderDesc? Neighbor(int nsx, int nsy, int nsz)
            {
                if (nsx < 0 || nsx >= data.sectionsX ||
                    nsy < 0 || nsy >= data.sectionsY ||
                    nsz < 0 || nsz >= data.sectionsZ)
                    return null;
                int ni = ((nsx * data.sectionsY) + nsy) * data.sectionsZ + nsz;
                return data.SectionDescs[ni];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool Bit(ulong[] arr, int idx)
                => arr != null && (idx >> 6) < arr.Length && (arr[idx >> 6] & (1UL << (idx & 63))) != 0UL;

            // General (S-agnostic) linear index used by occupancy bitsets: ((z * S) + x) * S + y
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int OccIndex(int x, int y, int z, int S)
                => ((z * S) + x) * S + y;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool OccBit(ulong[] occ, int x, int y, int z, int S)
                => occ != null && Bit(occ, OccIndex(x, y, z, S));

            // Helper: decode presence of a voxel within a SectionPrerenderDesc when neither face bits
            // nor occupancy bitset are available (fallback correctness path to avoid over-emission).
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool HasVoxel(SectionPrerenderDesc s, int lx, int ly, int lz, int S)
            {
                switch (s.Kind)
                {
                    case 0: // Empty
                        return false;
                    case 1: // Uniform
                        return s.UniformBlockId != 0;
                    case 2: // Sparse
                        if (s.SparseIndices == null) return false;
                        int ci = lz * S + lx; // column index
                        int liSparse = (ci << 4) + ly; // old layout assumption (16)
                        int target = (ci * S) + ly;   // generic layout
                        foreach (var idx in s.SparseIndices)
                        {
                            if (idx == liSparse || idx == target) return true;
                        }
                        return false;
                    case 3: // DenseExpanded
                        if (s.ExpandedDense == null) return false;
                        return s.ExpandedDense[((lz * S + lx) * S) + ly] != 0;
                    case 4: // Packed single-id or palette
                        if (s.PackedBitData == null || s.Palette == null || s.BitsPerIndex <= 0)
                            return false;
                        int li = ((lz * S + lx) * S) + ly;
                        long bitPos = (long)li * s.BitsPerIndex;
                        int word = (int)(bitPos >> 5);
                        int bitOffset = (int)(bitPos & 31);
                        uint value = s.PackedBitData[word] >> bitOffset;
                        int rem = 32 - bitOffset;
                        if (rem < s.BitsPerIndex)
                            value |= s.PackedBitData[word + 1] << rem;
                        int mask = (1 << s.BitsPerIndex) - 1;
                        int paletteIndex = (int)(value & mask);
                        if (paletteIndex <= 0 || paletteIndex >= s.Palette.Count) return false;
                        return s.Palette[paletteIndex] != 0;
                    default:
                        return false;
                }
            }

            // Emit a single quad (adds 4 verts + 6 indices)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            uint EmitFace(Faces face, int wx, int wy, int wz, uint vb)
            {
                var vtx = RawFaceData.rawVertexData[face];
                for (int i = 0; i < 4; i++)
                {
                    vertList.Add((byte)(vtx[i].x + wx));
                    vertList.Add((byte)(vtx[i].y + wy));
                    vertList.Add((byte)(vtx[i].z + wz));
                }
                var uvFace = GetUV(face);
                for (int i = 0; i < 4; i++)
                {
                    uvList.Add(uvFace[i].x);
                    uvList.Add(uvFace[i].y);
                }
                idxList.Add(vb + 0); idxList.Add(vb + 1); idxList.Add(vb + 2);
                idxList.Add(vb + 2); idxList.Add(vb + 3); idxList.Add(vb + 0);
                return vb + 4;
            }

            // Neighbor chunk plane arrays (may be null if neighbor chunk missing)
            var planeNegX = data.NeighborPlaneNegX; // neighbor at -X (+X face)
            var planePosX = data.NeighborPlanePosX; // neighbor at +X (-X face)
            var planeNegY = data.NeighborPlaneNegY; // neighbor at -Y (+Y face)
            var planePosY = data.NeighborPlanePosY; // neighbor at +Y (-Y face)
            var planeNegZ = data.NeighborPlaneNegZ; // neighbor at -Z (+Z face)
            var planePosZ = data.NeighborPlanePosZ; // neighbor at +Z (-Z face)

            // -------------------- -X FACE (LEFT) --------------------
            if (sx == 0)
            {
                uint vb = vertBase;
                for (int lz = 0; lz < S; lz++)
                {
                    int gz = baseZ + lz;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly;
                        int planeIdx = gz * maxY + gy; // YZ layout
                        bool coveredByNeighborChunk = Bit(planeNegX, planeIdx);
                        if (!coveredByNeighborChunk)
                            vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx - 1, sy, sz);
                if (n == null || n.Value.NonAirCount == 0)
                {
                    uint vb = vertBase;
                    for (int lz = 0; lz < S; lz++)
                    {
                        for (int ly = 0; ly < S; ly++)
                            vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                    }
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FacePosXBits; // neighbor +X face bits
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lz = 0; lz < S; lz++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool covered = false;
                            if (bits != null)
                                covered = Bit(bits, lz * S + ly);
                            else if (occ != null)
                                covered = OccBit(occ, S - 1, ly, lz, S);
                            else
                                covered = HasVoxel(n.Value, S - 1, ly, lz, S);

                            if (!covered)
                                vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                        }
                    }
                    vertBase = vb;
                }
            }

            // -------------------- +X FACE (RIGHT) --------------------
            if (sx == data.sectionsX - 1)
            {
                uint vb = vertBase;
                int wx = baseX + S - 1;
                for (int lz = 0; lz < S; lz++)
                {
                    int gz = baseZ + lz;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly;
                        int planeIdx = gz * maxY + gy;
                        bool covered = Bit(planePosX, planeIdx);
                        if (!covered)
                            vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx + 1, sy, sz);
                if (n == null || n.Value.NonAirCount == 0)
                {
                    uint vb = vertBase;
                    int wx = baseX + S - 1;
                    for (int lz = 0; lz < S; lz++)
                        for (int ly = 0; ly < S; ly++)
                            vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FaceNegXBits; // neighbor -X face bits
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    int wx = baseX + S - 1;
                    for (int lz = 0; lz < S; lz++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool covered = false;
                            if (bits != null)
                                covered = Bit(bits, lz * S + ly);
                            else if (occ != null)
                                covered = OccBit(occ, 0, ly, lz, S); // neighbor cell at x=0
                            else
                                covered = HasVoxel(n.Value, 0, ly, lz, S);

                            if (!covered)
                                vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                        }
                    }
                    vertBase = vb;
                }
            }

            // -------------------- -Y FACE (BOTTOM) --------------------
            if (sy == 0)
            {
                uint vb = vertBase;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int lz = 0; lz < S; lz++)
                    {
                        int gz = baseZ + lz;
                        int planeIdx = gx * maxZ + gz; // XZ layout
                        bool covered = Bit(planeNegY, planeIdx);
                        if (!covered)
                            vb = EmitFace(Faces.BOTTOM, gx, baseY, gz, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx, sy - 1, sz);
                if (n == null || n.Value.NonAirCount == 0)
                {
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                        for (int lz = 0; lz < S; lz++)
                            vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FacePosYBits; // neighbor +Y face bits
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int lz = 0; lz < S; lz++)
                        {
                            bool covered = false;
                            if (bits != null)
                                covered = Bit(bits, lx * S + lz);
                            else if (occ != null)
                                covered = OccBit(occ, lx, S - 1, lz, S);
                            else
                                covered = HasVoxel(n.Value, lx, S - 1, lz, S);

                            if (!covered)
                                vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
                        }
                    }
                    vertBase = vb;
                }
            }

            // -------------------- +Y FACE (TOP) --------------------
            if (sy == data.sectionsY - 1)
            {
                uint vb = vertBase;
                int wy = baseY + S - 1;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int lz = 0; lz < S; lz++)
                    {
                        int gz = baseZ + lz;
                        int planeIdx = gx * maxZ + gz;
                        bool covered = Bit(planePosY, planeIdx);
                        if (!covered)
                            vb = EmitFace(Faces.TOP, gx, wy, gz, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx, sy + 1, sz);
                if (n == null || n.Value.NonAirCount == 0)
                {
                    uint vb = vertBase;
                    int wy = baseY + S - 1;
                    for (int lx = 0; lx < S; lx++)
                        for (int lz = 0; lz < S; lz++)
                            vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FaceNegYBits; // neighbor -Y face bits
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    int wy = baseY + S - 1;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int lz = 0; lz < S; lz++)
                        {
                            bool covered = false;
                            if (bits != null)
                                covered = Bit(bits, lx * S + lz);
                            else if (occ != null)
                                covered = OccBit(occ, lx, 0, lz, S);
                            else
                                covered = HasVoxel(n.Value, lx, 0, lz, S);

                            if (!covered)
                                vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb);
                        }
                    }
                    vertBase = vb;
                }
            }

            // -------------------- -Z FACE (BACK) --------------------
            if (sz == 0)
            {
                uint vb = vertBase;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly;
                        int planeIdx = gx * maxY + gy; // XY layout
                        bool covered = Bit(planeNegZ, planeIdx);
                        if (!covered)
                            vb = EmitFace(Faces.BACK, gx, gy, baseZ, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx, sy, sz - 1);
                if (n == null || n.Value.NonAirCount == 0)
                {
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                        for (int ly = 0; ly < S; ly++)
                            vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FacePosZBits; // neighbor +Z face bits
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool covered = false;
                            if (bits != null)
                                covered = Bit(bits, lx * S + ly);
                            else if (occ != null)
                                covered = OccBit(occ, lx, ly, S - 1, S);
                            else
                                covered = HasVoxel(n.Value, lx, ly, S - 1, S);

                            if (!covered)
                                vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb);
                        }
                    }
                    vertBase = vb;
                }
            }

            // -------------------- +Z FACE (FRONT) --------------------
            if (sz == data.sectionsZ - 1)
            {
                uint vb = vertBase;
                int wz = baseZ + S - 1;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly;
                        int planeIdx = gx * maxY + gy;
                        bool covered = Bit(planePosZ, planeIdx);
                        if (!covered)
                            vb = EmitFace(Faces.FRONT, gx, gy, wz, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx, sy, sz + 1);
                if (n == null || n.Value.NonAirCount == 0)
                {
                    uint vb = vertBase;
                    int wz = baseZ + S - 1;
                    for (int lx = 0; lx < S; lx++)
                        for (int ly = 0; ly < S; ly++)
                            vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FaceNegZBits; // neighbor -Z face bits
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    int wz = baseZ + S - 1;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool covered = false;
                            if (bits != null)
                                covered = Bit(bits, lx * S + ly);
                            else if (occ != null)
                                covered = OccBit(occ, lx, ly, 0, S);
                            else
                                covered = HasVoxel(n.Value, lx, ly, 0, S);

                            if (!covered)
                                vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb);
                        }
                    }
                    vertBase = vb;
                }
            }
        }
    }
}

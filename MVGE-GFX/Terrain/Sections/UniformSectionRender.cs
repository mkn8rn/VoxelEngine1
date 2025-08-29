using MVGE_INF.Models.Generation;
using MVGE_GFX.Models;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Optimized emission for uniform (completely solid) section with opacity awareness.
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
                // Defensive: uniform descriptor but air
                return;
            }

            bool thisOpaque = BlockProperties.IsOpaque(block);

            Debug.Assert(data.maxX <= 256 && data.maxY <= 256 && data.maxZ <= 256,
                "Chunk dimension exceeds 255 – packed byte vertices will overflow.");

            // UV cache per face (array index by Faces enum)
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
            int maxY = data.maxY;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            SectionPrerenderDesc? Neighbor(int nsx, int nsy, int nsz)
            {
                if ((uint)nsx >= (uint)data.sectionsX ||
                    (uint)nsy >= (uint)data.sectionsY ||
                    (uint)nsz >= (uint)data.sectionsZ)
                    return null;
                int ni = ((nsx * data.sectionsY) + nsy) * data.sectionsZ + nsz;
                return data.SectionDescs[ni];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool Bit(ulong[] arr, int idx)
            {
                return arr != null && (idx >> 6) < arr.Length && (arr[idx >> 6] & (1UL << (idx & 63))) != 0UL;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int OccIndex(int x, int y, int z, int S)
            {
                return ((z * S) + x) * S + y;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool OccBit(ulong[] occ, int x, int y, int z, int S)
            {
                return occ != null && Bit(occ, OccIndex(x, y, z, S));
            }

            // Returns true if neighbor cell at (lx,ly,lz) exists AND is opaque.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool NeighborOpaque(SectionPrerenderDesc s, int lx, int ly, int lz, int S)
            {
                switch (s.Kind)
                {
                    case 0: // Empty
                        return false;
                    case 1: // Uniform
                        return BlockProperties.IsOpaque(s.UniformBlockId);
                    case 2: // Sparse
                        if (s.SparseIndices == null || s.SparseBlocks == null) return false;
                        int ci = lz * S + lx;
                        int liLegacy = (ci << 4) + ly; // legacy layout (S==16)
                        int generic = (ci * S) + ly;    // generic layout
                        for (int i = 0; i < s.SparseIndices.Length; i++)
                        {
                            int idx = s.SparseIndices[i];
                            if (idx == liLegacy || idx == generic)
                                return BlockProperties.IsOpaque(s.SparseBlocks[i]);
                        }
                        return false;
                    case 3: // DenseExpanded
                        if (s.ExpandedDense == null) return false;
                        return BlockProperties.IsOpaque(s.ExpandedDense[((lz * S + lx) * S) + ly]);
                    case 4: // Packed
                        if (s.PackedBitData == null || s.Palette == null || s.BitsPerIndex <= 0) return false;
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
                        return BlockProperties.IsOpaque(s.Palette[paletteIndex]);
                    default:
                        return false;
                }
            }

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
                idxList.Add(vb + 0);
                idxList.Add(vb + 1);
                idxList.Add(vb + 2);
                idxList.Add(vb + 2);
                idxList.Add(vb + 3);
                idxList.Add(vb + 0);
                return vb + 4;
            }

            // Neighbor chunk plane arrays
            var planeNegX = data.NeighborPlaneNegX;
            var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY;
            var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ;
            var planePosZ = data.NeighborPlanePosZ;

            // ---------------------- -X (LEFT) ----------------------
            if (sx == 0)
            {
                uint vb = vertBase;
                for (int lz = 0; lz < S; lz++)
                {
                    int gz = baseZ + lz;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly;
                        int planeIdx = gz * maxY + gy;
                        bool covered = Bit(planeNegX, planeIdx);
                        if (!covered || !thisOpaque)
                        {
                            vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                        }
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
                        for (int ly = 0; ly < S; ly++)
                            vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind == 1)
                {
                    // Neighbor uniform: occlude only if both opaque
                    if (!(thisOpaque && BlockProperties.IsOpaque(n.Value.UniformBlockId)))
                    {
                        uint vb = vertBase;
                        for (int lz = 0; lz < S; lz++)
                            for (int ly = 0; ly < S; ly++)
                                vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                        vertBase = vb;
                    }
                }
                else
                {
                    var bits = n.Value.FacePosXBits;
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lz = 0; lz < S; lz++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool opaqueNeighbor;
                            if (bits != null)
                            {
                                opaqueNeighbor = Bit(bits, lz * S + ly); // face bits imply opaque
                            }
                            else if (occ != null)
                            {
                                opaqueNeighbor = OccBit(occ, S - 1, ly, lz, S) && NeighborOpaque(n.Value, S - 1, ly, lz, S);
                            }
                            else
                            {
                                opaqueNeighbor = NeighborOpaque(n.Value, S - 1, ly, lz, S);
                            }
                            if (!(thisOpaque && opaqueNeighbor))
                            {
                                vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                            }
                        }
                    }
                    vertBase = vb;
                }
            }

            // ---------------------- +X (RIGHT) ----------------------
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
                        if (!covered || !thisOpaque)
                        {
                            vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                        }
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
                else if (n.Value.Kind == 1)
                {
                    if (!(thisOpaque && BlockProperties.IsOpaque(n.Value.UniformBlockId)))
                    {
                        uint vb = vertBase;
                        int wx = baseX + S - 1;
                        for (int lz = 0; lz < S; lz++)
                            for (int ly = 0; ly < S; ly++)
                                vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                        vertBase = vb;
                    }
                }
                else
                {
                    var bits = n.Value.FaceNegXBits;
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    int wx = baseX + S - 1;
                    for (int lz = 0; lz < S; lz++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool opaqueNeighbor;
                            if (bits != null)
                            {
                                opaqueNeighbor = Bit(bits, lz * S + ly);
                            }
                            else if (occ != null)
                            {
                                opaqueNeighbor = OccBit(occ, 0, ly, lz, S) && NeighborOpaque(n.Value, 0, ly, lz, S);
                            }
                            else
                            {
                                opaqueNeighbor = NeighborOpaque(n.Value, 0, ly, lz, S);
                            }
                            if (!(thisOpaque && opaqueNeighbor))
                            {
                                vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                            }
                        }
                    }
                    vertBase = vb;
                }
            }

            // ---------------------- -Y (BOTTOM) ----------------------
            if (sy == 0)
            {
                uint vb = vertBase;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int lz = 0; lz < S; lz++)
                    {
                        int gz = baseZ + lz;
                        int planeIdx = gx * data.maxZ + gz;
                        bool covered = Bit(planeNegY, planeIdx);
                        if (!covered || !thisOpaque)
                        {
                            vb = EmitFace(Faces.BOTTOM, gx, baseY, gz, vb);
                        }
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
                else if (n.Value.Kind == 1)
                {
                    if (!(thisOpaque && BlockProperties.IsOpaque(n.Value.UniformBlockId)))
                    {
                        uint vb = vertBase;
                        for (int lx = 0; lx < S; lx++)
                            for (int lz = 0; lz < S; lz++)
                                vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
                        vertBase = vb;
                    }
                }
                else
                {
                    var bits = n.Value.FacePosYBits;
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int lz = 0; lz < S; lz++)
                        {
                            bool opaqueNeighbor;
                            if (bits != null)
                            {
                                opaqueNeighbor = Bit(bits, lx * S + lz);
                            }
                            else if (occ != null)
                            {
                                opaqueNeighbor = OccBit(occ, lx, S - 1, lz, S) && NeighborOpaque(n.Value, lx, S - 1, lz, S);
                            }
                            else
                            {
                                opaqueNeighbor = NeighborOpaque(n.Value, lx, S - 1, lz, S);
                            }
                            if (!(thisOpaque && opaqueNeighbor))
                            {
                                vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
                            }
                        }
                    }
                    vertBase = vb;
                }
            }

            // ---------------------- +Y (TOP) ----------------------
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
                        int planeIdx = gx * data.maxZ + gz;
                        bool covered = Bit(planePosY, planeIdx);
                        if (!covered || !thisOpaque)
                        {
                            vb = EmitFace(Faces.TOP, gx, wy, gz, vb);
                        }
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
                else if (n.Value.Kind == 1)
                {
                    if (!(thisOpaque && BlockProperties.IsOpaque(n.Value.UniformBlockId)))
                    {
                        uint vb = vertBase;
                        int wy = baseY + S - 1;
                        for (int lx = 0; lx < S; lx++)
                            for (int lz = 0; lz < S; lz++)
                                vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb);
                        vertBase = vb;
                    }
                }
                else
                {
                    var bits = n.Value.FaceNegYBits;
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    int wy = baseY + S - 1;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int lz = 0; lz < S; lz++)
                        {
                            bool opaqueNeighbor;
                            if (bits != null)
                            {
                                opaqueNeighbor = Bit(bits, lx * S + lz);
                            }
                            else if (occ != null)
                            {
                                opaqueNeighbor = OccBit(occ, lx, 0, lz, S) && NeighborOpaque(n.Value, lx, 0, lz, S);
                            }
                            else
                            {
                                opaqueNeighbor = NeighborOpaque(n.Value, lx, 0, lz, S);
                            }
                            if (!(thisOpaque && opaqueNeighbor))
                            {
                                vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb);
                            }
                        }
                    }
                    vertBase = vb;
                }
            }

            // ---------------------- -Z (BACK) ----------------------
            if (sz == 0)
            {
                uint vb = vertBase;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly;
                        int planeIdx = gx * maxY + gy;
                        bool covered = Bit(planeNegZ, planeIdx);
                        if (!covered || !thisOpaque)
                        {
                            vb = EmitFace(Faces.BACK, gx, gy, baseZ, vb);
                        }
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
                else if (n.Value.Kind == 1)
                {
                    if (!(thisOpaque && BlockProperties.IsOpaque(n.Value.UniformBlockId)))
                    {
                        uint vb = vertBase;
                        for (int lx = 0; lx < S; lx++)
                            for (int ly = 0; ly < S; ly++)
                                vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb);
                        vertBase = vb;
                    }
                }
                else
                {
                    var bits = n.Value.FacePosZBits;
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool opaqueNeighbor;
                            if (bits != null)
                            {
                                opaqueNeighbor = Bit(bits, lx * S + ly);
                            }
                            else if (occ != null)
                            {
                                opaqueNeighbor = OccBit(occ, lx, ly, S - 1, S) && NeighborOpaque(n.Value, lx, ly, S - 1, S);
                            }
                            else
                            {
                                opaqueNeighbor = NeighborOpaque(n.Value, lx, ly, S - 1, S);
                            }
                            if (!(thisOpaque && opaqueNeighbor))
                            {
                                vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb);
                            }
                        }
                    }
                    vertBase = vb;
                }
            }

            // ---------------------- +Z (FRONT) ----------------------
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
                        if (!covered || !thisOpaque)
                        {
                            vb = EmitFace(Faces.FRONT, gx, gy, wz, vb);
                        }
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
                else if (n.Value.Kind == 1)
                {
                    if (!(thisOpaque && BlockProperties.IsOpaque(n.Value.UniformBlockId)))
                    {
                        uint vb = vertBase;
                        int wz = baseZ + S - 1;
                        for (int lx = 0; lx < S; lx++)
                            for (int ly = 0; ly < S; ly++)
                                vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb);
                        vertBase = vb;
                    }
                }
                else
                {
                    var bits = n.Value.FaceNegZBits;
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    int wz = baseZ + S - 1;
                    for (int lx = 0; lx < S; lx++)
                    {
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool opaqueNeighbor;
                            if (bits != null)
                            {
                                opaqueNeighbor = Bit(bits, lx * S + ly);
                            }
                            else if (occ != null)
                            {
                                opaqueNeighbor = OccBit(occ, lx, ly, 0, S) && NeighborOpaque(n.Value, lx, ly, 0, S);
                            }
                            else
                            {
                                opaqueNeighbor = NeighborOpaque(n.Value, lx, ly, 0, S);
                            }
                            if (!(thisOpaque && opaqueNeighbor))
                            {
                                vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb);
                            }
                        }
                    }
                    vertBase = vb;
                }
            }
        }
    }
}

using MVGE_INF.Models.Generation;
using MVGE_GFX.Models;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // Optimized emission for uniform (completely solid) section.
        // Uses neighbor section face bitsets (when non-uniform) to cull interior faces.
        private void EmitUniformSection(
            ref SectionPrerenderDesc desc,
            int sx, int sy, int sz,
            List<byte> vertList,
            List<byte> uvList,
            List<uint> idxList,
            ref uint vertBase)
        {
            int S = data.sectionSize; // expected 16
            ushort block = desc.UniformBlockId;
            if (block == 0) return; // treat as empty

            // Per-face UV cache (avoid repeated atlas lookups)
            var uvCache = new Dictionary<Faces, List<ByteVector2>>(6);
            List<ByteVector2> GetUV(Faces f)
            {
                if (!uvCache.TryGetValue(f, out var list))
                {
                    list = atlas.GetBlockUVs(block, f);
                    uvCache[f] = list;
                }
                return list;
            }

            int baseX = sx * S;
            int baseY = sy * S;
            int baseZ = sz * S;

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
            static bool Bit(ulong[] arr, int idx) => arr != null && (arr[idx >> 6] & (1UL << (idx & 63))) != 0UL;

            // Emit a single quad face
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

            // --- NEGATIVE X FACE (LEFT) ---
            if (sx == 0)
            {
                uint vb = vertBase;
                for (int lz = 0; lz < S; lz++)
                    for (int ly = 0; ly < S; ly++)
                        vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
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
                else if (n.Value.Kind != 1) // neighbor not uniform -> use face mask
                {
                    var bits = n.Value.FacePosXBits; // YZ plane (z*16 + y)
                    uint vb = vertBase;
                    for (int lz = 0; lz < S; lz++)
                        for (int ly = 0; ly < S; ly++)
                            if (!Bit(bits, lz * S + ly))
                                vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                    vertBase = vb;
                }
            }

            // --- POSITIVE X FACE (RIGHT) ---
            if (sx == data.sectionsX - 1)
            {
                uint vb = vertBase;
                int wx = baseX + S - 1;
                for (int lz = 0; lz < S; lz++)
                    for (int ly = 0; ly < S; ly++)
                        vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
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
                    var bits = n.Value.FaceNegXBits;
                    uint vb = vertBase;
                    int wx = baseX + S - 1;
                    for (int lz = 0; lz < S; lz++)
                        for (int ly = 0; ly < S; ly++)
                            if (!Bit(bits, lz * S + ly))
                                vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                    vertBase = vb;
                }
            }

            // --- NEGATIVE Y FACE (BOTTOM) ---
            if (sy == 0)
            {
                uint vb = vertBase;
                for (int lx = 0; lx < S; lx++)
                    for (int lz = 0; lz < S; lz++)
                        vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
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
                    var bits = n.Value.FacePosYBits; // XZ plane (x*16 + z)
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                        for (int lz = 0; lz < S; lz++)
                            if (!Bit(bits, lx * S + lz))
                                vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
                    vertBase = vb;
                }
            }

            // --- POSITIVE Y FACE (TOP) ---
            if (sy == data.sectionsY - 1)
            {
                uint vb = vertBase;
                int wy = baseY + S - 1;
                for (int lx = 0; lx < S; lx++)
                    for (int lz = 0; lz < S; lz++)
                        vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb);
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
                    var bits = n.Value.FaceNegYBits;
                    uint vb = vertBase;
                    int wy = baseY + S - 1;
                    for (int lx = 0; lx < S; lx++)
                        for (int lz = 0; lz < S; lz++)
                            if (!Bit(bits, lx * S + lz))
                                vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb);
                    vertBase = vb;
                }
            }

            // --- NEGATIVE Z FACE (BACK) ---
            if (sz == 0)
            {
                uint vb = vertBase;
                for (int lx = 0; lx < S; lx++)
                    for (int ly = 0; ly < S; ly++)
                        vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb);
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
                    var bits = n.Value.FacePosZBits; // XY plane (x*16 + y)
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                        for (int ly = 0; ly < S; ly++)
                            if (!Bit(bits, lx * S + ly))
                                vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb);
                    vertBase = vb;
                }
            }

            // --- POSITIVE Z FACE (FRONT) ---
            if (sz == data.sectionsZ - 1)
            {
                uint vb = vertBase;
                int wz = baseZ + S - 1;
                for (int lx = 0; lx < S; lx++)
                    for (int ly = 0; ly < S; ly++)
                        vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb);
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
                    var bits = n.Value.FaceNegZBits;
                    uint vb = vertBase;
                    int wz = baseZ + S - 1;
                    for (int lx = 0; lx < S; lx++)
                        for (int ly = 0; ly < S; ly++)
                            if (!Bit(bits, lx * S + ly))
                                vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb);
                    vertBase = vb;
                }
            }
        }
    }
}

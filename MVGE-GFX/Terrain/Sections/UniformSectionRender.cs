using MVGE_INF.Models.Generation;
using MVGE_GFX.Models;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
            int S = data.sectionSize; // expected 16
            ushort block = desc.UniformBlockId;
            if (block == 0) return; // treat as empty

            // Cache for UVs per face
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
            int maxX = data.maxX; int maxY = data.maxY; int maxZ = data.maxZ;

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
            static bool Bit(ulong[] arr, int idx) => arr != null && (idx >> 6) < arr.Length && (arr[idx >> 6] & (1UL << (idx & 63))) != 0UL;

            // Emit a single quad
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
                for (int i = 0; i < 4; i++) { uvList.Add(uvFace[i].x); uvList.Add(uvFace[i].y); }
                idxList.Add(vb + 0); idxList.Add(vb + 1); idxList.Add(vb + 2); idxList.Add(vb + 2); idxList.Add(vb + 3); idxList.Add(vb + 0);
                return vb + 4;
            }

            // Neighbor chunk plane arrays
            var planeNegX = data.NeighborPlaneNegX; // neighbor at -X (+X face)
            var planePosX = data.NeighborPlanePosX;
            var planeNegY = data.NeighborPlaneNegY; // neighbor at -Y (+Y face)
            var planePosY = data.NeighborPlanePosY;
            var planeNegZ = data.NeighborPlaneNegZ; // neighbor at -Z (+Z face)
            var planePosZ = data.NeighborPlanePosZ;

            // -------------- -X --------------
            if (sx == 0)
            {
                // Use neighbor chunk plane to cull
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
                        for (int ly = 0; ly < S; ly++)
                            vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    // Non-uniform neighbor: use its +X face mask; if mask missing fall back to occupancy bits test
                    var bits = n.Value.FacePosXBits;
                    var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lz = 0; lz < S; lz++)
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool covered = bits != null ? Bit(bits, lz * S + ly)
                                : occ != null && Bit(occ, ((lz * S + (S - 1)) * S + ly)); // sample cell just inside neighbor boundary
                            if (!covered)
                                vb = EmitFace(Faces.LEFT, baseX, baseY + ly, baseZ + lz, vb);
                        }
                    vertBase = vb;
                }
            }

            // -------------- +X --------------
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
                    uint vb = vertBase; int wx = baseX + S - 1;
                    for (int lz = 0; lz < S; lz++) for (int ly = 0; ly < S; ly++) vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FaceNegXBits; var occ = n.Value.OccupancyBits;
                    uint vb = vertBase; int wx = baseX + S - 1;
                    for (int lz = 0; lz < S; lz++)
                        for (int ly = 0; ly < S; ly++)
                        {
                            bool covered = bits != null ? Bit(bits, lz * S + ly)
                                : occ != null && Bit(occ, ((lz * S + 0) * S + ly));
                            if (!covered) vb = EmitFace(Faces.RIGHT, wx, baseY + ly, baseZ + lz, vb);
                        }
                    vertBase = vb;
                }
            }

            // -------------- -Y --------------
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
                    for (int lx = 0; lx < S; lx++) for (int lz = 0; lz < S; lz++) vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
                    vertBase = vb;
                }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FacePosYBits; var occ = n.Value.OccupancyBits;
                    uint vb = vertBase;
                    for (int lx = 0; lx < S; lx++)
                        for (int lz = 0; lz < S; lz++)
                        {
                            bool covered = bits != null ? Bit(bits, lx * S + lz)
                                : occ != null && Bit(occ, ((lz * S + lx) * S + (S - 1))); // y=15 inside neighbor
                            if (!covered) vb = EmitFace(Faces.BOTTOM, baseX + lx, baseY, baseZ + lz, vb);
                        }
                    vertBase = vb;
                }
            }

            // -------------- +Y --------------
            if (sy == data.sectionsY - 1)
            {
                uint vb = vertBase; int wy = baseY + S - 1;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int lz = 0; lz < S; lz++)
                    {
                        int gz = baseZ + lz; int planeIdx = gx * maxZ + gz;
                        bool covered = Bit(planePosY, planeIdx);
                        if (!covered) vb = EmitFace(Faces.TOP, gx, wy, gz, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx, sy + 1, sz);
                if (n == null || n.Value.NonAirCount == 0)
                { uint vb = vertBase; int wy = baseY + S - 1; for (int lx = 0; lx < S; lx++) for (int lz = 0; lz < S; lz++) vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb); vertBase = vb; }
                else if (n.Value.Kind != 1)
                {
                    var bits = n.Value.FaceNegYBits; var occ = n.Value.OccupancyBits; uint vb = vertBase; int wy = baseY + S - 1;
                    for (int lx = 0; lx < S; lx++) for (int lz = 0; lz < S; lz++)
                    {
                        bool covered = bits != null ? Bit(bits, lx * S + lz)
                            : occ != null && Bit(occ, ((lz * S + lx) * S + 0)); // y=0 of neighbor
                        if (!covered) vb = EmitFace(Faces.TOP, baseX + lx, wy, baseZ + lz, vb);
                    }
                    vertBase = vb;
                }
            }

            // -------------- -Z --------------
            if (sz == 0)
            {
                uint vb = vertBase;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly; int planeIdx = gx * maxY + gy; // XY layout
                        bool covered = Bit(planeNegZ, planeIdx);
                        if (!covered) vb = EmitFace(Faces.BACK, gx, gy, baseZ, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx, sy, sz - 1);
                if (n == null || n.Value.NonAirCount == 0)
                { uint vb = vertBase; for (int lx = 0; lx < S; lx++) for (int ly = 0; ly < S; ly++) vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb); vertBase = vb; }
                else if (n.Value.Kind != 1)
                { var bits = n.Value.FacePosZBits; var occ = n.Value.OccupancyBits; uint vb = vertBase; for (int lx = 0; lx < S; lx++) for (int ly = 0; ly < S; ly++) { bool covered = bits != null ? Bit(bits, lx * S + ly) : occ != null && Bit(occ, (((0) * S + lx) * S + ly)); if (!covered) vb = EmitFace(Faces.BACK, baseX + lx, baseY + ly, baseZ, vb); } vertBase = vb; }
            }

            // -------------- +Z --------------
            if (sz == data.sectionsZ - 1)
            {
                uint vb = vertBase; int wz = baseZ + S - 1;
                for (int lx = 0; lx < S; lx++)
                {
                    int gx = baseX + lx;
                    for (int ly = 0; ly < S; ly++)
                    {
                        int gy = baseY + ly; int planeIdx = gx * maxY + gy;
                        bool covered = Bit(planePosZ, planeIdx);
                        if (!covered) vb = EmitFace(Faces.FRONT, gx, gy, wz, vb);
                    }
                }
                vertBase = vb;
            }
            else
            {
                var n = Neighbor(sx, sy, sz + 1);
                if (n == null || n.Value.NonAirCount == 0)
                { uint vb = vertBase; int wz = baseZ + S - 1; for (int lx = 0; lx < S; lx++) for (int ly = 0; ly < S; ly++) vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb); vertBase = vb; }
                else if (n.Value.Kind != 1)
                { var bits = n.Value.FaceNegZBits; var occ = n.Value.OccupancyBits; uint vb = vertBase; int wz = baseZ + S - 1; for (int lx = 0; lx < S; lx++) for (int ly = 0; ly < S; ly++) { bool covered = bits != null ? Bit(bits, lx * S + ly) : occ != null && Bit(occ, (((S - 1) * S + lx) * S + ly)); if (!covered) vb = EmitFace(Faces.FRONT, baseX + lx, baseY + ly, wz, vb); } vertBase = vb; }
            }
        }
    }
}

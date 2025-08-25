using MVGE_GFX.Models;
using System;
using System.Buffers;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private bool CheckFullyOccluded(int maxX, int maxY, int maxZ)
        {
            if (faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                return true;
            }

            int baseWX = (int)chunkWorldPosition.X;
            int baseWY = (int)chunkWorldPosition.Y;
            int baseWZ = (int)chunkWorldPosition.Z;
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    int z0 = 0; int z1 = maxZ - 1;
                    if (flatBlocks[FlatIndex(x, y, z0)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, y, z1)] == emptyBlock) return false;
                }
            for (int z = 0; z < maxZ; z++)
                for (int y = 0; y < maxY; y++)
                {
                    int x0 = 0; int x1 = maxX - 1;
                    if (flatBlocks[FlatIndex(x0, y, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x1, y, z)] == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    int y0 = 0; int y1 = maxY - 1;
                    if (flatBlocks[FlatIndex(x, y0, z)] == emptyBlock) return false;
                    if (flatBlocks[FlatIndex(x, y1, z)] == emptyBlock) return false;
                }
            for (int y = 0; y < maxY; y++)
                for (int z = 0; z < maxZ; z++)
                {
                    if (getWorldBlock(baseWX - 1, baseWY + y, baseWZ + z) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + maxX, baseWY + y, baseWZ + z) == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int z = 0; z < maxZ; z++)
                {
                    if (getWorldBlock(baseWX + x, baseWY - 1, baseWZ + z) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + x, baseWY + maxY, baseWZ + z) == emptyBlock) return false;
                }
            for (int x = 0; x < maxX; x++)
                for (int y = 0; y < maxY; y++)
                {
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ - 1) == emptyBlock) return false;
                    if (getWorldBlock(baseWX + x, baseWY + y, baseWZ + maxZ) == emptyBlock) return false;
                }
            return true;
        }

        private void GenerateUniformFacesList()
        {
            // Determine which boundary planes are potentially visible (not mutually occluded by neighbor)
            bool emitLeft = !(faceNegX && nNegXPosX);
            bool emitRight = !(facePosX && nPosXNegX);
            bool emitBottom = !(faceNegY && nNegYPosY);
            bool emitTop = !(facePosY && nPosYNegY);
            bool emitBack = !(faceNegZ && nNegZPosZ);
            bool emitFront = !(facePosZ && nPosZNegZ);

            if (!(emitLeft || emitRight || emitBottom || emitTop || emitBack || emitFront))
            {
                chunkVertsList = new List<byte>(0);
                chunkUVsList = new List<byte>(0);
                chunkIndicesList = new List<uint>(0);
                indexFormat = IndexFormat.UInt;
                return;
            }

            // Greedy rectangle helper
            static void GreedyRectangles(Span<bool> mask, int rows, int cols, List<(int r, int c, int h, int w)> outRects)
            {
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        int idx = r * cols + c;
                        if (!mask[idx]) continue;
                        int w = 1; while (c + w < cols && mask[r * cols + c + w]) w++;
                        int h = 1; bool expand = true;
                        while (r + h < rows && expand)
                        {
                            for (int k = 0; k < w; k++) if (!mask[(r + h) * cols + (c + k)]) { expand = false; break; }
                            if (expand) h++;
                        }
                        for (int rr = 0; rr < h; rr++) for (int cc = 0; cc < w; cc++) mask[(r + rr) * cols + (c + cc)] = false;
                        outRects.Add((r, c, h, w));
                    }
                }
            }

            var merged = new List<(Faces face, byte x, byte y, byte z, byte h, byte w)>(32);

            // LEFT / RIGHT (Y x Z)
            if (emitLeft)
            {
                int rows = maxY, cols = maxZ; Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int y = 0; y < maxY; y++) for (int z = 0; z < maxZ; z++) mask[y * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X - 1, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z + z) == emptyBlock;
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full) merged.Add((Faces.LEFT, 0, 0, 0, (byte)rows, (byte)cols)); else { var rects = new List<(int r, int c, int h, int w)>(); GreedyRectangles(mask, rows, cols, rects); foreach (var r in rects) merged.Add((Faces.LEFT, 0, (byte)r.r, (byte)r.c, (byte)r.h, (byte)r.w)); }
            }
            if (emitRight)
            {
                int rows = maxY, cols = maxZ; Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int y = 0; y < maxY; y++) for (int z = 0; z < maxZ; z++) mask[y * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X + maxX, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z + z) == emptyBlock;
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full) merged.Add((Faces.RIGHT, (byte)(maxX - 1), 0, 0, (byte)rows, (byte)cols)); else { var rects = new List<(int r, int c, int h, int w)>(); GreedyRectangles(mask, rows, cols, rects); foreach (var r in rects) merged.Add((Faces.RIGHT, (byte)(maxX - 1), (byte)r.r, (byte)r.c, (byte)r.h, (byte)r.w)); }
            }
            // BOTTOM / TOP (X x Z)
            if (emitBottom)
            {
                int rows = maxX, cols = maxZ; Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++) for (int z = 0; z < maxZ; z++) mask[x * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y - 1, (int)chunkWorldPosition.Z + z) == emptyBlock;
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full) merged.Add((Faces.BOTTOM, 0, 0, 0, (byte)rows, (byte)cols)); else { var rects = new List<(int r, int c, int h, int w)>(); GreedyRectangles(mask, rows, cols, rects); foreach (var r in rects) merged.Add((Faces.BOTTOM, (byte)r.r, 0, (byte)r.c, (byte)r.h, (byte)r.w)); }
            }
            if (emitTop)
            {
                int rows = maxX, cols = maxZ; Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++) for (int z = 0; z < maxZ; z++) mask[x * maxZ + z] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y + maxY, (int)chunkWorldPosition.Z + z) == emptyBlock;
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full) merged.Add((Faces.TOP, 0, (byte)(maxY - 1), 0, (byte)rows, (byte)cols)); else { var rects = new List<(int r, int c, int h, int w)>(); GreedyRectangles(mask, rows, cols, rects); foreach (var r in rects) merged.Add((Faces.TOP, (byte)r.r, (byte)(maxY - 1), (byte)r.c, (byte)r.h, (byte)r.w)); }
            }
            // BACK / FRONT (X x Y)
            if (emitBack)
            {
                int rows = maxX, cols = maxY; Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++) for (int y = 0; y < maxY; y++) mask[x * maxY + y] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z - 1) == emptyBlock;
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full) merged.Add((Faces.BACK, 0, 0, 0, (byte)rows, (byte)cols)); else { var rects = new List<(int r, int c, int h, int w)>(); GreedyRectangles(mask, rows, cols, rects); foreach (var r in rects) merged.Add((Faces.BACK, (byte)r.r, (byte)r.c, 0, (byte)r.h, (byte)r.w)); }
            }
            if (emitFront)
            {
                int rows = maxX, cols = maxY; Span<bool> mask = stackalloc bool[256]; if (rows * cols > 256) mask = new bool[rows * cols];
                for (int x = 0; x < maxX; x++) for (int y = 0; y < maxY; y++) mask[x * maxY + y] = getWorldBlock((int)chunkWorldPosition.X + x, (int)chunkWorldPosition.Y + y, (int)chunkWorldPosition.Z + maxZ) == emptyBlock;
                bool full = true; for (int i = 0, n = rows * cols; i < n; i++) if (!mask[i]) { full = false; break; }
                if (full) merged.Add((Faces.FRONT, 0, 0, (byte)(maxZ - 1), (byte)rows, (byte)cols)); else { var rects = new List<(int r, int c, int h, int w)>(); GreedyRectangles(mask, rows, cols, rects); foreach (var r in rects) merged.Add((Faces.FRONT, (byte)r.r, (byte)r.c, (byte)(maxZ - 1), (byte)r.h, (byte)r.w)); }
            }

            int faceCount = merged.Count; int totalVerts = faceCount * 4; bool useUShortIndices = totalVerts <= 65535; indexFormat = useUShortIndices ? IndexFormat.UShort : IndexFormat.UInt;
            chunkVertsList = new List<byte>(totalVerts * 3); chunkUVsList = new List<byte>(totalVerts * 2); if (useUShortIndices) chunkIndicesUShortList = new List<ushort>(faceCount * 6); else chunkIndicesList = new List<uint>(faceCount * 6);

            int currentVertexBase = 0;
            foreach (var f in merged)
            {
                byte x = f.x, y = f.y, z = f.z, h = f.h, w = f.w; var verts = RawFaceData.rawVertexData[f.face];
                // Build stretched quad manually (same logic as pooled variant) but reuse UVs per face without scaling.
                List<ByteVector2> blockUVs = terrainTextureAtlas.GetBlockUVs(allOneBlockId, f.face);
                // Build vertex set depending on orientation
                void AddIndices()
                {
                    if (useUShortIndices)
                    {
                        chunkIndicesUShortList.Add((ushort)(currentVertexBase + 0));
                        chunkIndicesUShortList.Add((ushort)(currentVertexBase + 1));
                        chunkIndicesUShortList.Add((ushort)(currentVertexBase + 2));
                        chunkIndicesUShortList.Add((ushort)(currentVertexBase + 2));
                        chunkIndicesUShortList.Add((ushort)(currentVertexBase + 3));
                        chunkIndicesUShortList.Add((ushort)(currentVertexBase + 0));
                    }
                    else
                    {
                        chunkIndicesList.Add((uint)(currentVertexBase + 0));
                        chunkIndicesList.Add((uint)(currentVertexBase + 1));
                        chunkIndicesList.Add((uint)(currentVertexBase + 2));
                        chunkIndicesList.Add((uint)(currentVertexBase + 2));
                        chunkIndicesList.Add((uint)(currentVertexBase + 3));
                        chunkIndicesList.Add((uint)(currentVertexBase + 0));
                    }
                }
                switch (f.face)
                {
                    case Faces.LEFT:
                    case Faces.RIGHT:
                        chunkVertsList.Add(x); chunkVertsList.Add(y); chunkVertsList.Add(z);
                        chunkVertsList.Add(x); chunkVertsList.Add((byte)(y + h)); chunkVertsList.Add(z);
                        chunkVertsList.Add(x); chunkVertsList.Add((byte)(y + h)); chunkVertsList.Add((byte)(z + w));
                        chunkVertsList.Add(x); chunkVertsList.Add(y); chunkVertsList.Add((byte)(z + w));
                        break;
                    case Faces.BOTTOM:
                    case Faces.TOP:
                        chunkVertsList.Add(x); chunkVertsList.Add(y); chunkVertsList.Add(z);
                        chunkVertsList.Add((byte)(x + h)); chunkVertsList.Add(y); chunkVertsList.Add(z);
                        chunkVertsList.Add((byte)(x + h)); chunkVertsList.Add(y); chunkVertsList.Add((byte)(z + w));
                        chunkVertsList.Add(x); chunkVertsList.Add(y); chunkVertsList.Add((byte)(z + w));
                        break;
                    case Faces.BACK:
                    case Faces.FRONT:
                        chunkVertsList.Add(x); chunkVertsList.Add(y); chunkVertsList.Add(z);
                        chunkVertsList.Add((byte)(x + h)); chunkVertsList.Add(y); chunkVertsList.Add(z);
                        chunkVertsList.Add((byte)(x + h)); chunkVertsList.Add((byte)(y + w)); chunkVertsList.Add(z);
                        chunkVertsList.Add(x); chunkVertsList.Add((byte)(y + w)); chunkVertsList.Add(z);
                        break;
                }
                foreach (var uv in blockUVs)
                {
                    chunkUVsList.Add(uv.x); chunkUVsList.Add(uv.y);
                }
                AddIndices(); currentVertexBase += 4;
            }
        }
    }
}

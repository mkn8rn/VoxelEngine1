using MVGE_GFX.Models;
using MVGE_INF.Managers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.Terrain
{
    public partial class ChunkRender
    {
        private static byte[] InitPopCount()
        {
            var arr = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                int v = i; int c = 0; while (v != 0) { v &= v - 1; c++; }
                arr[i] = (byte)c;
            }
            return arr;
        }

        private void DeleteGL()
        {
            if (!isBuilt) return;
            chunkVAO.Delete();
            chunkVertexVBO.Delete();
            chunkUVVBO.Delete();
            chunkIBO.Delete();
            isBuilt = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FlatIndex(int x, int y, int z) => (x * maxZ + z) * maxY + y;

        private void GenerateFaces()
        {
            // if enclosed (all boundary voxels solid and neighbors sealing) skip mesh generation entirely
            if (CheckFullyOccluded(maxX, maxY, maxZ))
            {
                fullyOccluded = true;
                ReturnFlat();
                return;
            }

            bool usePooling = false;
            int nonEmpty = 0; // total solid voxels
            long faceEstimate = 0; // approximate visible face count (only valid if we finish full pass without triggering classic threshold)

            if (FlagManager.flags.useFacePooling.GetValueOrDefault())
            {
                int threshold = FlagManager.flags.faceAmountToPool.GetValueOrDefault(int.MaxValue);
                int strideX = maxZ * maxY; // distance between x slices
                int strideZ = maxY;         // distance between z rows

                // Single unified pass: counts solids & (unless early threshold trigger) accumulates face estimate.
                if (threshold >= 0)
                {
                    for (int x = 0; x < maxX && !usePooling; x++)
                    {
                        int slabBase = x * strideX;
                        for (int z = 0; z < maxZ && !usePooling; z++)
                        {
                            int rowBase = slabBase + z * strideZ;
                            for (int y = 0; y < maxY; y++)
                            {
                                int li = rowBase + y;
                                if (flatBlocks[li] == emptyBlock) continue;
                                nonEmpty++;
                                // Classic threshold check – if reached we select pooled path & abandon face estimate refinement.
                                if (nonEmpty >= threshold)
                                {
                                    usePooling = true;
                                    break;
                                }
                                // Exposure estimate: add 6, subtract 2 per previously visited solid neighbor (x-1, y-1, z-1)
                                faceEstimate += 6;
                                if (x > 0 && flatBlocks[li - strideX] != emptyBlock) faceEstimate -= 2; // -X shared pair
                                if (z > 0 && flatBlocks[li - strideZ] != emptyBlock) faceEstimate -= 2; // -Z shared pair
                                if (y > 0 && flatBlocks[li - 1] != emptyBlock) faceEstimate -= 2;       // -Y shared pair
                            }
                        }
                    }
                }
                else
                {
                    // threshold disabled (negative) -> still compute density/exposure in one pass
                    for (int x = 0; x < maxX; x++)
                    {
                        int slabBase = x * strideX;
                        for (int z = 0; z < maxZ; z++)
                        {
                            int rowBase = slabBase + z * strideZ;
                            for (int y = 0; y < maxY; y++)
                            {
                                int li = rowBase + y;
                                if (flatBlocks[li] == emptyBlock) continue;
                                nonEmpty++;
                                faceEstimate += 6;
                                if (x > 0 && flatBlocks[li - strideX] != emptyBlock) faceEstimate -= 2;
                                if (z > 0 && flatBlocks[li - strideZ] != emptyBlock) faceEstimate -= 2;
                                if (y > 0 && flatBlocks[li - 1] != emptyBlock) faceEstimate -= 2;
                            }
                        }
                    }
                }

                // Exposure-based heuristic only if we did NOT already select pooling via threshold and we have at least one solid.
                if (!usePooling && nonEmpty > 0)
                {
                    int volume = maxX * maxY * maxZ;
                    float density = nonEmpty / (float)volume;
                    // faceEstimate counts only if threshold not hit; if threshold hit pooling already true.
                    float exposureRatio = (float)faceEstimate / (nonEmpty * 6f);

                    const float MinimumDensityThreshold = 0.15f; // low density
                    const float MaxExposureThreshold = 0.55f;    // high exposure

                    if (density < MinimumDensityThreshold && exposureRatio > MaxExposureThreshold)
                    {
                        usePooling = true;
                    }
                }
            }

            if (usePooling)
            {
                var builder = new PooledFacesRender(
                    chunkWorldPosition, maxX, maxY, maxZ, emptyBlock,
                    getWorldBlock, null, null, terrainTextureAtlas, flatBlocks,
                    faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ,
                    nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ,
                    allOneBlock, allOneBlockId);
                var res = builder.Build();
                usedPooling = true;
                useUShort = res.UseUShort;
                vertBuffer = res.VertBuffer; uvBuffer = res.UVBuffer;
                indicesUIntBuffer = res.IndicesUIntBuffer; indicesUShortBuffer = res.IndicesUShortBuffer;
                vertBytesUsed = res.VertBytesUsed; uvBytesUsed = res.UVBytesUsed; indicesUsed = res.IndicesUsed;
                indexFormat = useUShort ? IndexFormat.UShort : IndexFormat.UInt;
                ReturnFlat();
            }
            else
            {
                // If we are in list mode but the chunk is uniform, we can still exploit a simpler path by building faces directly.
                if (allOneBlock)
                {
                    GenerateUniformFacesList();
                    ReturnFlat();
                    return;
                }
                GenerateFacesListFlatMaskedTwoPass_BB();
                ReturnFlat();
            }
        }

        private void ReturnFlat()
        {
            if (flatBlocks != null) ArrayPool<ushort>.Shared.Return(flatBlocks, false);
        }

        public static void ProcessPendingDeletes()
        {
            while (pendingDeletion.TryDequeue(out var cr)) cr.DeleteGL();
        }

        public void ScheduleDelete()
        {
            if (!isBuilt) return;
            pendingDeletion.Enqueue(this);
        }

        private void IntegrateFaceListEmit(ushort block, Faces face, ByteVector3 bp, ref int currentVertexBase, bool useUShortIndices)
        {
            var verts = RawFaceData.rawVertexData[face];
            // Append vertex positions
            foreach (var v in verts)
            {
                chunkVertsList.Add((byte)(v.x + bp.x));
                chunkVertsList.Add((byte)(v.y + bp.y));
                chunkVertsList.Add((byte)(v.z + bp.z));
            }

            // UVs
            var blockUVs = block != emptyBlock ? terrainTextureAtlas.GetBlockUVs(block, face) : EmptyUVList;
            foreach (var uv in blockUVs)
            {
                chunkUVsList.Add(uv.x);
                chunkUVsList.Add(uv.y);
            }
            // Indices (two triangles)
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
            currentVertexBase += 4;
        }
    }
}

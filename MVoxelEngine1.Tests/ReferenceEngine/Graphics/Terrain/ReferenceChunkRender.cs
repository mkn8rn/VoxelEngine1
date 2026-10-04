using System.Diagnostics;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Graphics.Terrain.Sections;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using OpenTK.Mathematics;

namespace MVoxelEngine1.Graphics.Terrain;

// CPU-only reference builder. Compiled only into the test executable.
internal sealed class ReferenceChunkRender : IDisposable
{
    private static long nextRenderDataId;
    private readonly Vector3 chunkWorldPosition;
    private readonly ChunkData chunkMeta;
    private readonly int maxX, maxY, maxZ;
    private readonly bool faceNegX, facePosX, faceNegY, facePosY, faceNegZ, facePosZ;
    private readonly bool nNegXPosX, nPosXNegX, nNegYPosY, nPosYNegY, nNegZPosZ, nPosZNegZ;
    private readonly bool allOneBlock;
    private readonly ushort allOneBlockId;
    private readonly int prepassSolidCount, prepassExposureEstimate;
    private int opaqueFaceCount, opaqueRectangleCount, transparentFaceCount, transparentRectangleCount;
    private bool fullyOccluded;
    private ChunkRenderUploadData? uploadData;
    public static BlockTextureAtlas terrainTextureAtlas
    {
        get => ChunkRender.terrainTextureAtlas;
        set => ChunkRender.terrainTextureAtlas = value;
    }
    public ChunkRenderUploadData UploadData => uploadData ?? throw new ObjectDisposedException(nameof(ReferenceChunkRender));
    public static bool IsOpenGlUploaded => false;
        public ReferenceChunkRender(
            ChunkPrerenderData prerenderData,
            FaceGenerationMode faceGenerationMode,
            Func<int, int, int, ushort>? getLocalBlock,
            ReferenceNeighborBlockPlanes? referenceNeighbors,
            PackedFaceNativePool packedFacePool)
        {
            ArgumentNullException.ThrowIfNull(packedFacePool);
            this.prepassSolidCount = prerenderData.PrepassSolidCount;
            this.prepassExposureEstimate = prerenderData.PrepassExposureEstimate;
            this.chunkMeta = prerenderData.chunkData;
            this.maxX = prerenderData.maxX; this.maxY = prerenderData.maxY; this.maxZ = prerenderData.maxZ;
            chunkWorldPosition = new Vector3(prerenderData.chunkData.x, prerenderData.chunkData.y, prerenderData.chunkData.z);
            faceNegX = prerenderData.FaceNegX; facePosX = prerenderData.FacePosX; faceNegY = prerenderData.FaceNegY; facePosY = prerenderData.FacePosY; faceNegZ = prerenderData.FaceNegZ; facePosZ = prerenderData.FacePosZ;
            nNegXPosX = prerenderData.NeighborNegXPosX; nPosXNegX = prerenderData.NeighborPosXNegX; nNegYPosY = prerenderData.NeighborNegYPosY; nPosYNegY = prerenderData.NeighborPosYNegY; nNegZPosZ = prerenderData.NeighborNegZPosZ; nPosZNegZ = prerenderData.NeighborPosZNegZ;
            allOneBlock = prerenderData.AllOneBlock; allOneBlockId = prerenderData.AllOneBlockId;
            FaceRectangleMeshData? meshData = null;
            try
            {
                meshData = GenerateFaces(
                    prerenderData,
                    faceGenerationMode,
                    getLocalBlock,
                    referenceNeighbors,
                    packedFacePool);
                SetMeshCounts(meshData);
                uploadData = new ChunkRenderUploadData(
                    Interlocked.Increment(ref nextRenderDataId),
                    chunkWorldPosition.X,
                    chunkWorldPosition.Y,
                    chunkWorldPosition.Z,
                    fullyOccluded,
                    faceGenerationMode,
                    meshData);
                meshData = null;
            }
            finally
            {
                meshData?.Dispose();
            }
        }

        private FaceRectangleMeshData GenerateFaces(
            ChunkPrerenderData prerenderData,
            FaceGenerationMode faceGenerationMode,
            Func<int, int, int, ushort>? getLocalBlock,
            ReferenceNeighborBlockPlanes? referenceNeighbors,
            PackedFaceNativePool packedFacePool)
        {
            if (faceGenerationMode == FaceGenerationMode.Reference)
            {
                if (getLocalBlock is null)
                    throw new ArgumentNullException(nameof(getLocalBlock));
                if (referenceNeighbors is null)
                    throw new ArgumentNullException(nameof(referenceNeighbors));

                return GenerateReferenceFaces(
                    prerenderData,
                    getLocalBlock,
                    referenceNeighbors);
            }

            if (faceGenerationMode != FaceGenerationMode.Optimized)
                throw new ArgumentOutOfRangeException(nameof(faceGenerationMode));

            if (prepassSolidCount > 0 && faceNegX && facePosX && faceNegY && facePosY && faceNegZ && facePosZ &&
                nNegXPosX && nPosXNegX && nNegYPosY && nPosYNegY && nNegZPosZ && nPosZNegZ)
            {
                fullyOccluded = true;
                return new FaceRectangleMeshData(
                    0,
                    Array.Empty<uint>(),
                    0,
                    Array.Empty<uint>());
            }

            bool recordPerformance = StartupPerformanceRecorder.IsRunning;
            bool usesGeneratedSpans = prerenderData.GeneratedSpans is not null;
            long buildStart = recordPerformance ? Stopwatch.GetTimestamp() : 0;
            var sectionRender = new SectionRender(
                prerenderData,
                terrainTextureAtlas,
                packedFacePool);
            FaceRectangleMeshData meshData = sectionRender.Build();
            if (recordPerformance)
            {
                MeshPerformanceRecorder.RecordBuiltChunk(
                    usesGeneratedSpans,
                    MeshPerformanceRecorder.GetElapsedTicks(buildStart));
            }
            return meshData;
        }

        private void SetMeshCounts(FaceRectangleMeshData mesh)
        {
            opaqueFaceCount = mesh.OpaqueFaceCount;
            opaqueRectangleCount = mesh.OpaqueRectangleCount;
            transparentFaceCount = mesh.TransparentFaceCount;
            transparentRectangleCount = mesh.TransparentRectangleCount;
            fullyOccluded = opaqueFaceCount == 0 && transparentFaceCount == 0;
        }

        private FaceRectangleMeshData GenerateReferenceFaces(
            ChunkPrerenderData prerenderData,
            Func<int, int, int, ushort> getLocalBlock,
            ReferenceNeighborBlockPlanes referenceNeighbors)
        {
            ReferenceFaceGenerationResult faces = prerenderData.GeneratedSpans is not null
                ? ReferenceFaceGenerator.Generate(
                    maxX,
                    maxY,
                    maxZ,
                    prerenderData.GeneratedSpans.GetBlockLocal,
                    referenceNeighbors,
                    TerrainLoader.IsOpaque)
                : allOneBlock && allOneBlockId != 0
                ? ReferenceFaceGenerator.GenerateUniform(
                    maxX,
                    maxY,
                    maxZ,
                    allOneBlockId,
                    referenceNeighbors,
                    TerrainLoader.IsOpaque)
                : ReferenceFaceGenerator.GenerateSections(
                    maxX,
                    maxY,
                    maxZ,
                    getLocalBlock,
                    referenceNeighbors,
                    TerrainLoader.IsOpaque,
                    prerenderData.RequireSectionDescriptions());

            uint[] opaqueTileIndices = BuildReferenceTileIndices(
                faces.OpaqueBlockIds,
                faces.OpaqueDirections);
            uint[] transparentTileIndices = BuildReferenceTileIndices(
                faces.TransparentBlockIds,
                faces.TransparentDirections);
            return FaceRectangleMeshData.FromFaces(
                faces.OpaqueFaceCount,
                faces.OpaqueOffsets,
                opaqueTileIndices,
                faces.OpaqueDirections,
                faces.TransparentFaceCount,
                faces.TransparentOffsets,
                transparentTileIndices,
                faces.TransparentDirections);
        }

        private static uint[] BuildReferenceTileIndices(
            ReadOnlySpan<ushort> blockIds,
            ReadOnlySpan<byte> directions)
        {
            if (blockIds.Length != directions.Length)
                throw new InvalidOperationException("Reference face arrays have different lengths.");

            var result = new uint[blockIds.Length];
            var cache = new Dictionary<int, uint>();
            for (int index = 0; index < result.Length; index++)
            {
                int key = (blockIds[index] << 3) | directions[index];
                if (!cache.TryGetValue(key, out uint tileIndex))
                {
                    tileIndex = SectionRender.ComputeTileIndex(
                        terrainTextureAtlas,
                        blockIds[index],
                        (Faces)directions[index]);
                    cache.Add(key, tileIndex);
                }

                result[index] = tileIndex;
            }

            return result;
        }


    public void ScheduleDelete() => Interlocked.Exchange(ref uploadData, null)?.Dispose();
    public void Dispose() => ScheduleDelete();
}

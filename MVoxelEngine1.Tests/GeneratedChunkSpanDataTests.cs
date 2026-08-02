using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.WorldGeneration.Terrain;
using OpenTK.Mathematics;

namespace MVoxelEngine1.Tests
{
    public class GeneratedChunkSpanDataTests
    {
        [Fact]
        public void EmptySentinelsRemainAirAtNegativeWorldHeight()
        {
            var source = new GeneratedChunkSpanData(
                new[]
                {
                    new BlockColumnProfile
                    {
                        StoneStart = -1,
                        StoneEnd = -1,
                        SoilStart = -1,
                        SoilEnd = -1,
                        WaterStart = -1,
                        WaterEnd = -1
                    }
                },
                width: 1,
                height: 4,
                depth: 1,
                chunkBaseY: -2,
                stoneBlockId: 1,
                soilBlockId: 2,
                waterBlockId: 3);

            Assert.Equal((ushort)0, source.GetBlockLocal(0, 1, 0));
        }

        [Fact]
        public void OptimizedRendererMatchesReferenceWithRuntimeBlockTypesAndTextures()
        {
            BlockTextureAtlas atlas = LoadDefaultRuntimeData();
            const ushort opaqueRuntimeBlock = (ushort)BaseBlockType.Stone;
            const ushort firstTransparentRuntimeBlock = 256;
            const ushort secondTransparentRuntimeBlock = 257;
            var source = new GeneratedChunkSpanData(
                new[]
                {
                    new BlockColumnProfile
                    {
                        StoneStart = 0,
                        StoneEnd = 0,
                        SoilStart = 1,
                        SoilEnd = 1,
                        WaterStart = 2,
                        WaterEnd = 2
                    }
                },
                width: 1,
                height: 4,
                depth: 1,
                chunkBaseY: 0,
                stoneBlockId: opaqueRuntimeBlock,
                soilBlockId: firstTransparentRuntimeBlock,
                waterBlockId: secondTransparentRuntimeBlock);

            ChunkPrerenderData data = CreatePrerenderData(source);
            ChunkRender.terrainTextureAtlas = atlas;
            var optimized = new ChunkRender(
                data,
                FaceGenerationMode.Optimized,
                null,
                null);
            var reference = new ChunkRender(
                data,
                FaceGenerationMode.Reference,
                source.GetBlockLocal,
                new ReferenceNeighborBlockPlanes());

            Assert.Equal(6, optimized.UploadData.OpaqueFaceCount);
            Assert.Equal(11, optimized.UploadData.TransparentFaceCount);
            Assert.Equal(GetFaceRecords(reference.UploadData), GetFaceRecords(optimized.UploadData));
            AssertRuntimeTiles(
                optimized.UploadData,
                atlas,
                opaqueRuntimeBlock,
                firstTransparentRuntimeBlock,
                secondTransparentRuntimeBlock);
        }

        [Fact]
        public void MaterializationPreservesBlocksAndReferenceFaces()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                maximumWorldHeight: 16,
                lod1RenderDistance: 0,
                chunkSizeY: 16,
                chunkSizeX: 16,
                chunkSizeZ: 16);
            LoadDefaultTerrainData(workspace.GameDataRoot);
            BiomeManager.LoadAllBiomes();
            Chunk chunk = CreateGeneratedChunk(workspace, CreateColumns());
            ushort[] beforeBlocks = ReadAllBlocks(chunk, 16);
            ReferenceFaceGenerationResult beforeFaces = chunk.GenerateReferenceFaces(
                new ReferenceNeighborBlockPlanes());

            Assert.All(
                chunk.sections.Cast<Section?>(),
                section => Assert.Null(section));
            chunk.MaterializeGeneratedSpansForStorage();

            Assert.Equal(beforeBlocks, ReadAllBlocks(chunk, 16));
            Assert.Contains(chunk.sections.Cast<Section?>(), section => section is not null);
            ReferenceFaceGenerationResult afterFaces = chunk.GenerateReferenceFaces(
                new ReferenceNeighborBlockPlanes());
            Assert.Equal(beforeFaces.OpaqueOffsets, afterFaces.OpaqueOffsets);
            Assert.Equal(beforeFaces.OpaqueBlockIds, afterFaces.OpaqueBlockIds);
            Assert.Equal(beforeFaces.OpaqueDirections, afterFaces.OpaqueDirections);
            Assert.Equal(beforeFaces.TransparentOffsets, afterFaces.TransparentOffsets);
            Assert.Equal(beforeFaces.TransparentBlockIds, afterFaces.TransparentBlockIds);
            Assert.Equal(beforeFaces.TransparentDirections, afterFaces.TransparentDirections);
        }

        [Fact]
        public void FirstEditMaterializesOnceAndChangesOnlyRequestedVoxel()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                maximumWorldHeight: 16,
                lod1RenderDistance: 0,
                chunkSizeY: 16,
                chunkSizeX: 16,
                chunkSizeZ: 16);
            LoadDefaultTerrainData(workspace.GameDataRoot);
            BiomeManager.LoadAllBiomes();
            Chunk chunk = CreateGeneratedChunk(workspace, CreateColumns());
            ushort[] expected = ReadAllBlocks(chunk, 16);
            const int editX = 2;
            const int editY = 6;
            const int editZ = 3;
            const ushort runtimeBlockId = 257;
            int editIndex = ((editX * 16) + editY) * 16 + editZ;
            expected[editIndex] = runtimeBlockId;

            chunk.SetBlockLocal(editX, editY, editZ, runtimeBlockId);

            Assert.Equal(expected, ReadAllBlocks(chunk, 16));
            Assert.Equal(runtimeBlockId, chunk.GetBlockLocal(editX, editY, editZ));
        }

        private static BlockTextureAtlas LoadDefaultRuntimeData(
            string? gameDataRoot = null)
        {
            LoadDefaultTerrainData(gameDataRoot ?? TestPaths.GameDataRoot);
            return new BlockTextureAtlas(BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        }

        private static void LoadDefaultTerrainData(string gameDataRoot)
        {
            GameManager.Initialize(gameDataRoot);
            string game = GameManager.SelectGameFolder("Default");
            GameManager.LoadGameDefaultSettings(game);
            TerrainLoader.allBlockTypes = new List<string>();
            TerrainLoader.allBlockTypesByBaseType = new Dictionary<string, BaseBlockType>();
            TerrainLoader.allBlockTypesByIds = new Dictionary<ushort, string>();
            TerrainLoader.allBlockTypeObjects = new List<BlockType>();
            _ = new TerrainLoader();
        }

        private static Chunk CreateGeneratedChunk(
            TestWorkspace workspace,
            BlockColumnProfile[] columns)
        {
            string chunks = Path.Combine(workspace.Root, "Chunks");
            Directory.CreateDirectory(chunks);
            return new Chunk(
                Vector3.Zero,
                123456,
                chunks,
                autoGenerate: true,
                Chunk.UniformOverride.None,
                columns);
        }

        private static BlockColumnProfile[] CreateColumns()
        {
            var columns = new BlockColumnProfile[16 * 16];
            for (int index = 0; index < columns.Length; index++)
            {
                columns[index] = new BlockColumnProfile
                {
                    Surface = 7,
                    StoneStart = 0,
                    StoneEnd = 4,
                    SoilStart = 5,
                    SoilEnd = 7,
                    WaterStart = 8,
                    WaterEnd = 9
                };
            }

            return columns;
        }

        private static ushort[] ReadAllBlocks(Chunk chunk, int size)
        {
            var result = new ushort[size * size * size];
            int index = 0;
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int z = 0; z < size; z++)
                        result[index++] = chunk.GetBlockLocal(x, y, z);
                }
            }

            return result;
        }

        private static ChunkPrerenderData CreatePrerenderData(
            GeneratedChunkSpanData source)
        {
            int yzWords = (source.Height * source.Depth + 63) / 64;
            int xzWords = (source.Width * source.Depth + 63) / 64;
            int xyWords = (source.Width * source.Height + 63) / 64;
            return new ChunkPrerenderData
            {
                GeneratedSpans = source,
                SectionDescs = Array.Empty<SectionPrerenderDesc>(),
                sectionsX = 1,
                sectionsY = 1,
                sectionsZ = 1,
                sectionSize = 16,
                maxX = source.Width,
                maxY = source.Height,
                maxZ = source.Depth,
                chunkData = new ChunkData(),
                NeighborPlaneNegX = new ulong[yzWords],
                NeighborPlanePosX = new ulong[yzWords],
                NeighborPlaneNegY = new ulong[xzWords],
                NeighborPlanePosY = new ulong[xzWords],
                NeighborPlaneNegZ = new ulong[xyWords],
                NeighborPlanePosZ = new ulong[xyWords],
                NeighborTransparentPlaneNegX = new ushort[source.Height * source.Depth],
                NeighborTransparentPlanePosX = new ushort[source.Height * source.Depth],
                NeighborTransparentPlaneNegY = new ushort[source.Width * source.Depth],
                NeighborTransparentPlanePosY = new ushort[source.Width * source.Depth],
                NeighborTransparentPlaneNegZ = new ushort[source.Width * source.Height],
                NeighborTransparentPlanePosZ = new ushort[source.Width * source.Height]
            };
        }

        private static string[] GetFaceRecords(ChunkRenderUploadData data)
        {
            var result = new List<string>(
                data.OpaqueFaceCount + data.TransparentFaceCount);
            AddFaceRecords(
                result,
                "opaque",
                data.OpaqueOffsets.ToArray(),
                data.OpaqueTileIndices.ToArray(),
                data.OpaqueFaceDirections.ToArray());
            AddFaceRecords(
                result,
                "transparent",
                data.TransparentOffsets.ToArray(),
                data.TransparentTileIndices.ToArray(),
                data.TransparentFaceDirections.ToArray());
            result.Sort(StringComparer.Ordinal);
            return result.ToArray();
        }

        private static void AddFaceRecords(
            List<string> destination,
            string renderPass,
            byte[] offsets,
            uint[] tileIndices,
            byte[] directions)
        {
            for (int index = 0; index < directions.Length; index++)
            {
                int offset = index * 3;
                destination.Add(
                    $"{renderPass}:{offsets[offset]}:{offsets[offset + 1]}:" +
                    $"{offsets[offset + 2]}:{directions[index]}:{tileIndices[index]}");
            }
        }

        private static void AssertRuntimeTiles(
            ChunkRenderUploadData data,
            BlockTextureAtlas atlas,
            ushort opaqueBlock,
            ushort firstTransparentBlock,
            ushort secondTransparentBlock)
        {
            AssertPassTiles(
                data.OpaqueOffsets.ToArray(),
                data.OpaqueTileIndices.ToArray(),
                data.OpaqueFaceDirections.ToArray(),
                atlas,
                _ => opaqueBlock);
            AssertPassTiles(
                data.TransparentOffsets.ToArray(),
                data.TransparentTileIndices.ToArray(),
                data.TransparentFaceDirections.ToArray(),
                atlas,
                y => y == 1 ? firstTransparentBlock : secondTransparentBlock);
        }

        private static void AssertPassTiles(
            byte[] offsets,
            uint[] tileIndices,
            byte[] directions,
            BlockTextureAtlas atlas,
            Func<byte, ushort> blockAtY)
        {
            for (int index = 0; index < directions.Length; index++)
            {
                ushort blockId = blockAtY(offsets[index * 3 + 1]);
                Faces face = (Faces)directions[index];
                ByteVector2 coordinate = atlas.GetBlockUVs(blockId, face)[2];
                uint expected = (uint)(coordinate.y * atlas.tilesX + coordinate.x);
                Assert.Equal(expected, tileIndices[index]);
            }
        }
    }
}

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
using System.Runtime.CompilerServices;

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
        public void FlatGeneratedSurfaceUsesExactRectangles()
        {
            BlockTextureAtlas atlas = LoadDefaultRuntimeData();
            var source = new GeneratedChunkSpanData(
                CreateColumns(),
                width: 16,
                height: 16,
                depth: 16,
                chunkBaseY: 0,
                stoneBlockId: (ushort)BaseBlockType.Stone,
                soilBlockId: (ushort)BaseBlockType.Soil,
                waterBlockId: (ushort)BaseBlockType.Water);
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

            Assert.Equal(
                GetFaceRecords(reference.UploadData),
                GetFaceRecords(optimized.UploadData));
            Assert.True(
                optimized.UploadData.OpaqueRectangleCount <
                optimized.UploadData.OpaqueFaceCount);
            Assert.True(
                optimized.UploadData.TransparentRectangleCount <
                optimized.UploadData.TransparentFaceCount);
        }

        [Fact]
        public void RendererReleasesBorrowedNeighborPlanesAfterFaceBuild()
        {
            BlockTextureAtlas atlas = LoadDefaultRuntimeData();
            var source = new GeneratedChunkSpanData(
                CreateColumns(),
                width: 16,
                height: 16,
                depth: 16,
                chunkBaseY: 0,
                stoneBlockId: (ushort)BaseBlockType.Stone,
                soilBlockId: (ushort)BaseBlockType.Soil,
                waterBlockId: (ushort)BaseBlockType.Water);
            (ChunkRender renderer, WeakReference<ulong[]> neighbor) =
                CreateRendererWithBorrowedNeighbor(source, atlas);

            for (int collection = 0; collection < 3; collection++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(neighbor.TryGetTarget(out _));
            GC.KeepAlive(renderer);
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
            Assert.False(chunk.HasMaterializedSectionGrid);
            ushort[] beforeBlocks = ReadAllBlocks(chunk, 16);
            ReferenceFaceGenerationResult beforeFaces = chunk.GenerateReferenceFaces(
                new ReferenceNeighborBlockPlanes());

            Assert.False(chunk.HasMaterializedSectionGrid);
            chunk.MaterializeGeneratedSpansForStorage();

            Assert.True(chunk.HasMaterializedSectionGrid);
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

            Assert.False(chunk.HasMaterializedSectionGrid);
            chunk.SetBlockLocal(editX, editY, editZ, runtimeBlockId);

            Assert.True(chunk.HasMaterializedSectionGrid);
            Assert.Equal(expected, ReadAllBlocks(chunk, 16));
            Assert.Equal(runtimeBlockId, chunk.GetBlockLocal(editX, editY, editZ));
        }

        [Fact]
        public void AllAirReadsAndAirWritesKeepSectionGridDeferred()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            ConfigureChunkTests(workspace);
            Chunk chunk = CreateUniformChunk(workspace, Chunk.UniformOverride.AllAir);

            Assert.True(chunk.AllAirChunk);
            Assert.False(chunk.HasMaterializedSectionGrid);
            Assert.Equal((ushort)BaseBlockType.Empty, chunk.GetBlockLocal(4, 5, 6));

            chunk.SetBlockLocal(4, 5, 6, (ushort)BaseBlockType.Empty);

            Assert.True(chunk.AllAirChunk);
            Assert.False(chunk.HasMaterializedSectionGrid);
        }

        [Fact]
        public void FirstAllAirNonAirEditMaterializesGridAndClearsUniformFlags()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            ConfigureChunkTests(workspace);
            Chunk chunk = CreateUniformChunk(workspace, Chunk.UniformOverride.AllAir);
            const ushort blockId = (ushort)BaseBlockType.Stone;

            chunk.SetBlockLocal(4, 5, 6, blockId);

            Assert.True(chunk.HasMaterializedSectionGrid);
            Assert.False(chunk.AllAirChunk);
            Assert.False(chunk.AllStoneChunk);
            Assert.False(chunk.AllSoilChunk);
            Assert.False(chunk.AllWaterChunk);
            Assert.False(chunk.AllOneBlockChunk);
            Assert.Equal(blockId, chunk.GetBlockLocal(4, 5, 6));
            Assert.Equal((ushort)BaseBlockType.Empty, chunk.GetBlockLocal(4, 5, 7));
        }

        [Fact]
        public void UniformAndLoadedChunksMaterializeRequiredSectionGrids()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            ConfigureChunkTests(workspace);

            Chunk stone = CreateUniformChunk(workspace, Chunk.UniformOverride.AllStone);
            Chunk water = CreateUniformChunk(workspace, Chunk.UniformOverride.AllWater);
            Chunk loaded = new(
                Vector3.Zero,
                123456,
                GetChunkDirectory(workspace),
                autoGenerate: false);

            Assert.True(stone.HasMaterializedSectionGrid);
            Assert.True(water.HasMaterializedSectionGrid);
            Assert.True(loaded.HasMaterializedSectionGrid);
            Assert.True(stone.UsesSharedUniformSection);
            Assert.True(water.UsesSharedUniformSection);
            Assert.False(loaded.UsesSharedUniformSection);
            Assert.Equal((ushort)BaseBlockType.Stone, stone.GetBlockLocal(4, 5, 6));
            Assert.Equal((ushort)BaseBlockType.Water, water.GetBlockLocal(4, 5, 6));
            Assert.Equal((ushort)BaseBlockType.Empty, loaded.GetBlockLocal(4, 5, 6));
        }

        [Theory]
        [InlineData(6)]
        [InlineData(11)]
        public void UniformSectionEditsUseIndependentCopies(ushort uniformBlockId)
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            ConfigureChunkTests(workspace, chunkSize: 32);
            Chunk.UniformOverride uniformOverride = uniformBlockId ==
                (ushort)BaseBlockType.Stone
                ? Chunk.UniformOverride.AllStone
                : Chunk.UniformOverride.AllWater;
            Chunk chunk = CreateUniformChunk(workspace, uniformOverride);

            Assert.True(chunk.UsesSharedUniformSection);
            chunk.SetBlockLocal(1, 1, 1, (ushort)BaseBlockType.Empty);

            Assert.Equal(
                (ushort)BaseBlockType.Empty,
                chunk.GetBlockLocal(1, 1, 1));
            Assert.Equal(uniformBlockId, chunk.GetBlockLocal(2, 1, 1));
            Assert.Equal(uniformBlockId, chunk.GetBlockLocal(17, 17, 17));
            Assert.True(chunk.UsesSharedUniformSection);

            chunk.SetBlockLocal(17, 17, 17, (ushort)BaseBlockType.Soil);

            Assert.Equal(
                (ushort)BaseBlockType.Empty,
                chunk.GetBlockLocal(1, 1, 1));
            Assert.Equal(
                (ushort)BaseBlockType.Soil,
                chunk.GetBlockLocal(17, 17, 17));
            Assert.Equal(uniformBlockId, chunk.GetBlockLocal(18, 17, 17));

            Section[,,] independentGrid = chunk.sections;
            Assert.False(chunk.UsesSharedUniformSection);
            Assert.NotSame(
                independentGrid[0, 0, 0],
                independentGrid[1, 1, 1]);
            Assert.NotSame(
                independentGrid[0, 1, 0],
                independentGrid[1, 0, 1]);
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
            return new Chunk(
                Vector3.Zero,
                123456,
                GetChunkDirectory(workspace),
                autoGenerate: true,
                Chunk.UniformOverride.None,
                columns);
        }

        private static Chunk CreateUniformChunk(
            TestWorkspace workspace,
            Chunk.UniformOverride uniformOverride)
        {
            return new Chunk(
                Vector3.Zero,
                123456,
                GetChunkDirectory(workspace),
                autoGenerate: true,
                uniformOverride,
                Array.Empty<BlockColumnProfile>());
        }

        private static string GetChunkDirectory(TestWorkspace workspace)
        {
            string chunks = Path.Combine(workspace.Root, "Chunks");
            Directory.CreateDirectory(chunks);
            return chunks;
        }

        private static void ConfigureChunkTests(
            TestWorkspace workspace,
            int chunkSize = 16)
        {
            SimulatedGpuUploadTestSupport.ConfigureSmallWorld(
                workspace.GameDataRoot,
                maximumWorldHeight: chunkSize,
                lod1RenderDistance: 0,
                chunkSizeY: chunkSize,
                chunkSizeX: chunkSize,
                chunkSizeZ: chunkSize);
            LoadDefaultTerrainData(workspace.GameDataRoot);
            BiomeManager.LoadAllBiomes();
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (
            ChunkRender Renderer,
            WeakReference<ulong[]> Neighbor)
            CreateRendererWithBorrowedNeighbor(
                GeneratedChunkSpanData source,
                BlockTextureAtlas atlas)
        {
            ChunkPrerenderData data = CreatePrerenderData(source);
            ulong[] neighbor = data.NeighborPlaneNegX;
            var weakNeighbor = new WeakReference<ulong[]>(neighbor);
            ChunkRender.terrainTextureAtlas = atlas;
            var renderer = new ChunkRender(
                data,
                FaceGenerationMode.Optimized,
                null,
                null);
            return (renderer, weakNeighbor);
        }

        private static string[] GetFaceRecords(ChunkRenderUploadData data)
        {
            var result = new List<string>(
                data.OpaqueFaceCount + data.TransparentFaceCount);
            AddFaceRecords(
                result,
                "opaque",
                data.OpaqueRectangles.Span);
            AddFaceRecords(
                result,
                "transparent",
                data.TransparentRectangles.Span);
            result.Sort(StringComparer.Ordinal);
            return result.ToArray();
        }

        private static void AddFaceRecords(
            List<string> destination,
            string renderPass,
            ReadOnlySpan<uint> rectangles)
        {
            var reader = new PackedFaceRectangleReader(rectangles);
            while (reader.MoveNext())
            {
                destination.Add(
                    $"{renderPass}:{reader.X}:{reader.Y}:{reader.Z}:" +
                    $"{reader.Direction}:{reader.TileIndex}");
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
                data.OpaqueRectangles.Span,
                atlas,
                _ => opaqueBlock);
            AssertPassTiles(
                data.TransparentRectangles.Span,
                atlas,
                y => y == 1 ? firstTransparentBlock : secondTransparentBlock);
        }

        private static void AssertPassTiles(
            ReadOnlySpan<uint> rectangles,
            BlockTextureAtlas atlas,
            Func<byte, ushort> blockAtY)
        {
            var reader = new PackedFaceRectangleReader(rectangles);
            while (reader.MoveNext())
            {
                ushort blockId = blockAtY(checked((byte)reader.Y));
                Faces face = (Faces)reader.Direction;
                ByteVector2 coordinate = atlas.GetBlockUVs(blockId, face)[2];
                uint expected = (uint)(coordinate.y * atlas.tilesX + coordinate.x);
                Assert.Equal(expected, reader.TileIndex);
            }
        }
    }
}

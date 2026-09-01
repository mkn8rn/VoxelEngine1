using System.Buffers.Binary;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.WorldGeneration.Native;
using MVoxelEngine1.WorldGeneration.Terrain;

namespace MVoxelEngine1.Tests;

public sealed class NativeWorldSaveImportTests
{
    private const ushort CustomTransparentBlockId = 257;

    [Fact]
    public void LegacyRepresentationsEnterCompactNativeStorageBeforeSeed()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 32, lod1Radius: 1);
        using var workspace = new SaveWorkspace();
        ushort[] expanded = new ushort[Section.VOXELS_PER_SECTION];
        expanded[LinearIndex(3, 4, 5)] = CustomTransparentBlockId;
        ushort[] coercedExpanded = new ushort[Section.VOXELS_PER_SECTION];
        coercedExpanded[LinearIndex(7, 8, 9)] = WaterId;
        SectionFixture?[] sections =
        [
            SectionFixture.Empty(),
            SectionFixture.Uniform(SoilId),
            SectionFixture.Raw(expanded),
            SectionFixture.Packed(
                kind: 4,
                bitsPerIndex: 1,
                [0, WaterId],
                CreateIndices(1, LinearIndex(2, 3, 4), 1)),
            SectionFixture.Packed(
                kind: 5,
                bitsPerIndex: 2,
                [0, SoilId, WaterId, CustomTransparentBlockId],
                CreateIndices(2, LinearIndex(6, 7, 8), 3)),
            null,
            SectionFixture.Raw(coercedExpanded, kind: 2),
            SectionFixture.Uniform(WaterId)
        ];
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(0, 0, 0, sections)],
            sectionCountX: 2,
            sectionCountY: 2,
            sectionCountZ: 2);

        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        Assert.Equal(1, plan.ChunkCount);
        Assert.Equal(6, plan.SectionCount);
        Assert.Equal(2, plan.RawSectionCount);
        Assert.Equal(6, plan.PaletteCount);
        Assert.Equal(384, plan.PackedWordCount);

        using NativeGameSnapshot game = CreateGameSnapshot();
        using NativeGtrtSession session = CreateSession(settings, game, plan);
        plan.Import(session);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.PublicationState);
            Assert.Equal(1, view.State.MaterializedChunkCount);
            Assert.Equal(6, view.State.MaterializedSectionCount);
            Assert.Equal(2, view.State.MaterializedRawSectionCount);
            Assert.Equal(6, view.State.MaterializedPaletteCursor);
            Assert.Equal(384, view.State.MaterializedPackedWordCursor);
            int packedSectionCount = 0;
            foreach (NativeMaterializedSectionRecord section in
                     view.MaterializedSections)
            {
                if (section.StorageKind == NativeSectionStorageKind.Packed)
                    packedSectionCount++;
            }
            Assert.Equal(2, packedSectionCount);
        });

        session.PublishSeed(123456);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            AssertSavedBlock(ref view, chunkIndex, 1, 1, 17, SoilId);
            AssertSavedBlock(
                ref view,
                chunkIndex,
                3,
                20,
                5,
                CustomTransparentBlockId);
            AssertSavedBlock(ref view, chunkIndex, 2, 19, 20, WaterId);
            AssertSavedBlock(
                ref view,
                chunkIndex,
                22,
                7,
                8,
                CustomTransparentBlockId);
            AssertSavedBlock(ref view, chunkIndex, 17, 1, 17, 0);
            AssertSavedBlock(ref view, chunkIndex, 23, 24, 9, WaterId);
            AssertSavedBlock(ref view, chunkIndex, 20, 20, 20, WaterId);

            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                2,
                19,
                20,
                CustomTransparentBlockId));
            AssertSavedBlock(
                ref view,
                chunkIndex,
                2,
                19,
                20,
                CustomTransparentBlockId);
            AssertSavedBlock(ref view, chunkIndex, 1, 17, 20, 0);
            Assert.Equal(3, view.State.MaterializedRawSectionCount);
            Assert.Equal(0, view.State.FailureCode);
        });

        var exporter = new NativeWorldSaveExporter(session);
        Assert.Equal(1, exporter.SaveDirtyChunks(workspace.QuadsDirectory));
        Assert.Equal(0, exporter.SaveDirtyChunks(workspace.QuadsDirectory));

        NativeWorldSaveImportPlan reloadedPlan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        using NativeGameSnapshot reloadedGame = CreateGameSnapshot();
        using NativeGtrtSession reloadedSession = CreateSession(
            settings,
            reloadedGame,
            reloadedPlan);
        reloadedPlan.Import(reloadedSession);
        reloadedSession.PublishSeed(123456);
        reloadedSession.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            AssertSavedBlock(ref view, chunkIndex, 1, 1, 17, SoilId);
            AssertSavedBlock(
                ref view,
                chunkIndex,
                3,
                20,
                5,
                CustomTransparentBlockId);
            AssertSavedBlock(
                ref view,
                chunkIndex,
                2,
                19,
                20,
                CustomTransparentBlockId);
            AssertSavedBlock(
                ref view,
                chunkIndex,
                22,
                7,
                8,
                CustomTransparentBlockId);
            AssertSavedBlock(ref view, chunkIndex, 17, 1, 17, 0);
            AssertSavedBlock(ref view, chunkIndex, 23, 24, 9, WaterId);
            AssertSavedBlock(ref view, chunkIndex, 20, 20, 20, WaterId);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void UniformSavedChunkUsesOneRecordAndSurvivesMovement()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 1);
        using var workspace = new SaveWorkspace();
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(
                20,
                0,
                0,
                [SectionFixture.Uniform(SoilId)])],
            sectionCountX: 1,
            sectionCountY: 1,
            sectionCountZ: 1);
        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        Assert.Equal(1, plan.ChunkCount);
        Assert.Equal(0, plan.SectionCount);

        using NativeGameSnapshot game = CreateGameSnapshot();
        using NativeGtrtSession session = CreateSession(settings, game, plan);
        plan.Import(session);
        session.PublishSeed(123456);
        session.PrepareRun(123456, 20, 0, 0);
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int chunkIndex = view.GetChunkIndex(20, 0, 0);
            Assert.True(chunkIndex >= 0);
            AssertSavedBlock(ref view, chunkIndex, 15, 15, 15, SoilId);
            Assert.Equal(
                NativeChunkStorageKind.UniformSections,
                view.Chunks[chunkIndex].StorageKind);
            Assert.True(NativeMaterializedTerrain.TrySetBlock(
                ref view,
                chunkIndex,
                15,
                15,
                15,
                CustomTransparentBlockId));
            AssertSavedBlock(
                ref view,
                chunkIndex,
                15,
                15,
                15,
                CustomTransparentBlockId);
            AssertSavedBlock(ref view, chunkIndex, 14, 15, 15, SoilId);
            Assert.Equal(1, view.State.MaterializedChunkCount);
            Assert.Equal(1, view.State.MaterializedSectionCount);
            Assert.Equal(1, view.State.MaterializedRawSectionCount);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void EditedUniformChunkSurvivesNativeSaveAndReload()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 0);
        using var workspace = new SaveWorkspace();
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(
                0,
                0,
                0,
                [SectionFixture.Uniform(SoilId)])],
            sectionCountX: 1,
            sectionCountY: 1,
            sectionCountZ: 1);
        NativeWorldSaveImportPlan firstPlan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        var firstAtlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using (NativeWorld firstWorld = NativeWorld.CreateForTesting(
                   NativeGtrtPipeline.Create(
                       firstAtlas,
                       settings,
                       generationWorkerCount: 1,
                       meshWorkerCount: 1,
                       savePlan: firstPlan),
                   seed: 123456,
                   NullRenderer,
                   workspace.QuadsDirectory))
        {
            Assert.Equal(SoilId, firstWorld.GetBlock(1, 1, 1));
            Assert.True(firstWorld.SetBlock(
                1,
                1,
                1,
                CustomTransparentBlockId));
            Assert.Equal(1, firstWorld.Save());
            uint flags = ReadFirstChunkFlags(workspace.QuadsDirectory);
            Assert.Equal(0u, flags & (1u << 3));
            Assert.Equal(0, firstWorld.Save());
        }

        NativeWorldSaveImportPlan secondPlan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        var secondAtlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeWorld secondWorld = NativeWorld.CreateForTesting(
            NativeGtrtPipeline.Create(
                secondAtlas,
                settings,
                generationWorkerCount: 1,
                meshWorkerCount: 1,
                savePlan: secondPlan),
            seed: 123456,
            NullRenderer,
            workspace.QuadsDirectory);

        Assert.Equal(
            CustomTransparentBlockId,
            secondWorld.GetBlock(1, 1, 1));
        Assert.Equal(SoilId, secondWorld.GetBlock(2, 1, 1));
        Assert.Equal(0, secondWorld.Save());
    }

    [Fact]
    public void UniformSavedChunkProducesTheExactNativePreUploadPacket()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 0);
        using var workspace = new SaveWorkspace();
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(
                0,
                0,
                0,
                [SectionFixture.Uniform(WaterId)])],
            sectionCountX: 1,
            sectionCountY: 1,
            sectionCountZ: 1);
        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        using NativeGtrtPipeline pipeline = NativeGtrtPipeline.Create(
            atlas,
            settings,
            generationWorkerCount: 1,
            meshWorkerCount: 1,
            savePlan: plan);

        NativePreUploadPacket packet = pipeline.Run(123456);

        Assert.Equal(1, pipeline.RequiredPacketCount);
        Assert.Equal(0, packet.ChunkX);
        Assert.Equal(0, packet.ChunkY);
        Assert.Equal(0, packet.ChunkZ);
        Assert.Equal(0, packet.OpaqueFaceCount);
        Assert.Equal(0, packet.OpaqueRectangleCount);
        Assert.Equal(256, packet.TransparentFaceCount);
        Assert.Equal(1, packet.TransparentRectangleCount);

        int consumed = pipeline.ConsumeReadyPackets(
            static (in NativeChunkRenderPacketDescriptor descriptor,
                    ReadOnlySpan<uint> opaqueWords,
                    ReadOnlySpan<uint> transparentWords) =>
            {
                Assert.Equal(0, descriptor.ChunkWorldX);
                Assert.Equal(0, descriptor.ChunkWorldY);
                Assert.Equal(0, descriptor.ChunkWorldZ);
                Assert.Equal(0, descriptor.OpaqueFaceCount);
                Assert.Equal(0, descriptor.OpaqueRectangleCount);
                Assert.True(opaqueWords.IsEmpty);
                Assert.Equal(256, descriptor.TransparentFaceCount);
                Assert.Equal(1, descriptor.TransparentRectangleCount);
                Assert.Equal(256, PackedFaceRectangle.CountLogicalFaces(
                    transparentWords));

                Span<int> directionCounts = stackalloc int[6];
                var reader = new PackedFaceRectangleReader(
                    transparentWords);
                while (reader.MoveNext())
                {
                    Assert.InRange(reader.X, 0, 15);
                    Assert.InRange(reader.Y, 0, 15);
                    Assert.InRange(reader.Z, 0, 15);
                    directionCounts[reader.Direction]++;
                }
                Assert.Equal(256, directionCounts[2]);
                for (int direction = 0; direction < 6; direction++)
                {
                    if (direction != 2)
                        Assert.Equal(0, directionCounts[direction]);
                }
            });
        Assert.Equal(1, consumed);
    }

    [Fact]
    public void InvalidRuntimeBlockFailsBeforeAnyNativePublication()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 1);
        using var workspace = new SaveWorkspace();
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(
                0,
                0,
                0,
                [SectionFixture.Uniform(65000)])],
            sectionCountX: 1,
            sectionCountY: 1,
            sectionCountZ: 1);
        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        using NativeGameSnapshot game = CreateGameSnapshot();
        using NativeGtrtSession session = CreateSession(settings, game, plan);

        Assert.Throws<InvalidDataException>(() => plan.Import(session));
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.MaterializedChunkCount);
            Assert.Equal(0, view.State.MaterializedSectionCount);
            Assert.Equal(0, view.State.FailureCode);
            Assert.Equal(0, view.State.PublicationState);
        });
    }

    [Fact]
    public void InvalidPackedPaletteIndexFailsBeforeAnyNativePublication()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 1);
        using var workspace = new SaveWorkspace();
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(
                0,
                0,
                0,
                [SectionFixture.Packed(
                    kind: 4,
                    bitsPerIndex: 1,
                    [0],
                    CreateIndices(1, LinearIndex(1, 2, 3), 1))])],
            sectionCountX: 1,
            sectionCountY: 1,
            sectionCountZ: 1);
        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        using NativeGameSnapshot game = CreateGameSnapshot();
        using NativeGtrtSession session = CreateSession(settings, game, plan);

        Assert.Throws<InvalidDataException>(() => plan.Import(session));
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.MaterializedChunkCount);
            Assert.Equal(0, view.State.MaterializedSectionCount);
            Assert.Equal(0, view.State.MaterializedPaletteCursor);
            Assert.Equal(0, view.State.MaterializedPackedWordCursor);
            Assert.Equal(0, view.State.FailureCode);
            Assert.Equal(0, view.State.PublicationState);
        });
    }

    [Fact]
    public void ChangedFileFailsBeforeAnyNativePublication()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 1);
        using var workspace = new SaveWorkspace();
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(
                0,
                0,
                0,
                [SectionFixture.Uniform(SoilId)])],
            sectionCountX: 1,
            sectionCountY: 1,
            sectionCountZ: 1);
        NativeWorldSaveImportPlan plan =
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings);
        string path = Directory.GetFiles(workspace.QuadsDirectory).Single();
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
            stream.WriteByte(0xFF);

        using NativeGameSnapshot game = CreateGameSnapshot();
        using NativeGtrtSession session = CreateSession(settings, game, plan);
        Assert.Throws<InvalidDataException>(() => plan.Import(session));
        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            Assert.Equal(0, view.State.MaterializedChunkCount);
            Assert.Equal(0, view.State.MaterializedSectionCount);
            Assert.Equal(0, view.State.PublicationState);
        });
    }

    [Fact]
    public void TruncatedQuadFailsDuringThePlanningPass()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 1);
        using var workspace = new SaveWorkspace();
        Directory.CreateDirectory(workspace.QuadsDirectory);
        string path = Path.Combine(workspace.QuadsDirectory, "quad0x0.bin");
        File.WriteAllBytes(path, "MVQH"u8.ToArray());

        Assert.Throws<InvalidDataException>(() =>
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings));
    }

    [Fact]
    public void TrailingSectionPayloadFailsDuringThePlanningPass()
    {
        LoadDefaultGame();
        GameSettings settings = CreateSettings(chunkSize: 16, lod1Radius: 1);
        using var workspace = new SaveWorkspace();
        WriteQuads(
            workspace.QuadsDirectory,
            [new ChunkFixture(
                0,
                0,
                0,
                [SectionFixture.UniformWithTrailingData(SoilId)])],
            sectionCountX: 1,
            sectionCountY: 1,
            sectionCountZ: 1);

        Assert.Throws<InvalidDataException>(() =>
            NativeWorldSaveImportPlan.Create(
                workspace.QuadsDirectory,
                settings));
    }

    private static NativeGameSnapshot CreateGameSnapshot()
    {
        var atlas = new BlockTextureAtlas(
            BlockTextureAtlasUploadMode.SimulatedGpuUpload);
        return NativeGameSnapshot.Create(atlas);
    }

    private static INativeChunkRenderer? NullRenderer(
        in NativeChunkRenderPacketDescriptor descriptor,
        ReadOnlySpan<uint> opaqueWords,
        ReadOnlySpan<uint> transparentWords)
    {
        Assert.Equal(descriptor.OpaqueWordCount, opaqueWords.Length);
        Assert.Equal(
            descriptor.TransparentWordCount,
            transparentWords.Length);
        return null;
    }

    private static NativeGtrtSession CreateSession(
        GameSettings settings,
        NativeGameSnapshot game,
        NativeWorldSaveImportPlan plan) =>
        NativeGtrtSession.Create(
            settings,
            game,
            generationWorkerCount: 1,
            meshWorkerCount: 1,
            materializedChunkCapacity: Math.Max(1, plan.ChunkCount),
            materializedSectionCapacity: Math.Max(1, plan.SectionCount),
            materializedRawSectionCapacity: checked(
                plan.RawSectionCount + 1),
            materializedPaletteCapacity: plan.PaletteCount,
            materializedPackedWordCapacity: plan.PackedWordCount);

    private static void AssertSavedBlock(
        scoped ref NativeGtrtSessionView view,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        ushort expected)
    {
        Assert.True(NativeMaterializedTerrain.TryGetBlock(
            ref view,
            chunkIndex,
            localX,
            localY,
            localZ,
            out ushort blockId,
            out bool handled));
        Assert.True(handled);
        Assert.Equal(expected, blockId);
    }

    private static int[] CreateIndices(
        int bitsPerIndex,
        int changedIndex,
        int changedValue)
    {
        int[] indices = new int[Section.VOXELS_PER_SECTION];
        Assert.InRange(changedValue, 0, (1 << bitsPerIndex) - 1);
        indices[changedIndex] = changedValue;
        return indices;
    }

    private static int LinearIndex(int x, int y, int z) =>
        ((z * Section.SECTION_SIZE) + x) * Section.SECTION_SIZE + y;

    private static uint ReadFirstChunkFlags(string quadsDirectory)
    {
        string path = Directory.GetFiles(
            quadsDirectory,
            "quad*x*.bin").Single();
        byte[] quad = File.ReadAllBytes(path);
        const int quadHeaderSize = 20;
        const int recordHeaderSize = 16;
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            quad.AsSpan(quadHeaderSize + 12, sizeof(int)));
        ReadOnlySpan<byte> payload = quad.AsSpan(
            quadHeaderSize + recordHeaderSize,
            payloadLength);
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(
            payload[32..]);
        int footerOffset = checked(36 + sectionCount * sizeof(uint));
        for (int index = 0; index < sectionCount; index++)
        {
            int tableOffset = checked(36 + index * sizeof(uint));
            int sectionOffset = checked((int)
                BinaryPrimitives.ReadUInt32LittleEndian(
                    payload[tableOffset..]));
            if (sectionOffset == 0)
                continue;
            ushort sectionLength =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    payload[(sectionOffset + 1)..]);
            footerOffset = Math.Max(
                footerOffset,
                checked(sectionOffset + 3 + sectionLength));
        }

        Assert.True(payload.Slice(footerOffset, 3).SequenceEqual("CMD"u8));
        return BinaryPrimitives.ReadUInt32LittleEndian(
            payload[(footerOffset + 11)..]);
    }

    private static ushort SoilId => (byte)BaseBlockType.Soil;

    private static ushort WaterId => (byte)BaseBlockType.Water;

    private static void LoadDefaultGame()
    {
        GameManager.Initialize(TestPaths.GameDataRoot);
        string game = GameManager.SelectGameFolder("Default");
        GameManager.LoadGameDefaultSettings(game);
        TerrainLoader.allBlockTypes.Clear();
        TerrainLoader.allBlockTypesByBaseType.Clear();
        TerrainLoader.allBlockTypesByIds.Clear();
        TerrainLoader.allBlockTypeObjects.Clear();
        _ = new TerrainLoader();
        BiomeManager.LoadAllBiomes();
    }

    private static GameSettings CreateSettings(
        int chunkSize,
        int lod1Radius)
    {
        GameSettings source = GameManager.settings;
        return new GameSettings
        {
            chunkMaxX = chunkSize,
            chunkMaxY = chunkSize,
            chunkMaxZ = chunkSize,
            blockTileWidth = source.blockTileWidth,
            blockTileHeight = source.blockTileHeight,
            textureFileExtension = source.textureFileExtension,
            renderStreamingAllowed = false,
            lod1RenderDistance = lod1Radius,
            lod2RenderDistance = source.lod2RenderDistance,
            lod3RenderDistance = source.lod3RenderDistance,
            lod4RenderDistance = source.lod4RenderDistance,
            lod5RenderDistance = source.lod5RenderDistance,
            entityLoadRange = source.entityLoadRange,
            entitySpawnMaxRange = source.entitySpawnMaxRange,
            entityDespawnMaxRange = source.entityDespawnMaxRange,
            regionWidthInChunks = source.regionWidthInChunks,
            oneRegionWorld = source.oneRegionWorld,
            chunkGenerationBufferInitial = lod1Radius,
            chunkGenerationBufferRuntime = lod1Radius,
            gameDataDirectory = source.gameDataDirectory,
            loadedGameDirectory = source.loadedGameDirectory,
            loadedGameSettingsDirectory =
                source.loadedGameSettingsDirectory,
            assetsBaseBlockTexturesDirectory =
                source.assetsBaseBlockTexturesDirectory,
            assetsBlockTexturesDirectory =
                source.assetsBlockTexturesDirectory,
            dataBlockTypesDirectory = source.dataBlockTypesDirectory,
            dataBiomeTypesDirectory = source.dataBiomeTypesDirectory,
            savesWorldDirectory = source.savesWorldDirectory,
            savesCharactersDirectory = source.savesCharactersDirectory
        };
    }

    private static void WriteQuads(
        string quadsDirectory,
        IReadOnlyList<ChunkFixture> chunks,
        int sectionCountX,
        int sectionCountY,
        int sectionCountZ)
    {
        Directory.CreateDirectory(quadsDirectory);
        foreach (IGrouping<(int X, int Z), ChunkFixture> group in
                 chunks.GroupBy(chunk =>
                     Quadrant.GetBatchIndices(chunk.X, chunk.Z)))
        {
            string path = Path.Combine(
                quadsDirectory,
                $"quad{group.Key.X}x{group.Key.Z}.bin");
            using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            using var writer = new BinaryWriter(stream);
            writer.Write("MVQH"u8);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write(group.Key.X);
            writer.Write(group.Key.Z);
            ChunkFixture[] records = group.ToArray();
            writer.Write(records.Length);
            foreach (ChunkFixture chunk in records)
            {
                byte[] payload = WriteChunk(
                    chunk,
                    sectionCountX,
                    sectionCountY,
                    sectionCountZ);
                writer.Write(chunk.X);
                writer.Write(chunk.Y);
                writer.Write(chunk.Z);
                writer.Write(payload.Length);
                writer.Write(payload);
            }
        }
    }

    private static byte[] WriteChunk(
        ChunkFixture chunk,
        int sectionCountX,
        int sectionCountY,
        int sectionCountZ)
    {
        int sectionCount = checked(
            sectionCountX * sectionCountY * sectionCountZ);
        Assert.Equal(sectionCount, chunk.Sections.Length);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("MVCH"u8);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write(chunk.X);
        writer.Write(chunk.Y);
        writer.Write(chunk.Z);
        writer.Write(sectionCountX);
        writer.Write(sectionCountY);
        writer.Write(sectionCountZ);
        writer.Write(sectionCount);
        long tablePosition = stream.Position;
        for (int index = 0; index < sectionCount; index++)
            writer.Write(0u);

        uint[] offsets = new uint[sectionCount];
        for (int index = 0; index < sectionCount; index++)
        {
            SectionFixture? section = chunk.Sections[index];
            if (section is null)
                continue;

            offsets[index] = checked((uint)stream.Position);
            writer.Write(section.Kind);
            byte[] payload = section.WritePayload();
            writer.Write(checked((ushort)payload.Length));
            writer.Write(payload);
        }

        long endPosition = stream.Position;
        stream.Position = tablePosition;
        foreach (uint offset in offsets)
            writer.Write(offset);
        stream.Position = endPosition;
        return stream.ToArray();
    }

    private sealed record ChunkFixture(
        int X,
        int Y,
        int Z,
        SectionFixture?[] Sections);

    private sealed class SectionFixture
    {
        private readonly ushort uniformBlockId;
        private readonly ushort[]? raw;
        private readonly ushort[]? palette;
        private readonly int[]? paletteIndices;
        private readonly bool appendTrailingData;

        private SectionFixture(
            byte kind,
            ushort uniformBlockId = 0,
            ushort[]? raw = null,
            byte bitsPerIndex = 0,
            ushort[]? palette = null,
            int[]? paletteIndices = null,
            bool appendTrailingData = false)
        {
            Kind = kind;
            this.uniformBlockId = uniformBlockId;
            this.raw = raw;
            BitsPerIndex = bitsPerIndex;
            this.palette = palette;
            this.paletteIndices = paletteIndices;
            this.appendTrailingData = appendTrailingData;
        }

        internal byte Kind { get; }

        internal byte BitsPerIndex { get; }

        internal static SectionFixture Empty() => new(kind: 0);

        internal static SectionFixture Uniform(ushort blockId) =>
            new(kind: 1, uniformBlockId: blockId);

        internal static SectionFixture UniformWithTrailingData(
            ushort blockId) =>
            new(
                kind: 1,
                uniformBlockId: blockId,
                appendTrailingData: true);

        internal static SectionFixture Raw(ushort[] values, byte kind = 3) =>
            new(kind, raw: values);

        internal static SectionFixture Packed(
            byte kind,
            byte bitsPerIndex,
            ushort[] palette,
            int[] paletteIndices) =>
            new(
                kind,
                bitsPerIndex: bitsPerIndex,
                palette: palette,
                paletteIndices: paletteIndices);

        internal byte[] WritePayload()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0);
            writer.Write((byte)0);
            switch (Kind)
            {
                case 0:
                    break;
                case 1:
                    writer.Write(uniformBlockId);
                    break;
                case 2:
                case 3:
                    Assert.NotNull(raw);
                    Assert.Equal(Section.VOXELS_PER_SECTION, raw!.Length);
                    foreach (ushort blockId in raw)
                        writer.Write(blockId);
                    break;
                case 4:
                case 5:
                    Assert.NotNull(palette);
                    Assert.NotNull(paletteIndices);
                    Assert.Equal(
                        Section.VOXELS_PER_SECTION,
                        paletteIndices.Length);
                    writer.Write(BitsPerIndex);
                    writer.Write(checked((ushort)palette.Length));
                    foreach (ushort blockId in palette)
                        writer.Write(blockId);
                    uint[] words = Pack(paletteIndices, BitsPerIndex);
                    writer.Write(words.Length);
                    foreach (uint word in words)
                        writer.Write(word);
                    break;
                default:
                    throw new InvalidOperationException(
                        "The test section kind is invalid.");
            }
            if (appendTrailingData)
                writer.Write((byte)0xFF);
            return stream.ToArray();
        }

        private static uint[] Pack(int[] indices, int bitsPerIndex)
        {
            int wordCount = checked(
                (indices.Length * bitsPerIndex + 31) / 32);
            uint[] words = new uint[wordCount];
            uint mask = (1u << bitsPerIndex) - 1u;
            for (int index = 0; index < indices.Length; index++)
            {
                uint value = checked((uint)indices[index]);
                Assert.Equal(0u, value & ~mask);
                long bitPosition = (long)index * bitsPerIndex;
                int wordIndex = (int)(bitPosition >> 5);
                int bitOffset = (int)(bitPosition & 31);
                words[wordIndex] |= value << bitOffset;
                int remaining = 32 - bitOffset;
                if (remaining < bitsPerIndex)
                    words[wordIndex + 1] |= value >> remaining;
            }
            return words;
        }
    }

    private sealed class SaveWorkspace : IDisposable
    {
        internal SaveWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "VoxelEngine1",
                "native-save-import",
                Guid.NewGuid().ToString("N"));
            QuadsDirectory = Path.Combine(Root, "quads");
        }

        internal string Root { get; }

        internal string QuadsDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}

using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.Infrastructure.Models.Terrain;
using MVoxelEngine1.WorldGeneration.Native;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests;

public sealed class NativeGeneratedTerrainTests
{
    [Fact]
    public void SessionPreservesRuntimeMaterialFlagsAndTiles()
    {
        NativeTerrainMaterialSet expected = CreateRuntimeMaterials();
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 4,
            chunkSizeZ: 4,
            lod1Radius: 0,
            materials: expected);
        using NativeGtrtSession session = NativeGtrtSession.Create(layout);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            AssertDescriptorEqual(expected.Stone, view.Materials.Stone);
            AssertDescriptorEqual(expected.Soil, view.Materials.Soil);
            AssertDescriptorEqual(expected.Water, view.Materials.Water);
        });
    }

    [Fact]
    public void QueriesGeneratedProfilesAcrossEveryChunkBoundary()
    {
        using NativeGtrtSession session = CreateSession();
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int chunkIndex = view.GetChunkIndex(0, 0, 0);

            AssertBlock(ref view, chunkIndex, 4, 3, 1, WaterId);
            AssertBlock(ref view, chunkIndex, -1, 3, 1, WaterId);
            AssertBlock(ref view, chunkIndex, 1, 3, 4, WaterId);
            AssertBlock(ref view, chunkIndex, 1, 3, -1, WaterId);
            AssertBlock(ref view, chunkIndex, 1, 4, 1, WaterId);
            AssertBlock(ref view, chunkIndex, 1, -1, 1, 0);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void SharedOpaqueAndWaterFacesAreHiddenInBothDirections()
    {
        using NativeGtrtSession session = CreateSession();
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int center = view.GetChunkIndex(0, 0, 0);
            int right = view.GetChunkIndex(1, 0, 0);

            AssertFace(ref view, center, 3, 0, 1, 1, false);
            AssertFace(ref view, right, 0, 0, 1, 0, false);
            AssertFace(ref view, center, 3, 3, 1, 1, false);
            AssertFace(ref view, right, 0, 3, 1, 0, false);
            AssertFace(ref view, center, 1, 3, 3, 5, false);
            AssertFace(ref view, center, 1, 3, 0, 4, false);
            AssertFace(ref view, center, 1, 3, 1, 3, false);
            int upper = view.GetChunkIndex(0, 1, 0);
            AssertFace(ref view, upper, 1, 0, 1, 2, false);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void WaterFaceAppearsWhenTheNeighborProfileContainsAir()
    {
        using NativeGtrtSession session = CreateSession();
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int rightColumn = view.GetColumnIndex(1, 0);
            Span<BlockColumnProfile> rightProfiles =
                view.GetColumnProfiles(rightColumn);
            rightProfiles[1] = new BlockColumnProfile
            {
                StoneStart = 0,
                StoneEnd = 1,
                SoilStart = 2,
                SoilEnd = 2,
                WaterStart = -1,
                WaterEnd = -1
            };

            int center = view.GetChunkIndex(0, 0, 0);
            AssertFace(ref view, center, 3, 3, 1, 1, true);
            Assert.Equal(0, view.State.FailureCode);
        });
    }

    [Fact]
    public void MissingResidentNeighborFailsClosed()
    {
        using NativeGtrtSession session = CreateSession();
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int outerChunk = view.GetChunkIndex(2, 0, 0);

            Assert.False(NativeGeneratedTerrain.TryIsFaceVisible(
                ref view,
                outerChunk,
                3,
                3,
                1,
                direction: 1,
                out bool visible));
            Assert.False(visible);
            Assert.Equal(
                (int)NativeGtrtFailureCode.InvalidTerrainQuery,
                view.State.FailureCode);
        });
    }

    [Fact]
    public void UnreadyResidentNeighborFailsClosed()
    {
        using NativeGtrtSession session = CreateSession();
        session.PublishSeed(123456);

        session.Access(owner =>
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
            int rightColumn = view.GetColumnIndex(1, 0);
            view.Columns[rightColumn].State = NativeColumnState.Reserved;
            int center = view.GetChunkIndex(0, 0, 0);

            Assert.False(NativeGeneratedTerrain.TryIsFaceVisible(
                ref view,
                center,
                3,
                3,
                1,
                direction: 1,
                out bool visible));
            Assert.False(visible);
            Assert.Equal(
                (int)NativeGtrtFailureCode.InvalidTerrainQuery,
                view.State.FailureCode);
        });
    }

    [Theory]
    [InlineData(true, 5, true, 6, false)]
    [InlineData(true, 5, false, 0, true)]
    [InlineData(false, 11, true, 5, false)]
    [InlineData(false, 11, false, 11, false)]
    [InlineData(false, 11, false, 12, true)]
    [InlineData(false, 11, false, 0, true)]
    public void FaceVisibilityMatchesTheLiveRenderer(
        bool sourceOpaque,
        ushort sourceId,
        bool neighborOpaque,
        ushort neighborId,
        bool expected)
    {
        Assert.Equal(
            expected,
            NativeGeneratedTerrain.FaceVisible(
                sourceOpaque,
                sourceId,
                neighborOpaque,
                neighborId));
    }

    [Fact]
    public void QueryKernelAllocatesNoManagedBytesAfterWarmup()
    {
        var harness = new TerrainQueryHarness();
        using (NativeGtrtSession warmup = CreateSession())
        {
            warmup.PublishSeed(123456);
            warmup.Access(harness.PrepareAction);
            warmup.Access(harness.QueryAction);
            Assert.True(harness.Succeeded);
        }

        using NativeGtrtSession measured = CreateSession();
        measured.PublishSeed(123456);
        measured.Access(harness.PrepareAction);
        harness.Succeeded = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        measured.Access(harness.QueryAction);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(harness.Succeeded);
        Assert.NotEqual(0, harness.Checksum);
        Assert.Equal(0, allocated);
    }

    private static ushort StoneId => (byte)BaseBlockType.Stone;

    private static ushort SoilId => (byte)BaseBlockType.Soil;

    private static ushort WaterId => (byte)BaseBlockType.Water;

    private static NativeGtrtSession CreateSession()
    {
        var layout = new NativeGtrtSessionLayout(
            chunkSizeX: 4,
            chunkSizeY: 4,
            chunkSizeZ: 4,
            lod1Radius: 1,
            materials: NativeTerrainMaterialSet.CreateConventional());
        return NativeGtrtSession.Create(layout);
    }

    private static NativeTerrainMaterialSet CreateRuntimeMaterials() =>
        new(
            new NativeBlockDescriptor(
                StoneId,
                BaseBlockType.Stone,
                BlockStateOfMatter.Solid,
                NativeBlockFlags.Defined | NativeBlockFlags.Opaque,
                1,
                2,
                3,
                4,
                5,
                6),
            new NativeBlockDescriptor(
                SoilId,
                BaseBlockType.Soil,
                BlockStateOfMatter.Solid,
                NativeBlockFlags.Defined | NativeBlockFlags.Opaque,
                7,
                8,
                9,
                10,
                11,
                12),
            new NativeBlockDescriptor(
                WaterId,
                BaseBlockType.Water,
                BlockStateOfMatter.Liquid,
                NativeBlockFlags.Defined |
                    NativeBlockFlags.Transparent |
                    NativeBlockFlags.Liquid,
                13,
                14,
                15,
                16,
                17,
                18));

    private static void PrepareTerrain(
        scoped ref NativeGtrtSessionView view)
    {
        var profile = new BlockColumnProfile
        {
            StoneStart = 0,
            StoneEnd = 1,
            SoilStart = 2,
            SoilEnd = 2,
            WaterStart = 3,
            WaterEnd = 5
        };

        Span<NativeColumnRecord> columns = view.Columns;
        for (int columnIndex = 0;
             columnIndex < columns.Length;
             columnIndex++)
        {
            ref NativeColumnRecord column = ref columns[columnIndex];
            view.GetColumnProfiles(columnIndex).Fill(profile);
            column.GenerationEpoch = view.State.SessionEpoch;
            column.State = NativeColumnState.Generated;
        }
    }

    private static void AssertBlock(
        scoped ref NativeGtrtSessionView view,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        ushort expected)
    {
        Assert.True(NativeGeneratedTerrain.TryGetBlock(
            ref view,
            chunkIndex,
            localX,
            localY,
            localZ,
            out ushort actual));
        Assert.Equal(expected, actual);
    }

    private static void AssertFace(
        scoped ref NativeGtrtSessionView view,
        int chunkIndex,
        int localX,
        int localY,
        int localZ,
        byte direction,
        bool expected)
    {
        Assert.True(NativeGeneratedTerrain.TryIsFaceVisible(
            ref view,
            chunkIndex,
            localX,
            localY,
            localZ,
            direction,
            out bool actual));
        Assert.Equal(expected, actual);
    }

    private static void AssertDescriptorEqual(
        NativeBlockDescriptor expected,
        NativeBlockDescriptor actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.BaseType, actual.BaseType);
        Assert.Equal(expected.StateOfMatter, actual.StateOfMatter);
        Assert.Equal(expected.Flags, actual.Flags);
        for (byte direction = 0; direction < 6; direction++)
            Assert.Equal(expected.GetTile(direction), actual.GetTile(direction));
    }

    private sealed class TerrainQueryHarness
    {
        internal TerrainQueryHarness()
        {
            PrepareAction = Prepare;
            QueryAction = Query;
        }

        internal NativeLeaseAction<byte> PrepareAction { get; }

        internal NativeLeaseAction<byte> QueryAction { get; }

        internal bool Succeeded { get; set; }

        internal int Checksum { get; private set; }

        private static void Prepare(
            scoped NativeLeaseView<byte> owner)
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            PrepareTerrain(ref view);
        }

        private void Query(scoped NativeLeaseView<byte> owner)
        {
            var view = new NativeGtrtSessionView(owner.AsSpan());
            int chunkIndex = view.GetChunkIndex(0, 0, 0);
            int checksum = 0;
            for (int index = 0; index < 10_000; index++)
            {
                int localX = index & 3;
                int localY = index % 6;
                int localZ = (index >> 2) & 3;
                byte direction = (byte)(index % 6);
                if (!NativeGeneratedTerrain.TryGetBlock(
                        ref view,
                        chunkIndex,
                        localX,
                        localY,
                        localZ,
                        out ushort blockId) ||
                    !NativeGeneratedTerrain.TryIsFaceVisible(
                        ref view,
                        chunkIndex,
                        localX,
                        localY,
                        localZ,
                        direction,
                        out bool visible))
                {
                    Succeeded = false;
                    return;
                }

                checksum = unchecked(
                    checksum * 31 + blockId + (visible ? 1 : 0));
            }

            Checksum = checksum;
            Succeeded = view.State.FailureCode == 0;
        }
    }
}

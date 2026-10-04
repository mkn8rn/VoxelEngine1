using System.Globalization;
using System.Resources;

namespace MVoxelEngine1.Infrastructure.Resources;

public static class EngineMessages
{
    private static readonly ResourceManager resources = new("MVoxelEngine1.Infrastructure.Resources.EngineMessages", typeof(EngineMessages).Assembly);

    public static string SelectWorld => Get(nameof(SelectWorld));

    public static string GenerateNewWorld => Get(nameof(GenerateNewWorld));

    public static string EnterWorldName => Get(nameof(EnterWorldName));

    public static string WorldNameLatinOnly => Get(nameof(WorldNameLatinOnly));

    public static string WorldNameInUse => Get(nameof(WorldNameInUse));

    public static string EnterWorldSeed => Get(nameof(EnterWorldSeed));

    public static string WorldSeedInteger => Get(nameof(WorldSeedInteger));

    public static string TerrainLoading => Get(nameof(TerrainLoading));

    public static string TerrainLoaded => Get(nameof(TerrainLoaded));

    public static string SelectGame => Get(nameof(SelectGame));

    public static string InvalidInput => Get(nameof(InvalidInput));

    public static string NoCustomBlockFiles => Get(nameof(NoCustomBlockFiles));

    public static string GeneratingTextureAtlas => Get(nameof(GeneratingTextureAtlas));

    public static string GeneratingSimulatedTextureAtlas => Get(nameof(GeneratingSimulatedTextureAtlas));

    public static string LoadingBaseTextures => Get(nameof(LoadingBaseTextures));

    public static string LoadingOtherTextures => Get(nameof(LoadingOtherTextures));

    public static string TextureAtlasGenerated => Get(nameof(TextureAtlasGenerated));

    public static string MappingTextures => Get(nameof(MappingTextures));

    public static string InitializingGameManager => Get(nameof(InitializingGameManager));

    public static string InitializingDataLoaders => Get(nameof(InitializingDataLoaders));

    public static string LoadingBiomes => Get(nameof(LoadingBiomes));

    public static string InitializingTextureAtlases => Get(nameof(InitializingTextureAtlases));

    public static string InitializingShaders => Get(nameof(InitializingShaders));

    public static string InitializingPlayer => Get(nameof(InitializingPlayer));

    public static string EnablingOpenGl => Get(nameof(EnablingOpenGl));

    public static string OptimizedFaceMode => Get(nameof(OptimizedFaceMode));

    public static string ReferenceValidationEnabled => Get(nameof(ReferenceValidationEnabled));

    public static string SimulatedUploadStarted => Get(nameof(SimulatedUploadStarted));

    private static string Get(string name) => resources.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"The engine message resource '{name}' is missing.");
}

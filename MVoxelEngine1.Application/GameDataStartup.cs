using MVoxelEngine1.Infrastructure.Resources;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;

namespace MVoxelEngine1.Application
{
    internal static class GameDataStartup
    {
        public static TerrainLoader Load()
        {
            Console.WriteLine(EngineMessages.InitializingGameManager);
            GameManager.Initialize();

            string game = GameManager.SelectGameFolder(FlagManager.flags.game);
            GameManager.LoadGameDefaultSettings(game);

            Console.WriteLine(EngineMessages.InitializingDataLoaders);
            var terrainLoader = new TerrainLoader();

            Console.WriteLine(EngineMessages.LoadingBiomes);
            BiomeManager.LoadAllBiomes();
            Console.WriteLine($"Loaded {BiomeManager.Biomes.Count} biome(s).");
            StartupPerformanceRecorder.RecordGameLoaded();
            return terrainLoader;
        }
    }
}

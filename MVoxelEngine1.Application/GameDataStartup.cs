using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;

namespace MVoxelEngine1.Application
{
    internal static class GameDataStartup
    {
        public static TerrainLoader Load()
        {
            Console.WriteLine("Game manager initializing.");
            GameManager.Initialize();

            string game = GameManager.SelectGameFolder(FlagManager.flags.game);
            GameManager.LoadGameDefaultSettings(game);

            Console.WriteLine("Data loaders initializing.");
            var terrainLoader = new TerrainLoader();

            Console.WriteLine("Biomes loading.");
            BiomeManager.LoadAllBiomes();
            Console.WriteLine($"Loaded {BiomeManager.Biomes.Count} biome(s).");
            StartupPerformanceRecorder.RecordGameLoaded();
            return terrainLoader;
        }
    }
}

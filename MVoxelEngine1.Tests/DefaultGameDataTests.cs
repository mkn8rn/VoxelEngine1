using System.Buffers.Binary;
using System.Text.Json;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;

namespace MVoxelEngine1.Tests
{
    public class DefaultGameDataTests
    {
        [Fact]
        public void DefaultGameLoadsWithResolvedDirectories()
        {
            string defaultGame = LoadDefaultGame(TestPaths.GameDataRoot);
            var settings = GameManager.settings;

            Assert.Equal(Path.GetFullPath(TestPaths.GameDataRoot), settings.gameDataDirectory);
            Assert.Equal(Path.GetFullPath(defaultGame), settings.loadedGameDirectory);
            Assert.Equal(160, settings.chunkMaxX);
            Assert.Equal(160, settings.chunkMaxY);
            Assert.Equal(160, settings.chunkMaxZ);
            Assert.True(Directory.Exists(settings.assetsBaseBlockTexturesDirectory));
            Assert.True(Directory.Exists(settings.assetsBlockTexturesDirectory));
            Assert.True(Directory.Exists(settings.dataBlockTypesDirectory));
            Assert.True(Directory.Exists(settings.dataBiomeTypesDirectory));
            Assert.True(Directory.Exists(settings.savesWorldDirectory));
            Assert.True(Directory.Exists(settings.savesCharactersDirectory));
        }

        [Fact]
        public void DefaultGameJsonFilesHaveExpectedRootKinds()
        {
            string defaultGame = LoadDefaultGame(TestPaths.GameDataRoot);
            var jsonOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            string defaultsFile = Path.Combine(defaultGame, "Defaults.txt");
            AssertJsonRoot(defaultsFile, JsonValueKind.Object, jsonOptions);

            string dataDirectory = Path.Combine(defaultGame, "Data");
            string[] dataFiles = Directory.GetFiles(dataDirectory, "*.txt", SearchOption.AllDirectories);
            Assert.NotEmpty(dataFiles);

            foreach (string dataFile in dataFiles)
            {
                JsonValueKind expectedKind = Path.GetFileName(dataFile).Equals("GenerationRules.txt", StringComparison.OrdinalIgnoreCase)
                    ? JsonValueKind.Array
                    : JsonValueKind.Object;
                AssertJsonRoot(dataFile, expectedKind, jsonOptions);
            }
        }

        [Fact]
        public void DefaultGamePngTexturesMatchDeclaredTileSize()
        {
            string defaultGame = LoadDefaultGame(TestPaths.GameDataRoot);
            var settings = GameManager.settings;
            string texturesDirectory = Path.Combine(defaultGame, "Assets", "Textures", "Blocks");
            string[] textureFiles = Directory.GetFiles(texturesDirectory, "*.png", SearchOption.AllDirectories);
            Assert.NotEmpty(textureFiles);
            Assert.Equal(".png", settings.textureFileExtension);

            byte[] expectedSignature = [137, 80, 78, 71, 13, 10, 26, 10];
            foreach (string textureFile in textureFiles)
            {
                byte[] header = File.ReadAllBytes(textureFile).Take(24).ToArray();
                Assert.Equal(24, header.Length);
                Assert.Equal(expectedSignature, header[..8]);
                Assert.Equal(settings.blockTileWidth, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
                Assert.Equal(settings.blockTileHeight, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
            }
        }

        [Fact]
        public void WorldSaveRoundTripsTheFourLineFormat()
        {
            using TestWorkspace workspace = TestPaths.CreateWorkspace();
            string defaultGame = LoadDefaultGame(workspace.GameDataRoot);

            var writer = new WorldLoader();
            writer.CreateWorldSave("RoundTrip", 123456);

            string worldFile = Path.Combine(defaultGame, "Saves", "Worlds", writer.ID.ToString(), "world.txt");
            string[] lines = File.ReadAllLines(worldFile);
            Assert.Equal(4, lines.Length);
            Assert.Equal(writer.ID, Guid.Parse(lines[0]));
            Assert.Equal(writer.RegionID, Guid.Parse(lines[1]));
            Assert.Equal("RoundTrip", lines[2]);
            Assert.Equal(123456, int.Parse(lines[3]));

            var reader = new WorldLoader();
            reader.LoadWorldSave(writer.ID);
            Assert.Equal(writer.ID, reader.ID);
            Assert.Equal(writer.RegionID, reader.RegionID);
            Assert.Equal("RoundTrip", reader.worldName);
            Assert.Equal(123456, reader.seed);
        }

        private static string LoadDefaultGame(string gameDataRoot)
        {
            Assert.True(Directory.Exists(gameDataRoot), $"GameData directory was not copied to {gameDataRoot}.");
            GameManager.Initialize(gameDataRoot);
            string defaultGame = GameManager.SelectGameFolder("Default");
            GameManager.LoadGameDefaultSettings(defaultGame);
            return defaultGame;
        }

        private static void AssertJsonRoot(string path, JsonValueKind expectedKind, JsonDocumentOptions options)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), options);
            Assert.Equal(expectedKind, document.RootElement.ValueKind);
        }
    }
}

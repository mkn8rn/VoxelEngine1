namespace MVGE_INF.Models
{
    public class GameSettings
    {
        // Chunk settings (required in JSON)
        public required int chunkMaxX { get; set; }
        public required int chunkMaxZ { get; set; }
        public required int chunkMaxY { get; set; }

        // Block texture atlas settings (required)
        public required int blockTileWidth { get; set; }
        public required int blockTileHeight { get; set; }
        public required string textureFileExtension { get; set; }

        // Render settings
        public bool renderStreamingAllowed { get; set; }

        // Render distances (required)
        public required int lod1RenderDistance { get; set; }
        public required int lod2RenderDistance { get; set; }
        public required int lod3RenderDistance { get; set; }
        public required int lod4RenderDistance { get; set; }
        public required int lod5RenderDistance { get; set; }
        public required int entityLoadRange { get; set; }
        public required int entitySpawnMaxRange { get; set; }
        public required int entityDespawnMaxRange { get; set; }

        // Generation settings (required)
        public required long regionWidthInChunks { get; set; }
        public required bool oneRegionWorld { get; set; }
        public required int chunkGenerationBufferRuntime { get; set; }
        public required int chunkGenerationBufferInitial { get; set; }

        // Directories (required in JSON except those computed at runtime)
        public string gamesDirectory { get; set; } = string.Empty;          // set by GameManager (not required in JSON)
        public string loadedGameDirectory { get; set; } = string.Empty;      // set by GameManager (not required in JSON)
        public required string loadedGameSettingsDirectory { get; set; }
        public required string assetsBaseBlockTexturesDirectory { get; set; }
        public required string assetsBlockTexturesDirectory { get; set; }
        public required string dataBlockTypesDirectory { get; set; }
        public required string dataBiomeTypesDirectory { get; set; }
        public required string savesWorldDirectory { get; set; }
        public required string savesCharactersDirectory { get; set; }
    }
}

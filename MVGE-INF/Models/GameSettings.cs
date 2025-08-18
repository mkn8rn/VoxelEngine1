namespace MVGE_INF.Models
{
    public struct GameSettings
    {
        // Chunk settings
        public int chunkMaxX;
        public int chunkMaxZ;
        public int chunkMaxY;

        // Block texture atlas settings
        public int blockTileWidth;
        public int blockTileHeight;
        public string textureFileExtension;

        // Render distances
        public int lod1RenderDistance;
        public int lod2RenderDistance;
        public int lod3RenderDistance;
        public int lod4RenderDistance;
        public int lod5RenderDistance;
        public int entityLoadRange;
        public int entitySpawnMaxRange;
        public int entityDespawnMaxRange;

        // Directories
        public string gamesDirectory;
        public string loadedGameDirectory;
        public string loadedGameSettingsDirectory;
        public string assetsBaseBlockTexturesDirectory;
        public string assetsBlockTexturesDirectory;
        public string dataBlockTypesDirectory;
        public string dataBiomeTypesDirectory;
        public string savesWorldDirectory;
        public string savesCharactersDirectory;
    }
}

using MVGE_GFX.Terrain;
using System;

namespace MVGE_GFX
{
    public static class GraphicsResourceManager
    {
        private static BlockTextureAtlas atlasInstance;
        private static bool preloadStarted;
        private static readonly object lockObj = new();

        public static void BeginPreload()
        {
            lock (lockObj)
            {
                if (preloadStarted) return;
                preloadStarted = true;
            }
            // Safe to call multiple times; internal method guards itself
            BlockTextureAtlas.BeginAsyncIOPreload();
        }

        public static BlockTextureAtlas EnsureAtlas()
        {
            if (atlasInstance != null) return atlasInstance;
            lock (lockObj)
            {
                if (atlasInstance == null)
                {
                    atlasInstance = BlockTextureAtlas.GetOrCreate();
                }
            }
            return atlasInstance;
        }
    }
}

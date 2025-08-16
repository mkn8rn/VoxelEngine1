using MVGE.Gameplay;
using MVGE.Graphics;
using MVGE.Graphics.Terrain;
using MVGE.World;
using MVGE.World.Terrain;
using MVGE.Middleware;
using MVGE.Models;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace MVGE
{
    public enum GameMode
    {
        Menu,
        Survival,
        Campaign
    }

    public struct LaunchSettings
    {
        // Window settings
        public int windowWidth;
        public int windowHeight;

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

    public class GameManager : GameWindow
    {
        // render pipeline
        WorldManager world = null!;
        BlockTextureAtlas blockTextureAtlas = null!;
        ShaderProgram shaderProgram = null!;

        // data loaders
        TerrainDataLoader blockDataLoader = null!;

        // Game state
        public static GameMode gameMode = GameMode.Menu;
        public static LaunchSettings settings = new LaunchSettings();

        // player
        Player player = null!;
        private readonly CommandLineFlags flags;

        public GameManager() : base(GameWindowSettings.Default, NativeWindowSettings.Default)
        {
            this.flags = ArgumentsMiddleware.Flags;
            // Load the settings
            SetDefaultSettings();

            // center window on monitor
            this.CenterWindow(new Vector2i(GameManager.settings.windowWidth, GameManager.settings.windowHeight));
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            GL.Viewport(0, 0, e.Width, e.Height);
            GameManager.settings.windowWidth = e.Width;
            GameManager.settings.windowHeight = e.Height;
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            // Select game folder
            string selectedGameFolder = SelectGameFolder(flags.game);
            LoadGameDefaultSettings(selectedGameFolder);

            // Initialize the Data Loaders
            Console.WriteLine("Data loaders initializing.");
            this.blockDataLoader = new TerrainDataLoader() ?? throw new Exception("blockDataLoader is null");

            // Initialize the Texture Atlases
            Console.WriteLine("Texture atlases initializing.");
            this.blockTextureAtlas = new BlockTextureAtlas() ?? throw new Exception("blockTextureAtlas is null");
            MeshRender.terrainTextureAtlas = this.blockTextureAtlas ?? throw new Exception("terrainTextureAtlas is null");

            // Initialize the Shaders
            Console.WriteLine("Shaders initializing.");
            shaderProgram = new ShaderProgram("Default.vert", "Default.frag") ?? throw new Exception("shaderProgram is null");

            // Bind the texture atlas
            int textureLocation = GL.GetUniformLocation(shaderProgram.ID, "textureAtlas");
            GL.Uniform1(textureLocation, 0);
            this.blockTextureAtlas.Bind();

            // Initialize the World rendering
            world = new WorldManager() ?? throw new Exception("world is null");

            // Enabling OpenGL options
            Console.WriteLine("Enabling OpenGL options.");
            GL.Enable(EnableCap.DepthTest);
            GL.FrontFace(FrontFaceDirection.Cw);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);

            // Garbage collection
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Initialize the Player (and its Camera)
            Console.WriteLine("Initializing player.");
            player = new Player() ?? throw new Exception("player is null");
            CursorState = CursorState.Grabbed;
        }

        protected override void OnUnload()
        {
            base.OnUnload();
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            GL.ClearColor(0.3f, 0f, 0.6f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // transformation matrices
            Matrix4 model = Matrix4.Identity;
            Matrix4 view = player.camera.GetViewMatrix();
            Matrix4 projection = player.camera.GetProjectionMatrix((float)settings.windowWidth / settings.windowHeight);

            int modelLocation = GL.GetUniformLocation(shaderProgram.ID, "model");
            int viewLocation = GL.GetUniformLocation(shaderProgram.ID, "view");
            int projectionLocation = GL.GetUniformLocation(shaderProgram.ID, "projection");

            GL.UniformMatrix4(modelLocation, true, ref model);
            GL.UniformMatrix4(viewLocation, true, ref view);
            GL.UniformMatrix4(projectionLocation, true, ref projection);

            world.Render(shaderProgram);

            Context.SwapBuffers();

            base.OnRenderFrame(args);
        }

        protected override void OnUpdateFrame(FrameEventArgs args)
        {
            MouseState mouse = MouseState;
            KeyboardState input = KeyboardState;

            base.OnUpdateFrame(args);
            player.Update(input, mouse, args);
        }

        private void SetDefaultSettings()
        {
            var path = Path.GetDirectoryName(typeof(GameManager).Assembly.Location)!;
            settings.gamesDirectory = Path.Combine(path, "Games");
        }

        private void LoadGameDefaultSettings(string gameDirectory)
        {
            string defaultsPath = Path.Combine(gameDirectory, "Defaults.txt");
            if (!File.Exists(defaultsPath))
                throw new Exception($"Defaults.txt not found in {gameDirectory}");

            var lines = File.ReadAllLines(defaultsPath);
            var dict = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                    dict[parts[0].Trim()] = parts[1].Trim();
            }

            string GetSetting(string key)
            {
                if (!dict.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                    throw new Exception($"Missing or empty setting: {key}");
                return value;
            }

            settings.windowWidth = int.Parse(GetSetting("windowWidth"));
            settings.windowHeight = int.Parse(GetSetting("windowHeight"));
            settings.blockTileWidth = int.Parse(GetSetting("blockTileWidth"));
            settings.blockTileHeight = int.Parse(GetSetting("blockTileHeight"));
            settings.textureFileExtension = GetSetting("textureFileExtension");
            settings.lod1RenderDistance = int.Parse(GetSetting("lod1RenderDistance"));
            settings.lod2RenderDistance = int.Parse(GetSetting("lod2RenderDistance"));
            settings.lod3RenderDistance = int.Parse(GetSetting("lod3RenderDistance"));
            settings.lod4RenderDistance = int.Parse(GetSetting("lod4RenderDistance"));
            settings.lod5RenderDistance = int.Parse(GetSetting("lod5RenderDistance"));
            settings.entityLoadRange = int.Parse(GetSetting("entityLoadRange"));
            settings.entitySpawnMaxRange = int.Parse(GetSetting("entitySpawnMaxRange"));
            settings.entityDespawnMaxRange = int.Parse(GetSetting("entityDespawnMaxRange"));
            settings.loadedGameSettingsDirectory = Path.Combine(gameDirectory, GetSetting("loadedGameSettingsDirectory"));
            settings.assetsBaseBlockTexturesDirectory = Path.Combine(gameDirectory, GetSetting("assetsBaseBlockTexturesDirectory"));
            settings.assetsBlockTexturesDirectory = Path.Combine(gameDirectory, GetSetting("assetsBlockTexturesDirectory"));
            settings.dataBlockTypesDirectory = Path.Combine(gameDirectory, GetSetting("dataBlockTypesDirectory"));
            settings.dataBiomeTypesDirectory = Path.Combine(gameDirectory, GetSetting("dataBiomeTypesDirectory"));
            settings.savesWorldDirectory = Path.Combine(gameDirectory, GetSetting("savesWorldDirectory"));
            settings.savesCharactersDirectory = Path.Combine(gameDirectory, GetSetting("savesCharactersDirectory"));
            settings.loadedGameDirectory = gameDirectory;
        }

        private string SelectGameFolder(string? autoGameName = null)
        {
            string[] gameFolders = Directory.GetDirectories(settings.gamesDirectory);
            if (gameFolders.Length == 1)
            {
                string onlyGameName = Path.GetFileName(gameFolders[0]);
                Console.WriteLine($"Only one game: '{onlyGameName}' detected. Skipping game selection.");
                return gameFolders[0];
            }
            var defaultIndex = Array.FindIndex(gameFolders, f => Path.GetFileName(f).Equals("Default", StringComparison.OrdinalIgnoreCase));
            List<string> orderedFolders = new List<string>();
            if (defaultIndex != -1)
            {
                orderedFolders.Add(gameFolders[defaultIndex]);
                for (int i = 0; i < gameFolders.Length; i++)
                {
                    if (i != defaultIndex) orderedFolders.Add(gameFolders[i]);
                }
            }
            else
            {
                orderedFolders.AddRange(gameFolders);
            }

            if (!string.IsNullOrEmpty(autoGameName))
            {
                var match = orderedFolders.FirstOrDefault(f => Path.GetFileName(f).Equals(autoGameName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    Console.WriteLine($"Auto-selecting game: '{autoGameName}' via command-line flag.");
                    return match;
                }
                else
                {
                    Console.WriteLine($"Game '{autoGameName}' not found. Proceeding with manual selection.");
                }
            }

            Console.WriteLine("Select a game to load:");
            for (int i = 0; i < orderedFolders.Count; i++)
            {
                Console.WriteLine($"{i + 1}: {Path.GetFileName(orderedFolders[i])}");
            }
            while (true)
            {
                Console.Write($"Enter a number (1-{orderedFolders.Count}): ");
                string? input = Console.ReadLine();
                if (int.TryParse(input, out int selectedIndex) && selectedIndex >= 1 && selectedIndex <= orderedFolders.Count)
                {
                    return orderedFolders[selectedIndex - 1];
                }
                Console.WriteLine("Invalid input. Please try again.");
            }
        }
    }
}

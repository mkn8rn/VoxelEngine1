using MVGE.Gameplay;
using MVGE.Graphics;
using MVGE.Graphics.Terrain;
using MVGE.World;
using MVGE.World.Terrain;
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
        WorldManager world;
        BlockTextureAtlas blockTextureAtlas;
        ShaderProgram shaderProgram;

        // data loaders
        TerrainDataLoader blockDataLoader;

        // Game state
        public static GameMode gameMode = GameMode.Menu;
        public static LaunchSettings settings = new LaunchSettings();

        // player
        Player player;

        public GameManager() : base(GameWindowSettings.Default, NativeWindowSettings.Default)
        {
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

            //todo: list available games in /Games
            //and let the user select one to load
            Console.WriteLine("Loading DefaultGame.");

            // Initialize the Data Loaders
            Console.WriteLine("Data loaders initializing.");
            this.blockDataLoader = new TerrainDataLoader();

            // Initialize the Texture Atlases
            Console.WriteLine("Texture atlases initializing.");
            this.blockTextureAtlas = new BlockTextureAtlas();
            MeshRender.terrainTextureAtlas = this.blockTextureAtlas;

            // Initialize the Shaders
            Console.WriteLine("Shaders initializing.");
            shaderProgram = new ShaderProgram("Default.vert", "Default.frag");

            // Bind the texture atlas
            int textureLocation = GL.GetUniformLocation(shaderProgram.ID, "textureAtlas");
            GL.Uniform1(textureLocation, 0);
            this.blockTextureAtlas.Bind();

            // Initialize the World rendering
            world = new WorldManager();

            // Enabling OpenGL options
            Console.WriteLine("Enabling OpenGL options.");
            GL.Enable(EnableCap.DepthTest);
            GL.FrontFace(FrontFaceDirection.Cw);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            // Garbage collection
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Initialize the Player (and its Camera)
            Console.WriteLine("Initializing player.");
            player = new Player();
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
            settings.windowWidth = 1280;
            settings.windowHeight = 720;

            settings.blockTileWidth = 32;
            settings.blockTileHeight = 32;
            settings.textureFileExtension = ".png";

            settings.lod1RenderDistance = 6;
            settings.lod2RenderDistance = 18;
            settings.lod3RenderDistance = 54;
            settings.lod4RenderDistance = 162;
            settings.lod5RenderDistance = 486;

            settings.entityLoadRange = 12;
            settings.entitySpawnMaxRange = 12;
            settings.entityDespawnMaxRange = 12;

            settings.gamesDirectory = "../../../Games";
            settings.loadedGameDirectory = "../../../Games/DefaultGame";
            settings.loadedGameSettingsDirectory = "../../../Games/DefaultGame/Settings";

            settings.assetsBaseBlockTexturesDirectory = "../../../Games/DefaultGame/Assets/Textures/Blocks/Base/";
            settings.assetsBlockTexturesDirectory = "../../../Games/DefaultGame/Assets/Textures/Blocks/";
            settings.dataBlockTypesDirectory = "../../../Games/DefaultGame/Data/Blocks/Types/";
            settings.dataBiomeTypesDirectory = "../../../Games/DefaultGame/Data/Biomes/";
            settings.savesWorldDirectory = "../../../Games/DefaultGame/Saves/Worlds/";
            settings.savesCharactersDirectory = "../../../Games/DefaultGame/Saves/Characters/";
        }
    }
}

using MVoxelEngine1.Application.Gameplay;
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
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Graphics;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using System.Diagnostics;

namespace MVoxelEngine1.Application
{
    public enum GameMode
    {
        Menu,
        Survival,
        Campaign
    }

    public class Window : GameWindow
    {
        // render pipeline
        World world = null!;
        BlockTextureAtlas blockTextureAtlas = null!;
        ShaderProgram shaderProgram = null!;

        // data loaders
        TerrainLoader blockDataLoader = null!;

        // player
        Player player = null!;

        // settings
        int windowWidth;
        int windowHeight;
        bool benchmarkWritten;

        public Window() : base(GameWindowSettings.Default, NativeWindowSettings.Default)
        {
            // Load the settings
            LoadEnvironmentDefaultSettings();

            if (FlagManager.flags.windowWidth == null || FlagManager.flags.windowHeight == null)
            {
                throw new Exception("windowWidth or windowHeight flag is null.");
            }
            windowWidth = FlagManager.flags.windowWidth.Value;
            windowHeight = FlagManager.flags.windowHeight.Value;
            // center window on monitor
            CenterWindow(new Vector2i(windowWidth, windowHeight));
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            GL.Viewport(0, 0, e.Width, e.Height);
            windowWidth = e.Width;
            windowHeight = e.Height;
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            blockDataLoader = GameDataStartup.Load();

            // Initialize the Texture Atlases
            Console.WriteLine("Texture atlases initializing.");
            blockTextureAtlas = new BlockTextureAtlas() ?? throw new Exception("blockTextureAtlas is null");
            ChunkRender.terrainTextureAtlas = blockTextureAtlas ?? throw new Exception("terrainTextureAtlas is null");

            // Initialize the Shaders
            Console.WriteLine("Shaders initializing.");
            shaderProgram = new ShaderProgram("Default.vert", "Default.frag") ?? throw new Exception("shaderProgram is null");

            // Bind the texture atlas
            int textureLocation = GL.GetUniformLocation(shaderProgram.ID, "textureAtlas");
            GL.Uniform1(textureLocation, 0);
            blockTextureAtlas.Bind();

            // Initialize the World rendering
            StartupPerformanceRecorder.RecordSeedAccepted();
            world = new World() ?? throw new Exception("world is null");

            // Initialize the Player (and its Camera)
            Console.WriteLine("Initializing player.");
            player = new Player(world) ?? throw new Exception("player is null");
            // Initialize player chunk position explicitly (already done in Player ctor but keep explicit for clarity)
            world.PlayerChunkPosition = (0,0,0);

            // Enabling OpenGL options
            Console.WriteLine("Enabling OpenGL options.");
            GL.Enable(EnableCap.DepthTest);
            GL.FrontFace(FrontFaceDirection.Cw);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            CursorState = CursorState.Grabbed;
        }

        protected override void OnUnload()
        {
            world?.Dispose();
            base.OnUnload();
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            long renderStart = Stopwatch.GetTimestamp();

            GL.ClearColor(0.3f, 0f, 0.6f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // transformation matrices
            Matrix4 model = Matrix4.Identity;
            Matrix4 view = player.camera.GetViewMatrix();
            Matrix4 projection = player.camera.GetProjectionMatrix((float)windowWidth / windowHeight);

            int modelLocation = GL.GetUniformLocation(shaderProgram.ID, "model");
            int viewLocation = GL.GetUniformLocation(shaderProgram.ID, "view");
            int projectionLocation = GL.GetUniformLocation(shaderProgram.ID, "projection");

            GL.UniformMatrix4(modelLocation, true, ref model);
            GL.UniformMatrix4(viewLocation, true, ref view);
            GL.UniformMatrix4(projectionLocation, true, ref projection);

            world.Render(shaderProgram);
            bool gpuStreamingStarted = StartupPerformanceRecorder.HasGpuStreamingStarted;

            Context.SwapBuffers();

            StartupPerformanceRecorder.RecordCameraAppearance();
            if (gpuStreamingStarted)
                StartupPerformanceRecorder.RecordFirstRender(Stopwatch.GetElapsedTime(renderStart));

            if (!benchmarkWritten &&
                !string.IsNullOrWhiteSpace(FlagManager.flags.benchmarkOutput) &&
                StartupPerformanceRecorder.IsComplete)
            {
                StartupPerformanceRecorder.WriteSnapshot(FlagManager.flags.benchmarkOutput);
                benchmarkWritten = true;
                Console.WriteLine($"Benchmark metrics written to {Path.GetFullPath(FlagManager.flags.benchmarkOutput)}");
                Close();
            }

            base.OnRenderFrame(args);
        }

        protected override void OnUpdateFrame(FrameEventArgs args)
        {
            MouseState mouse = MouseState;
            KeyboardState input = KeyboardState;

            base.OnUpdateFrame(args);
            player.Update(input, mouse, args);
        }

        private void LoadEnvironmentDefaultSettings()
        {
            ProgramFlags flags = FlagManager.flags;
            if (flags.windowWidth == null)
                throw new Exception("windowWidth flag is null.");
            if (flags.windowHeight == null)
                throw new Exception("windowHeight flag is null.");

            windowWidth = flags.windowWidth.Value;
            windowHeight = flags.windowHeight.Value;
        }
    }
}

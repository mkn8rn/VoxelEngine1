using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Application.Gameplay
{
    public enum PlayerState
    {
        Spectator,
        Alive
    }

    internal class Player
    {
        private PlayerState playerMode;
        public Vector3 position = Vector3.Zero;
        public Vector3 velocity = Vector3.Zero;
        public Vector3 direction = -Vector3.UnitZ; // Facing forward

        internal const float MovementSpeed = 60f;
        private float jumpStrength = 5f;

        public Camera camera;

        private readonly World world; // reference to world for chunk scheduling

        // Cache last reported chunk to avoid redundant property sets
        private int lastChunkX = int.MinValue;
        private int lastChunkY = int.MinValue;
        private int lastChunkZ = int.MinValue;

        public Player(World world)
        {
            this.world = world;
            playerMode = PlayerState.Alive;
            camera = new Camera(position);
            UpdateWorldChunkPosition(); // initialize
        }

        public void Update(KeyboardState input, MouseState mouse, FrameEventArgs args)
        {
            HandleInput(input, mouse, args);

            // Only update direction, not position
            camera.UpdateDirection(direction);

            UpdateWorldChunkPosition();
        }

        public void Update(PlayerInputKeys input, double elapsedSeconds)
        {
            ApplyMovement(input, elapsedSeconds);
            camera.UpdateDirection(direction);
            UpdateWorldChunkPosition();
        }

        private void UpdateWorldChunkPosition()
        {
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int wx = (int)MathF.Floor(camera.position.X);
            int wy = (int)MathF.Floor(camera.position.Y);
            int wz = (int)MathF.Floor(camera.position.Z);
            int cx = FloorDiv(wx, sizeX);
            int cy = FloorDiv(wy, sizeY);
            int cz = FloorDiv(wz, sizeZ);
            if (cx != lastChunkX || cy != lastChunkY || cz != lastChunkZ)
            {
                world.PlayerChunkPosition = (cx, cy, cz);
                lastChunkX = cx; lastChunkY = cy; lastChunkZ = cz;

                Console.WriteLine($"Player chunk position updated to: ({cx}, {cy}, {cz})");
            }
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

        private void HandleInput(KeyboardState input, MouseState mouse, FrameEventArgs args)
        {
            PlayerInputKeys keys = PlayerInputKeys.None;
            if (input.IsKeyDown(Keys.W)) keys |= PlayerInputKeys.W;
            if (input.IsKeyDown(Keys.A)) keys |= PlayerInputKeys.A;
            if (input.IsKeyDown(Keys.S)) keys |= PlayerInputKeys.S;
            if (input.IsKeyDown(Keys.D)) keys |= PlayerInputKeys.D;
            if (input.IsKeyDown(Keys.Space)) keys |= PlayerInputKeys.Space;
            if (input.IsKeyDown(Keys.LeftShift)) keys |= PlayerInputKeys.LeftShift;

            ApplyMovement(keys, args.Time);

            float deltaX = mouse.Delta.X;
            float deltaY = mouse.Delta.Y;
            camera.ProcessMouseMovement(deltaX, deltaY);
            direction = camera.front;
        }

        private void ApplyMovement(PlayerInputKeys input, double elapsedSeconds)
        {
            if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

            float cameraSpeed = MovementSpeed * (float)elapsedSeconds;
            Vector3 moveDirection = Vector3.Zero;

            if ((input & PlayerInputKeys.W) != 0)
                moveDirection += camera.front;
            if ((input & PlayerInputKeys.S) != 0)
                moveDirection -= camera.front;

            Vector3 right = Vector3.Normalize(Vector3.Cross(camera.front, camera.up));
            if ((input & PlayerInputKeys.A) != 0)
                moveDirection -= right;
            if ((input & PlayerInputKeys.D) != 0)
                moveDirection += right;

            if ((input & PlayerInputKeys.Space) != 0)
                moveDirection += camera.up;
            if ((input & PlayerInputKeys.LeftShift) != 0)
                moveDirection -= camera.up;

            if (moveDirection.LengthSquared > 0)
                moveDirection = Vector3.Normalize(moveDirection);

            camera.position += moveDirection * cameraSpeed;
        }
    }
}

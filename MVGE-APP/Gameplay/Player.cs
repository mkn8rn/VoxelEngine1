using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVGE_INF.Managers;

namespace MVGE_GEN.Gameplay
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

        private float SPEED = 60f;
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
            float cameraSpeed = SPEED * (float)args.Time;
            Vector3 moveDirection = Vector3.Zero;

            // Forward/backward
            if (input.IsKeyDown(Keys.W))
                moveDirection += camera.front;
            if (input.IsKeyDown(Keys.S))
                moveDirection -= camera.front;

            // Left/right
            Vector3 right = Vector3.Normalize(Vector3.Cross(camera.front, camera.up));
            if (input.IsKeyDown(Keys.A))
                moveDirection -= right;
            if (input.IsKeyDown(Keys.D))
                moveDirection += right;

            // Up/down
            if (input.IsKeyDown(Keys.Space))
                moveDirection += camera.up;
            if (input.IsKeyDown(Keys.LeftShift))
                moveDirection -= camera.up;

            if (moveDirection.LengthSquared > 0)
                moveDirection = Vector3.Normalize(moveDirection);

            camera.position += moveDirection * cameraSpeed;

            // Mouse look (direction)
            float deltaX = mouse.Delta.X;
            float deltaY = mouse.Delta.Y;
            camera.ProcessMouseMovement(deltaX, deltaY);
            direction = camera.front;
        }
    }
}

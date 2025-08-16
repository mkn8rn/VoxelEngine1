using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE.Gameplay
{
    public enum PlayerState
    {
        Spectator,
        Alive
    }

    internal class Player
    {
        private PlayerState gameMode;
        public Vector3 position = Vector3.Zero;
        public Vector3 velocity = Vector3.Zero;
        public Vector3 direction = -Vector3.UnitZ; // Facing forward

        private float SPEED = 12f;
        private float jumpStrength = 5f;
        private bool isGrounded = true;

        public Camera camera;

        public Player()
        {
            gameMode = PlayerState.Alive;
            camera = new Camera(position);
        }

        public void Update(KeyboardState input, MouseState mouse, FrameEventArgs args)
        {
            HandleInput(input, mouse, args);

            // Only update direction, not position
            camera.UpdateDirection(direction);
        }

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

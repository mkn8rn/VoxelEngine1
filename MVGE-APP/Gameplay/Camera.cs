using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GEN.Gameplay
{
    internal class Camera
    {
        public Vector3 position = Vector3.Zero;
        public Vector3 front = -Vector3.UnitZ;
        public Vector3 up = Vector3.UnitY;
        private float pitch;
        private float yaw = -90.0f;
        private float sensitivity = 0.1f;

        public Camera(Vector3 startPosition)
        {
            position = startPosition;
        }

        public Matrix4 GetViewMatrix()
        {
            return Matrix4.LookAt(position, position + front, up);
        }

        public Matrix4 GetProjectionMatrix(float aspectRatio)
        {
            return Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45.0f), aspectRatio, 0.1f, 1000.0f);
        }

        public void UpdatePosition(Vector3 playerPosition)
        {
            position = playerPosition + new Vector3(0, 1.8f, 0); // Slight offset for eye height
        }

        public void UpdateDirection(Vector3 playerDirection)
        {
            front = playerDirection;
        }

        public void ProcessMouseMovement(float deltaX, float deltaY)
        {
            yaw += deltaX * sensitivity;
            pitch -= deltaY * sensitivity;

            // Clamp pitch
            pitch = MathHelper.Clamp(pitch, -89.0f, 89.0f);

            // Update front vector
            front.X = MathF.Cos(MathHelper.DegreesToRadians(pitch)) * MathF.Cos(MathHelper.DegreesToRadians(yaw));
            front.Y = MathF.Sin(MathHelper.DegreesToRadians(pitch));
            front.Z = MathF.Cos(MathHelper.DegreesToRadians(pitch)) * MathF.Sin(MathHelper.DegreesToRadians(yaw));
            front = Vector3.Normalize(front);
        }
    }
}

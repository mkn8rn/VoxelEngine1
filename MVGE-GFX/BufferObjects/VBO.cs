using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.BufferObjects
{
    // Render passes supported by the renderer. Opaque = default geometry, Transparent = alpha blended geometry.
    public enum RenderPass : byte { Opaque = 0, Transparent = 1 }

    public class VBO
    {
        // OpenGL buffer object id (immutable after creation)
        public int ID;
        // Pass this buffer logically belongs to (helps higher layers organize attribute sets per pass).
        public RenderPass Pass { get; }

        // Byte data constructor (defaults to Opaque pass)
        public VBO(List<byte> data, RenderPass pass = RenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, ID);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Count, data.ToArray(), BufferUsageHint.StaticDraw);
        }

        // Vector2 data constructor (defaults to Opaque pass)
        public VBO(List<Vector2> data, RenderPass pass = RenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, ID);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Count * Vector2.SizeInBytes, data.ToArray(), BufferUsageHint.StaticDraw);
        }

        // Vector3 data constructor (defaults to Opaque pass)
        public VBO(List<Vector3> data, RenderPass pass = RenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, ID);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Count * Vector3.SizeInBytes, data.ToArray(), BufferUsageHint.StaticDraw);
        }

        // Raw byte[] constructor with explicit length (defaults to Opaque pass)
        public VBO(byte[] data, int length, RenderPass pass = RenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, ID);
            GL.BufferData(BufferTarget.ArrayBuffer, length, data, BufferUsageHint.StaticDraw);
        }

        // Bind this VBO to GL_ARRAY_BUFFER target.
        public void Bind()
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, ID);
        }

        // Unbind any VBO from GL_ARRAY_BUFFER target.
        public void Unbind()
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        // Destroy the underlying GL buffer.
        public void Delete()
        {
            GL.DeleteBuffer(ID);
        }
    }
}

using OpenTK.Graphics.OpenGL4;
using System.Collections.Generic;

namespace MVGE_GFX.BufferObjects
{
    // Render passes supported by the renderer. Opaque = default geometry, Transparent = alpha blended geometry.
    public enum IndexRenderPass : byte { Opaque = 0, Transparent = 1 }

    public class IBO
    {
        // OpenGL element buffer id and element count
        public int ID;
        public int Count { get; }
        // Pass this index buffer is associated with.
        public IndexRenderPass Pass { get; }

        // 32-bit indices (defaults to Opaque pass)
        public IBO(List<uint> data, IndexRenderPass pass = IndexRenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            Count = data.Count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, Count * sizeof(uint), data.ToArray(), BufferUsageHint.StaticDraw);
        }

        // 16-bit indices (defaults to Opaque pass)
        public IBO(List<ushort> data, IndexRenderPass pass = IndexRenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            Count = data.Count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, Count * sizeof(ushort), data.ToArray(), BufferUsageHint.StaticDraw);
        }

        // Array overload (uint) (defaults to Opaque pass)
        public IBO(uint[] data, int count, IndexRenderPass pass = IndexRenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            Count = count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, count * sizeof(uint), data, BufferUsageHint.StaticDraw);
        }

        // Array overload (ushort) (defaults to Opaque pass)
        public IBO(ushort[] data, int count, IndexRenderPass pass = IndexRenderPass.Opaque)
        {
            Pass = pass;
            ID = GL.GenBuffer();
            Count = count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, count * sizeof(ushort), data, BufferUsageHint.StaticDraw);
        }

        // Bind this index buffer.
        public void Bind() => GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
        // Unbind any index buffer.
        public void Unbind() => GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
        // Delete the GL buffer.
        public void Delete() => GL.DeleteBuffer(ID);
    }
}

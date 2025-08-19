using OpenTK.Graphics.OpenGL4;
using System.Collections.Generic;

namespace MVGE_GFX.BufferObjects
{
    public class IBO
    {
        public int ID;
        public int Count { get; }

        public IBO(List<uint> data)
        {
            ID = GL.GenBuffer();
            Count = data.Count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, Count * sizeof(uint), data.ToArray(), BufferUsageHint.StaticDraw);
        }
        public IBO(List<ushort> data)
        {
            ID = GL.GenBuffer();
            Count = data.Count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, Count * sizeof(ushort), data.ToArray(), BufferUsageHint.StaticDraw);
        }

        // New overloads for pooled buffers
        public IBO(uint[] data, int count)
        {
            ID = GL.GenBuffer();
            Count = count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, count * sizeof(uint), data, BufferUsageHint.StaticDraw);
        }
        public IBO(ushort[] data, int count)
        {
            ID = GL.GenBuffer();
            Count = count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
            GL.BufferData(BufferTarget.ElementArrayBuffer, count * sizeof(ushort), data, BufferUsageHint.StaticDraw);
        }

        public void Bind() => GL.BindBuffer(BufferTarget.ElementArrayBuffer, ID);
        public void Unbind() => GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
        public void Delete() => GL.DeleteBuffer(ID);
    }
}

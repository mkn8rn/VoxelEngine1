using OpenTK.Graphics.OpenGL4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.BufferObjects
{
    public class VAO
    {
        // OpenGL vertex array object id
        public int ID;
        public VAO()
        {
            ID = GL.GenVertexArray();
            GL.BindVertexArray(ID);
        }

        // Link a floating-point / normalized attribute to this VAO for a given render pass.
        // The pass parameter is informational only (VAO state itself is pass-agnostic) and allows
        // calling code to express intent clearly when organizing opaque vs transparent attribute sets.
        public void LinkToVAO(int location, int numComponents, VertexAttribPointerType type, bool normalized, VBO vbo)
        {
            Bind();
            vbo.Bind();
            GL.VertexAttribPointer(location, numComponents, type, normalized, 0, 0);
            GL.EnableVertexAttribArray(location);
            Unbind();
        }

        // Link a floating-point / normalized attribute with explicit stride and offset (bytes).
        // Use when attribute elements are padded for alignment (e.g., 4-byte stride for 3-byte values).
        public void LinkToVAO(int location, int numComponents, VertexAttribPointerType type, bool normalized, VBO vbo, int stride, int offset)
        {
            Bind();
            vbo.Bind();
            GL.VertexAttribPointer(location, numComponents, type, normalized, stride, offset);
            GL.EnableVertexAttribArray(location);
            Unbind();
        }

        // Link an integer attribute (no normalization) to this VAO.
        public void LinkIntegerToVAO(int location, int numComponents, VertexAttribIntegerType type, VBO vbo)
        {
            Bind();
            vbo.Bind();
            GL.VertexAttribIPointer(location, numComponents, type, 0, IntPtr.Zero);
            GL.EnableVertexAttribArray(location);
            Unbind();
        }

        // Set instancing divisor for an attribute location (used for per-instance data in both passes).
        public void SetDivisor(int location, int divisor)
        {
            Bind();
            GL.VertexAttribDivisor(location, divisor);
            Unbind();
        }

        // enable/disable a vertex attribute array on this VAO.
        public void SetAttribEnabled(int location, bool enabled)
        {
            Bind();
            if (enabled) GL.EnableVertexAttribArray(location);
            else GL.DisableVertexAttribArray(location);
            Unbind();
        }

        // Bind this VAO.
        public void Bind()
        {
            GL.BindVertexArray(ID);
        }

        // Unbind any VAO.
        public void Unbind()
        {
            GL.BindVertexArray(0);
        }

        // Delete this VAO.
        public void Delete()
        {
            GL.DeleteVertexArray(ID);
        }
    }
}

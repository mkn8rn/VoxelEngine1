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
        public int ID;
        public VAO()
        {
            ID = GL.GenVertexArray();
            GL.BindVertexArray(ID);
        }

        public void LinkToVAO(int location, int numComponents, VertexAttribPointerType type, bool normalized, VBO vbo)
        {
            Bind();
            vbo.Bind();
            GL.VertexAttribPointer(location, numComponents, type, normalized, 0, 0);
            GL.EnableVertexAttribArray(location);
            Unbind();
        }

        public void Bind()
        {
            GL.BindVertexArray(ID);
        }

        public void Unbind()
        {
            GL.BindVertexArray(0);
        }

        public void Delete()
        {
            GL.DeleteVertexArray(ID);
        }
    }
}

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE.Graphics
{
    internal class ShaderProgram
    {
        public int ID;
        public ShaderProgram(string vertexShaderFilePath, string fragmentShaderFilePath)
        {
            ID = GL.CreateProgram();
            int vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, LoadShaderSource(vertexShaderFilePath));
            GL.CompileShader(vertexShader);
            CheckShaderCompileStatus(vertexShader);

            int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragmentShader, LoadShaderSource(fragmentShaderFilePath));
            GL.CompileShader(fragmentShader);
            CheckShaderCompileStatus(fragmentShader);

            GL.AttachShader(ID, vertexShader);
            GL.AttachShader(ID, fragmentShader);

            GL.LinkProgram(ID);
            CheckProgramLinkStatus(ID);

            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
        }

        private void CheckShaderCompileStatus(int shader)
        {
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
            if (status == (int)All.False)
            {
                string infoLog = GL.GetShaderInfoLog(shader);
                Console.WriteLine($"Shader compilation failed: {infoLog}");
            }
        }

        private void CheckProgramLinkStatus(int program)
        {
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);
            if (status == (int)All.False)
            {
                string infoLog = GL.GetProgramInfoLog(program);
                Console.WriteLine($"Program linking failed: {infoLog}");
            }
        }

        public void Bind()
        {
            GL.UseProgram(ID);
        }

        public void Unbind()
        {
            GL.UseProgram(0);
        }

        public void Delete()
        {
            GL.DeleteShader(ID);
        }

        private int GetUniformLocation(string name)
        {
            int location = GL.GetUniformLocation(ID, name);
            if (location == -1)
            {
                Console.WriteLine($"Warning: Uniform '{name}' not found or not used in shader.");
            }
            return location;
        }

        public void SetUniform(string name, Vector3 value)
        {
            int location = GetUniformLocation(name);
            if (location != -1)
            {
                GL.Uniform3(location, value);
            }
        }
        public void SetUniform(string name, float value)
        {
            int location = GetUniformLocation(name);
            if (location != -1)
            {
                GL.Uniform1(location, value);
            }
        }

        public static string LoadShaderSource(string filePath)
        {
            string shaderSource = "";

            try
            {
                using (StreamReader reader = new StreamReader("../../../Graphics/Shaders/" + filePath))
                {
                    shaderSource = reader.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to load shader source file: " + e.Message);
            }

            return shaderSource;
        }
    }
}

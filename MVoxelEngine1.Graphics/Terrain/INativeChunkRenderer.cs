using MVoxelEngine1.Graphics.Models;

namespace MVoxelEngine1.Graphics.Terrain;

public interface INativeChunkRenderer : IDisposable
{
    void RenderOpaque(ShaderProgram program);

    void RenderTransparent(ShaderProgram program);
}

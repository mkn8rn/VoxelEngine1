namespace MVoxelEngine1.Infrastructure.Models;

public interface IPlayerChunkPositionSink
{
    (int cx, int cy, int cz) PlayerChunkPosition { get; set; }
}

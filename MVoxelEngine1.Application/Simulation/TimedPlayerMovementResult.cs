using System.Diagnostics;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Application.Simulation
{
    internal readonly record struct TimedPlayerMovementResult(long FrameIndex, double SimulationElapsedSeconds, double WallElapsedSeconds);
}

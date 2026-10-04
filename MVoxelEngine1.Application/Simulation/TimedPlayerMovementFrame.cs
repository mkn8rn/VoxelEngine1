using System.Diagnostics;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Application.Simulation
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal readonly record struct TimedPlayerMovementFrame(long FrameIndex, double SimulationElapsedSeconds, double WallElapsedSeconds, double DeltaSeconds, PlayerInputKeys Keys);
}

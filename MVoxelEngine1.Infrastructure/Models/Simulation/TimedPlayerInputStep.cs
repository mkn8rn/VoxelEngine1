using System.Globalization;

namespace MVoxelEngine1.Infrastructure.Models.Simulation
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public readonly record struct TimedPlayerInputStep(PlayerInputKeys Keys, double DurationSeconds);
}

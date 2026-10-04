using System.Globalization;

namespace MVoxelEngine1.Infrastructure.Models.Simulation
{
    public readonly record struct TimedPlayerInputStep(PlayerInputKeys Keys, double DurationSeconds);
}

using System.Diagnostics;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Application.Simulation
{
    internal readonly record struct TimedPlayerInputBoundary(bool Started, int StepIndex, TimedPlayerInputStep Step, double SimulationElapsedSeconds);
}

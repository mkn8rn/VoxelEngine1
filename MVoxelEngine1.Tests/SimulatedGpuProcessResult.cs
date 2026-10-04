using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MVoxelEngine1.Tests
{
    internal sealed record SimulatedGpuProcessResult(int ExitCode, string StandardOutput, string StandardError, bool WindowObserved, long PeakWorkingSetBytes);
}

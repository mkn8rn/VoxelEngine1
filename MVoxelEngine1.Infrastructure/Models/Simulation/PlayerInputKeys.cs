using System.Globalization;

namespace MVoxelEngine1.Infrastructure.Models.Simulation
{
    [Flags]
    public enum PlayerInputKeys
    {
        None = 0,
        W = 1 << 0,
        A = 1 << 1,
        S = 1 << 2,
        D = 1 << 3,
        Space = 1 << 4,
        LeftShift = 1 << 5
    }
}

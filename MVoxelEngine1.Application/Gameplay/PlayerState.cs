using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Application.Gameplay
{
    internal enum PlayerState
    {
        Spectator,
        Alive
    }
}

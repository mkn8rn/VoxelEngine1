using MVoxelEngine1.Application.Gameplay;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Graphics;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Diagnostics;
using System.Diagnostics;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Application
{
    internal enum GameMode
    {
        Menu,
        Survival,
        Campaign
    }
}

using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MVoxelEngine1.Graphics.Textures
{
    public enum BlockTextureAtlasUploadMode
    {
        OpenGl,
        SimulatedGpuUpload
    }
}

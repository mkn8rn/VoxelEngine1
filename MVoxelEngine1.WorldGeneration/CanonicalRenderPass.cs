using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MVoxelEngine1.Graphics.Models;
using MVoxelEngine1.WorldGeneration.Native;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.WorldGeneration
{
#pragma warning disable CA1028 // Retain the existing one-byte enum contract used by block, face and render-pass data; changing the underlying type would alter that contract.
    public enum CanonicalRenderPass : byte
#pragma warning restore CA1028
    {
        Opaque = 0,
        Transparent = 1
    }
}

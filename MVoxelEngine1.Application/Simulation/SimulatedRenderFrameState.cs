using System.Text.Json;
using System.Buffers;
using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using MVoxelEngine1.Application.Gameplay;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Simulation;
using MVoxelEngine1.WorldGeneration;
using MVoxelEngine1.WorldGeneration.Native;
using OpenTK.Mathematics;

namespace MVoxelEngine1.Application.Simulation
{
    internal sealed class SimulatedRenderFrameState
    {
        public required long FrameIndex { get; init; }
        public required IReadOnlyList<NativeChunkRenderPacketDescriptor> OpaquePassChunks { get; init; }
        public required IReadOnlyList<NativeChunkRenderPacketDescriptor> TransparentPassChunks { get; init; }
    }
}

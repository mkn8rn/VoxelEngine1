using System.Runtime.ExceptionServices;
using MVoxelEngine1.Graphics;
using MVoxelEngine1.Graphics.Terrain;
using MVoxelEngine1.Graphics.Textures;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models;
using OpenTK.Graphics.OpenGL4;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal delegate INativeChunkRenderer? NativeChunkRendererFactory(in NativeChunkRenderPacketDescriptor descriptor, ReadOnlySpan<uint> opaqueWords, ReadOnlySpan<uint> transparentWords);

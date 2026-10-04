using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal enum NativeGtrtFailureCode : int
{
    None = 0,
    InvalidGenerationClaim = 1,
    InvalidGenerationCompletion = 2,
    InvalidMeshDependency = 3,
    MeshReadyQueueFull = 4,
    InvalidMeshClaim = 5,
    InvalidMeshCompletion = 6,
    InvalidGenerationWorkspace = 7,
    InvalidProfileGeneration = 8,
    InvalidTerrainQuery = 9,
    InvalidMeshWorkspace = 10,
    PacketStorageExhausted = 11,
    InvalidPacketPublication = 12,
    InvalidGeneratedMesh = 13,
    InvalidPacketActivation = 14,
    InvalidPacketRetirement = 15,
    InvalidPacketRecycle = 16,
    InvalidMeshCancellation = 17,
    InvalidGenerationCancellation = 18,
    InvalidSessionReset = 19,
    InvalidMaterializedTerrain = 20,
    MaterializedChunkStorageExhausted = 21,
    MaterializedSectionStorageExhausted = 22
}

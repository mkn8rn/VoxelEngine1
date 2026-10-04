using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Diagnostics;
using MVoxelEngine1.Infrastructure.Models;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeGtrtSessionState
{
    internal long Seed;
    internal int SessionEpoch;
    internal int PublicationState;
    internal int GenerationCursor;
    internal int RemainingColumns;
    internal int MeshCursor;
    internal int RemainingChunks;
    internal int ReadyPacketCount;
    internal int FailureCode;
    internal int CancellationState;
    internal long MeshEnqueuePosition;
    internal long MeshDequeuePosition;
    internal int PacketWordCursor;
    internal int ClaimedMeshCount;
    internal int PacketRecycleState;
    internal int ClaimedGenerationCount;
    internal int DisposalState;
    internal int PacketConsumerCount;
    internal int CenterChunkX;
    internal int CenterChunkY;
    internal int CenterChunkZ;
    internal int MaterializedChunkCount;
    internal int MaterializedSectionCount;
    internal int MaterializedRawSectionCount;
    internal int MaterializedPaletteCursor;
    internal int MaterializedPackedWordCursor;
    internal int PlannedColumns;
    internal int PlannedMeshes;
    internal int RetainedColumns;
    internal int RetainedPackets;
    internal int TransactionOpen;
    internal int FreeRangeCount;
    internal int PacketAllocationLock;
}

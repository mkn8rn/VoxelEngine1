using System.Diagnostics;
using MVoxelEngine1.Infrastructure.Diagnostics;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
public readonly record struct NativeWorkerAllocationSample(int ManagedThreadId, int WorkerIndex, bool GeneratesTerrain, long WaitBytes, long WorkBytes, long CompletionBytes, long TotalBytes);

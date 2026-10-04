using System.Runtime;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
public sealed record NativeGtrtAllocationEvidence(string IntervalStart, string IntervalEnd, int CoordinatorManagedThreadId, long CoordinatorManagedBytes, long ProcessManagedBytes, int Generation0Collections, int Generation1Collections, int Generation2Collections, bool NoGcRegionCompleted, IReadOnlyList<NativeWorkerAllocationSample> Workers, NativePreUploadPacket FirstRequiredPacket, NativeSessionAllocationMetrics NativeStorage);

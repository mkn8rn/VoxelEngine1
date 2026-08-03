using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MVoxelEngine1.WorldGeneration.Terrain;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.Tests
{
    public sealed class NativeHeightWorkspacePerformanceTests
    {
        private const int MapCount = 729;
        private const int MapSide = 27;
        private const int MapSize = 160;
        private const int ValueCount = MapSize * MapSize;
        private const int WorkspaceLength = ValueCount * 2;
        private const int WorkerCount = 24;
        private const int SampleCount = 6;
        private const long Seed = 123456;
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        [Fact(Explicit = true, Timeout = 180_000)]
        [Trait("Category", "Performance")]
        [Trait("Resource", "CPU")]
        public void PublishedNamWorkspaceCompetesWithReusableManagedHeightBuffers()
        {
            HeightBatchEvidence managedWarmup = RunHeightBatch(
                "reusable-managed-array",
                RunManagedWorker);
            HeightBatchEvidence nativeWarmup = RunHeightBatch(
                "published-nam-workspace-0.1.2",
                RunNativeWorker);
            Assert.Equal(
                managedWarmup.WorkerChecksums,
                nativeWarmup.WorkerChecksums);

            var samples = new HeightPairEvidence[SampleCount];
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                bool nativeFirst = (sampleIndex & 1) != 0;
                HeightBatchEvidence first = nativeFirst
                    ? RunHeightBatch(
                        "published-nam-workspace-0.1.2",
                        RunNativeWorker)
                    : RunHeightBatch(
                        "reusable-managed-array",
                        RunManagedWorker);
                HeightBatchEvidence second = nativeFirst
                    ? RunHeightBatch(
                        "reusable-managed-array",
                        RunManagedWorker)
                    : RunHeightBatch(
                        "published-nam-workspace-0.1.2",
                        RunNativeWorker);
                HeightBatchEvidence managed = nativeFirst ? second : first;
                HeightBatchEvidence native = nativeFirst ? first : second;
                Assert.Equal(managed.WorkerChecksums, native.WorkerChecksums);
                Assert.Equal(managed.OutputSha256, native.OutputSha256);
                samples[sampleIndex] = new HeightPairEvidence(
                    sampleIndex,
                    nativeFirst,
                    managed,
                    native,
                    managed.ElapsedMilliseconds / native.ElapsedMilliseconds);
            }

            double managedMean = samples.Average(
                sample => sample.Managed.ElapsedMilliseconds);
            double nativeMean = samples.Average(
                sample => sample.Native.ElapsedMilliseconds);
            double managedAllocationMean = samples.Average(
                sample => sample.Managed.ManagedAllocatedBytes);
            double nativeAllocationMean = samples.Average(
                sample => sample.Native.ManagedAllocatedBytes);
            var report = new HeightWorkspaceReport(
                Seed,
                MapCount,
                MapSize,
                WorkspaceLength,
                WorkerCount,
                SampleCount,
                samples,
                managedMean,
                nativeMean,
                managedMean / nativeMean,
                managedAllocationMean,
                nativeAllocationMean,
                nativeMean < managedMean,
                samples[0].Managed.OutputSha256,
                DateTimeOffset.UtcNow);
            string resultsDirectory = Path.Combine(
                TestPaths.RepositoryRoot,
                "TestResults",
                "diagnostics");
            Directory.CreateDirectory(resultsDirectory);
            string resultPath = Path.Combine(
                resultsDirectory,
                $"native-height-workspace-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Native height workspace result: {resultPath}");
            Console.WriteLine(
                $"Managed mean: {managedMean:R} ms. " +
                $"NAM mean: {nativeMean:R} ms.");
        }

        private static HeightBatchEvidence RunHeightBatch(
            string implementation,
            Func<int, ulong> workerAction)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            Stopwatch stopwatch = Stopwatch.StartNew();
            var workers = new Task<ulong>[WorkerCount];
            for (int workerIndex = 0; workerIndex < workers.Length; workerIndex++)
            {
                int capturedWorkerIndex = workerIndex;
                workers[workerIndex] = Task.Run(
                    () => workerAction(capturedWorkerIndex));
            }

            Task.WaitAll(workers);
            stopwatch.Stop();
            ulong[] checksums = workers
                .Select(worker => worker.Result)
                .ToArray();
            return new HeightBatchEvidence(
                implementation,
                stopwatch.Elapsed.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                GC.CollectionCount(0) - gen0Before,
                GC.CollectionCount(1) - gen1Before,
                GC.CollectionCount(2) - gen2Before,
                ComputeOutputSha256(checksums),
                checksums);
        }

        private static ulong RunManagedWorker(int workerIndex)
        {
            var workspace = new float[WorkspaceLength];
            return RunManagedWorkerCore(workerIndex, workspace);
        }

        private static ulong RunManagedWorkerCore(
            int workerIndex,
            float[] workspace)
        {
            int start = GetWorkerStart(workerIndex);
            int end = GetWorkerStart(workerIndex + 1);
            Span<float> heights = workspace.AsSpan(0, ValueCount);
            ulong checksum = FnvOffset;
            for (int mapIndex = start; mapIndex < end; mapIndex++)
            {
                GetMapOrigin(mapIndex, out int baseX, out int baseZ);
                Quadrant.FillHeightMap(
                    Seed,
                    baseX,
                    baseZ,
                    MapSize,
                    MapSize,
                    heights);
                checksum = ConsumeHeights(checksum, heights);
            }

            return checksum;
        }

        private static ulong RunNativeWorker(int workerIndex)
        {
            using NativePool<float> pool = new(
                preLease: WorkspaceLength,
                returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
            using NativeWorkspace<float> workspace =
                pool.CreateWorkspace(WorkspaceLength);
            return RunNativeWorkerCore(workerIndex, in workspace);
        }

        private static ulong RunNativeWorkerCore(
            int workerIndex,
            scoped in NativeWorkspace<float> workspace)
        {
            int start = GetWorkerStart(workerIndex);
            int end = GetWorkerStart(workerIndex + 1);
            ulong checksum = FnvOffset;
            for (int mapIndex = start; mapIndex < end; mapIndex++)
            {
                GetMapOrigin(mapIndex, out int baseX, out int baseZ);
                checksum = workspace.Process(
                    ValueCount,
                    values => Quadrant.FillHeightMap(
                        Seed,
                        baseX,
                        baseZ,
                        MapSize,
                        MapSize,
                        values),
                    values => ConsumeHeights(checksum, values));
            }

            return checksum;
        }

        private static int GetWorkerStart(int workerIndex) =>
            (int)((long)workerIndex * MapCount / WorkerCount);

        private static void GetMapOrigin(
            int mapIndex,
            out int baseX,
            out int baseZ)
        {
            int mapX = mapIndex / MapSide;
            int mapZ = mapIndex % MapSide;
            baseX = (mapX - MapSide / 2) * MapSize;
            baseZ = (mapZ - MapSide / 2) * MapSize;
        }

        private static ulong ConsumeHeights(
            ulong checksum,
            ReadOnlySpan<float> values)
        {
            foreach (float value in values)
            {
                checksum ^= unchecked((uint)BitConverter.SingleToInt32Bits(value));
                checksum *= FnvPrime;
            }

            return checksum;
        }

        private static string ComputeOutputSha256(ulong[] checksums)
        {
            byte[] bytes = new byte[checksums.Length * sizeof(ulong)];
            for (int index = 0; index < checksums.Length; index++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    bytes.AsSpan(index * sizeof(ulong), sizeof(ulong)),
                    checksums[index]);
            }

            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private sealed record HeightBatchEvidence(
            string Implementation,
            double ElapsedMilliseconds,
            long ManagedAllocatedBytes,
            int Gen0Collections,
            int Gen1Collections,
            int Gen2Collections,
            string OutputSha256,
            ulong[] WorkerChecksums);

        private sealed record HeightPairEvidence(
            int SampleIndex,
            bool NativeFirst,
            HeightBatchEvidence Managed,
            HeightBatchEvidence Native,
            double ManagedToNativeSpeedup);

        private sealed record HeightWorkspaceReport(
            long Seed,
            int MapCount,
            int MapSize,
            int WorkspaceLength,
            int WorkerCount,
            int SampleCount,
            HeightPairEvidence[] Samples,
            double ManagedMeanMilliseconds,
            double NativeMeanMilliseconds,
            double ManagedToNativeMeanSpeedup,
            double ManagedMeanAllocatedBytes,
            double NativeMeanAllocatedBytes,
            bool NativeAdvantage,
            string OutputSha256,
            DateTimeOffset RecordedUtc);
    }
}

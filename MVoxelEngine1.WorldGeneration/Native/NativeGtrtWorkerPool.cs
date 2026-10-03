using System.Diagnostics;
using MVoxelEngine1.Infrastructure.Diagnostics;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

internal readonly record struct NativeWorkerAllocationSample(
    int ManagedThreadId,
    int WorkerIndex,
    bool GeneratesTerrain,
    long WaitBytes,
    long WorkBytes,
    long CompletionBytes,
    long TotalBytes);

internal sealed class NativeGtrtWorkerPool : IDisposable
{
    private static readonly NativeLeaseAction<byte> WarmSessionAction =
        static (scoped NativeLeaseView<byte> owner) =>
        {
            _ = new NativeGtrtSessionView(owner.AsSpan());
        };
    private static readonly TimeSpan WorkerStartTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkerCompletionTimeout =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WorkerJoinTimeout =
        TimeSpan.FromSeconds(30);

    private readonly NativeGtrtSession session;
    private readonly ManualResetEvent readyGate = new(false);
    private readonly ManualResetEvent armedGate = new(false);
    private readonly ManualResetEvent publicationGate = new(false);
    private readonly ManualResetEvent generationCompletionGate = new(false);
    private readonly ManualResetEvent completionGate = new(false);
    private readonly NativeGtrtWorker[] workers;
    private readonly NativeLeaseAction<byte> validateCompletionAction;
    private readonly bool streamGeneration;
    private readonly int generationWorkerCount;
    private readonly int meshWorkerCount;
    private int readyWorkerCount;
    private int remainingArmWorkerCount;
    private int remainingGenerationWorkerCount;
    private int remainingMeshWorkerCount;
    private int remainingWorkerCount;
    private int runState;
    private int shutdownRequested;
    private int startupPerformanceEnabled;
    private int disposed;
    private int runEpoch;
    private bool completionValid;
    private NativeGtrtFailureCode completionFailure;
    private long initialGenerationMilliseconds = -1;
    private long initialMeshMilliseconds = -1;

    internal NativeGtrtWorkerPool(
        NativeGtrtSession session,
        int generationWorkerCount,
        int meshWorkerCount,
        bool streamGeneration = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            generationWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            meshWorkerCount);

        _ = StartupPerformanceRecorder.IsRunning;
        this.session = session;
        this.streamGeneration = streamGeneration;
        validateCompletionAction = ValidateCompletion;
        workers = new NativeGtrtWorker[
            checked(generationWorkerCount + meshWorkerCount)];
        this.generationWorkerCount = generationWorkerCount;
        this.meshWorkerCount = meshWorkerCount;

        int workerOffset = 0;
        for (int index = 0; index < generationWorkerCount; index++)
        {
            workers[workerOffset++] = new NativeGtrtWorker(
                this,
                session,
                index,
                NativeGtrtWorkerKind.Generation);
        }

        for (int index = 0; index < meshWorkerCount; index++)
        {
            workers[workerOffset++] = new NativeGtrtWorker(
                this,
                session,
                index,
                NativeGtrtWorkerKind.Mesh);
        }

        try
        {
            foreach (NativeGtrtWorker worker in workers)
                worker.Start();
            if (!readyGate.WaitOne(WorkerStartTimeout))
                throw new TimeoutException("The native GTRT workers did not enter their start gate.");
        }
        catch
        {
            Volatile.Write(ref shutdownRequested, 1);
            SignalWorkers();
            JoinWorkers();
            DisposeWaitHandles();
            throw;
        }
    }

    internal long MaximumManagedAllocationBytes
    {
        get
        {
            long maximum = 0;
            foreach (NativeGtrtWorker worker in workers)
                maximum = Math.Max(maximum, worker.ManagedAllocationBytes);
            return maximum;
        }
    }

    internal long InitialGenerationMilliseconds =>
        Volatile.Read(ref initialGenerationMilliseconds);

    internal long InitialMeshMilliseconds =>
        Volatile.Read(ref initialMeshMilliseconds);

    internal int WorkerCount => workers.Length;

    internal void CopyAllocationSamples(Span<NativeWorkerAllocationSample> destination)
    {
        if (destination.Length < workers.Length)
            throw new ArgumentException("The worker allocation destination is too small.", nameof(destination));
        for (int index = 0; index < workers.Length; index++)
            destination[index] = workers[index].AllocationSample;
    }

    internal void Run(
        long seed,
        int centerChunkX = 0,
        int centerChunkY = 0,
        int centerChunkZ = 0)
    {
        int priorState;
        while (true)
        {
            priorState = Volatile.Read(ref runState);
            if ((priorState != 0 && priorState != 2) ||
                Interlocked.CompareExchange(
                    ref runState,
                    1,
                    priorState) != priorState)
            {
                if (priorState == 0 || priorState == 2)
                    continue;
                throw new InvalidOperationException(
                    "The native GTRT worker pool is not idle.");
            }
            break;
        }

        foreach (NativeGtrtWorker worker in workers)
        {
            if (worker.Fault is not null)
            {
                Volatile.Write(ref runState, 3);
                throw new InvalidOperationException(
                    "A native GTRT worker is not available.",
                    worker.Fault);
            }
        }

        armedGate.Reset();
        publicationGate.Reset();
        generationCompletionGate.Reset();
        completionGate.Reset();
        Volatile.Write(ref startupPerformanceEnabled, 0);
        remainingWorkerCount = workers.Length;
        remainingArmWorkerCount = workers.Length;
        remainingGenerationWorkerCount = generationWorkerCount;
        remainingMeshWorkerCount = meshWorkerCount;
        runEpoch = checked(runEpoch + 1);

        try
        {
            SignalWorkers();
            if (!armedGate.WaitOne(WorkerStartTimeout))
                throw new TimeoutException("The native GTRT workers did not arm before seed publication.");
            session.PrepareRun(
                seed,
                centerChunkX,
                centerChunkY,
                centerChunkZ);
            if (priorState == 0 && StartupPerformanceRecorder.IsRunning)
            {
                Volatile.Write(ref startupPerformanceEnabled, 1);
                StartupPerformanceRecorder.BeginInitialGeneration();
                if (streamGeneration)
                    StartupPerformanceRecorder.BeginInitialChunkMeshBuild();
            }
        }
        catch
        {
            Volatile.Write(ref shutdownRequested, 1);
            Volatile.Write(ref runState, 3);
            publicationGate.Set();
            generationCompletionGate.Set();
            SignalWorkers();
            throw;
        }

        publicationGate.Set();
        if (!completionGate.WaitOne(WorkerCompletionTimeout))
        {
            session.RequestCancellation();
            throw new TimeoutException(
                "The native GTRT workers did not complete their work.");
        }

        foreach (NativeGtrtWorker worker in workers)
        {
            while (worker.MeasuredRunEpoch != runEpoch)
                Thread.Yield();
            if (worker.Fault is not null)
            {
                throw new InvalidOperationException(
                    "A native GTRT worker failed.",
                    worker.Fault);
            }
        }

        Volatile.Write(ref runState, 2);
        completionValid = false;
        completionFailure = NativeGtrtFailureCode.None;
        session.Access(validateCompletionAction);
        if (!completionValid)
        {
            throw new InvalidOperationException(
                $"Native GTRT work did not complete. " +
                $"Failure code: {completionFailure}.");
        }

        Volatile.Write(ref runState, 2);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        int priorState = Interlocked.Exchange(ref runState, 3);
        Volatile.Write(ref shutdownRequested, 1);
        if (priorState == 1)
            session.RequestCancellation();

        SignalWorkers();
        publicationGate.Set();
        generationCompletionGate.Set();
        JoinWorkers();
        DisposeWaitHandles();
    }

    private void SignalWorkers()
    {
        foreach (NativeGtrtWorker worker in workers)
            worker.Signal();
    }

    private void DisposeWaitHandles()
    {
        foreach (NativeGtrtWorker worker in workers)
            worker.DisposeStartSignal();
        generationCompletionGate.Dispose();
        publicationGate.Dispose();
        armedGate.Dispose();
        completionGate.Dispose();
        readyGate.Dispose();
    }

    private bool ShutdownRequested =>
        Volatile.Read(ref shutdownRequested) != 0;

    private void NotifyReady()
    {
        int count = Interlocked.Increment(ref readyWorkerCount);
        if (count == workers.Length)
            readyGate.Set();
    }

    private void NotifyArmed()
    {
        if (Interlocked.Decrement(ref remainingArmWorkerCount) == 0)
            armedGate.Set();
    }

    private void NotifyStageCompleted(NativeGtrtWorkerKind kind)
    {
        if (Volatile.Read(ref runState) == 1)
        {
            if (kind == NativeGtrtWorkerKind.Generation)
            {
                int remainingGeneration = Interlocked.Decrement(
                    ref remainingGenerationWorkerCount);
                if (remainingGeneration == 0)
                {
                    if (Volatile.Read(ref startupPerformanceEnabled) != 0)
                    {
                        Volatile.Write(
                            ref initialGenerationMilliseconds,
                            StartupPerformanceRecorder.CompleteInitialGeneration());
                        if (!streamGeneration)
                        {
                            StartupPerformanceRecorder.BeginInitialChunkMeshBuild();
                        }
                    }

                    generationCompletionGate.Set();
                }
            }
            else
            {
                int remainingMesh = Interlocked.Decrement(
                    ref remainingMeshWorkerCount);
                if (remainingMesh == 0 &&
                    Volatile.Read(ref startupPerformanceEnabled) != 0)
                {
                    Volatile.Write(
                        ref initialMeshMilliseconds,
                        StartupPerformanceRecorder.CompleteInitialChunkMeshBuild());
                }
            }
        }

    }

    private void NotifyWorkerFinished()
    {
        int remaining = Interlocked.Decrement(ref remainingWorkerCount);
        if (remaining == 0)
            completionGate.Set();
    }

    private void JoinWorkers()
    {
        foreach (NativeGtrtWorker worker in workers)
        {
            if (!worker.Join(WorkerJoinTimeout))
            {
                throw new TimeoutException(
                    "A native GTRT worker did not stop.");
            }
        }
    }

    private void ValidateCompletion(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        ref NativeGtrtSessionState state = ref view.State;
        completionFailure =
            (NativeGtrtFailureCode)Volatile.Read(ref state.FailureCode);
        completionValid =
            completionFailure == NativeGtrtFailureCode.None &&
            Volatile.Read(ref state.RemainingColumns) == 0 &&
            Volatile.Read(ref state.RemainingChunks) == 0 &&
            Volatile.Read(ref state.ReadyPacketCount) ==
                state.PlannedMeshes &&
            state.PlannedMeshes + state.RetainedPackets == view.RequiredChunkCount &&
            Volatile.Read(ref state.ClaimedGenerationCount) == 0 &&
            Volatile.Read(ref state.ClaimedMeshCount) == 0;
    }

    private enum NativeGtrtWorkerKind
    {
        Generation,
        Mesh
    }

    private sealed class NativeGtrtWorker
    {
        private readonly NativeGtrtWorkerPool owner;
        private readonly NativeGtrtSession session;
        private readonly int workerIndex;
        private readonly NativeGtrtWorkerKind kind;
        private readonly NativeLeaseAction<byte> workAction;
        private readonly Thread thread;
        private readonly AutoResetEvent startSignal = new(false);
        private bool started;
        private int measuredRunEpoch;

        internal NativeGtrtWorker(
            NativeGtrtWorkerPool owner,
            NativeGtrtSession session,
            int workerIndex,
            NativeGtrtWorkerKind kind)
        {
            this.owner = owner;
            this.session = session;
            this.workerIndex = workerIndex;
            this.kind = kind;
            workAction = kind == NativeGtrtWorkerKind.Generation
                ? RunGeneration
                : RunMesh;
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"Native GTRT {kind} {workerIndex}"
            };
        }

        internal Exception? Fault { get; private set; }

        internal long ManagedAllocationBytes { get; private set; }

        internal NativeWorkerAllocationSample AllocationSample { get; private set; }

        internal int MeasuredRunEpoch => Volatile.Read(ref measuredRunEpoch);

        internal void Start()
        {
            thread.UnsafeStart();
            started = true;
        }

        internal void Signal() => startSignal.Set();

        internal void DisposeStartSignal() => startSignal.Dispose();

        internal bool Join(TimeSpan timeout) => !started || thread.Join(timeout);

        private void Run()
        {
            session.Access(WarmSessionAction);
            startSignal.Set();
            startSignal.WaitOne();
            _ = startSignal.WaitOne(0);
            _ = owner.publicationGate.WaitOne(0);
            _ = owner.generationCompletionGate.WaitOne(0);
            owner.NotifyReady();
            while (true)
            {
                startSignal.WaitOne();
                if (owner.ShutdownRequested)
                    return;

                long allocationStart = GC.GetAllocatedBytesForCurrentThread();
                owner.NotifyArmed();
                long workStart = allocationStart;
                long workEnd = allocationStart;
                try
                {
                    owner.publicationGate.WaitOne();
                    if (owner.ShutdownRequested)
                        return;
                    if (kind == NativeGtrtWorkerKind.Mesh &&
                        !owner.streamGeneration)
                    {
                        owner.generationCompletionGate.WaitOne();
                        if (owner.ShutdownRequested)
                            return;
                    }

                    workStart = GC.GetAllocatedBytesForCurrentThread();
                    session.Access(workAction);
                    workEnd = GC.GetAllocatedBytesForCurrentThread();
                }
                catch (Exception exception)
                {
                    Fault = exception;
                }
                finally
                {
                    owner.NotifyStageCompleted(kind);
                    owner.NotifyWorkerFinished();
                    long allocationEnd = GC.GetAllocatedBytesForCurrentThread();
                    long allocated = allocationEnd - allocationStart;
                    if (allocated >= ManagedAllocationBytes)
                    {
                        ManagedAllocationBytes = allocated;
                        AllocationSample = new NativeWorkerAllocationSample(
                            Environment.CurrentManagedThreadId,
                            workerIndex,
                            kind == NativeGtrtWorkerKind.Generation,
                            workStart - allocationStart,
                            workEnd - workStart,
                            allocationEnd - workEnd,
                            allocated);
                    }
                    Volatile.Write(ref measuredRunEpoch, owner.runEpoch);
                }

                if (Fault is not null)
                    return;
            }
        }

        private void RunGeneration(scoped NativeLeaseView<byte> ownerView)
        {
            var sessionView = new NativeGtrtSessionView(
                ownerView.AsSpan());
            NativeGameSnapshotView game = sessionView.GameSnapshot;
            while (sessionView.State.FailureCode == 0 &&
                   sessionView.TryClaimGeneration(
                       out NativeWorkItem work))
            {
                NativeColumnRecord column =
                    sessionView.Columns[work.RecordIndex];
                int biomeIndex = game.SelectBiomeIndex(
                    sessionView.State.Seed,
                    column.ChunkX,
                    column.ChunkZ);
                NativeBiomeDescriptor biome = game.Biomes[biomeIndex];
                if (!NativeColumnProfileGenerator.TryGenerate(
                        ref sessionView,
                        workerIndex,
                        in work,
                        biomeIndex,
                        in biome) ||
                    !sessionView.TryCompleteGeneration(in work))
                {
                    return;
                }
            }
        }

        private void RunMesh(scoped NativeLeaseView<byte> ownerView)
        {
            var sessionView = new NativeGtrtSessionView(
                ownerView.AsSpan());
            while (!sessionView.CancellationRequested &&
                   sessionView.State.FailureCode == 0 &&
                   Volatile.Read(
                       ref sessionView.State.RemainingChunks) != 0)
            {
                if (!sessionView.TryClaimMesh(out NativeWorkItem work))
                {
                    Thread.Yield();
                    continue;
                }

                long buildStart = Stopwatch.GetTimestamp();
                if (!NativeGeneratedMesh.TryBuild(
                        ref sessionView,
                        in work,
                        workerIndex))
                {
                    return;
                }

                StartupPerformanceRecorder.RecordFirstChunkBuild(
                    Stopwatch.GetElapsedTime(buildStart));
            }
        }
    }
}

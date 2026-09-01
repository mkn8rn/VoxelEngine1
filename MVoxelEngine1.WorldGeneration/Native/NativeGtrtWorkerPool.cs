using System.Diagnostics;
using MVoxelEngine1.Infrastructure.Diagnostics;
using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;

internal sealed class NativeGtrtWorkerPool : IDisposable
{
    private static readonly TimeSpan WorkerStartTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkerCompletionTimeout =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WorkerJoinTimeout =
        TimeSpan.FromSeconds(30);

    private readonly NativeGtrtSession session;
    private readonly Semaphore startSignals = new(0, int.MaxValue);
    private readonly ManualResetEvent readyGate = new(false);
    private readonly ManualResetEvent generationCompletionGate = new(false);
    private readonly ManualResetEvent completionGate = new(false);
    private readonly NativeGtrtWorker[] workers;
    private readonly NativeLeaseAction<byte> validateCompletionAction;
    private readonly bool streamGeneration;
    private readonly int generationWorkerCount;
    private readonly int meshWorkerCount;
    private int readyWorkerCount;
    private int remainingGenerationWorkerCount;
    private int remainingMeshWorkerCount;
    private int remainingWorkerCount;
    private int runState;
    private int shutdownRequested;
    private int startupPerformanceEnabled;
    private int disposed;
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

        foreach (NativeGtrtWorker worker in workers)
            worker.Start();

        if (!readyGate.WaitOne(WorkerStartTimeout))
        {
            Volatile.Write(ref shutdownRequested, 1);
            startSignals.Release(workers.Length);
            JoinWorkers();
            throw new TimeoutException(
                "The native GTRT workers did not enter their start gate.");
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

        generationCompletionGate.Reset();
        completionGate.Reset();
        Volatile.Write(ref startupPerformanceEnabled, 0);
        remainingWorkerCount = workers.Length;
        remainingGenerationWorkerCount = generationWorkerCount;
        remainingMeshWorkerCount = meshWorkerCount;

        try
        {
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
            startSignals.Release(workers.Length);
            throw;
        }

        startSignals.Release(workers.Length);
        if (!completionGate.WaitOne(WorkerCompletionTimeout))
        {
            session.RequestCancellation();
            throw new TimeoutException(
                "The native GTRT workers did not complete their work.");
        }

        foreach (NativeGtrtWorker worker in workers)
        {
            if (worker.Fault is not null)
            {
                throw new InvalidOperationException(
                    "A native GTRT worker failed.",
                    worker.Fault);
            }
        }

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

        startSignals.Release(workers.Length);
        generationCompletionGate.Set();
        JoinWorkers();
        generationCompletionGate.Dispose();
        completionGate.Dispose();
        readyGate.Dispose();
        startSignals.Dispose();
    }

    private bool ShutdownRequested =>
        Volatile.Read(ref shutdownRequested) != 0;

    private void NotifyReady()
    {
        int count = Interlocked.Increment(ref readyWorkerCount);
        if (count == workers.Length)
            readyGate.Set();
    }

    private void NotifyCompleted(NativeGtrtWorkerKind kind)
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
                view.RequiredChunkCount &&
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

        internal void Start() => thread.Start();

        internal bool Join(TimeSpan timeout) => thread.Join(timeout);

        private void Run()
        {
            owner.NotifyReady();
            while (true)
            {
                owner.startSignals.WaitOne();
                if (owner.ShutdownRequested)
                    return;

                try
                {
                    if (kind == NativeGtrtWorkerKind.Mesh &&
                        !owner.streamGeneration)
                    {
                        owner.generationCompletionGate.WaitOne();
                        if (owner.ShutdownRequested)
                            return;
                    }

                    long allocationStart =
                        GC.GetAllocatedBytesForCurrentThread();
                    session.Access(workAction);
                    long allocated =
                        GC.GetAllocatedBytesForCurrentThread() -
                        allocationStart;
                    if (allocated > ManagedAllocationBytes)
                        ManagedAllocationBytes = allocated;
                }
                catch (Exception exception)
                {
                    Fault = exception;
                }
                finally
                {
                    owner.NotifyCompleted(kind);
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

using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Tests
{
    public class InitialGenerationCompletionGateTests
    {
        [Fact]
        public void ImmediateCompletionReturnsWithoutNotification()
        {
            using var gate = new InitialGenerationCompletionGate();
            int predicateCalls = 0;

            gate.WaitUntilComplete(() =>
            {
                predicateCalls++;
                return true;
            });

            Assert.Equal(1, predicateCalls);
        }

        [Fact]
        public async Task CompletionBetweenCheckAndResetIsNotLost()
        {
            using var gate = new InitialGenerationCompletionGate();
            using var firstCheckStarted = new ManualResetEventSlim(false);
            using var releaseFirstCheck = new ManualResetEventSlim(false);
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            int complete = 0;
            int predicateCalls = 0;

            Task waitTask = Task.Run(() => gate.WaitUntilComplete(() =>
            {
                int call = Interlocked.Increment(ref predicateCalls);
                if (call == 1)
                {
                    firstCheckStarted.Set();
                    if (!releaseFirstCheck.Wait(
                        TimeSpan.FromSeconds(1),
                        cancellationToken))
                        throw new TimeoutException("The first completion check was not released.");

                    return false;
                }

                return Volatile.Read(ref complete) == 1;
            }), cancellationToken);

            Assert.True(firstCheckStarted.Wait(
                TimeSpan.FromSeconds(1),
                cancellationToken));
            Volatile.Write(ref complete, 1);
            gate.NotifyCollectionBecameEmpty();
            releaseFirstCheck.Set();

            await waitTask.WaitAsync(
                TimeSpan.FromSeconds(1),
                cancellationToken);
            Assert.Equal(2, Volatile.Read(ref predicateCalls));
        }

        [Fact]
        public async Task WaitContinuesUntilEveryCollectionIsEmpty()
        {
            using var gate = new InitialGenerationCompletionGate();
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            int emptyCollectionCount = 0;
            int predicateCalls = 0;

            Task waitTask = Task.Run(() => gate.WaitUntilComplete(() =>
            {
                Interlocked.Increment(ref predicateCalls);
                return Volatile.Read(ref emptyCollectionCount) == 3;
            }), cancellationToken);

            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref predicateCalls) >= 2,
                TimeSpan.FromSeconds(1)));

            Volatile.Write(ref emptyCollectionCount, 1);
            gate.NotifyCollectionBecameEmpty();
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref predicateCalls) >= 4,
                TimeSpan.FromSeconds(1)));
            Assert.False(waitTask.IsCompleted);

            Volatile.Write(ref emptyCollectionCount, 2);
            gate.NotifyCollectionBecameEmpty();
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref predicateCalls) >= 6,
                TimeSpan.FromSeconds(1)));
            Assert.False(waitTask.IsCompleted);

            Volatile.Write(ref emptyCollectionCount, 3);
            gate.NotifyCollectionBecameEmpty();
            await waitTask.WaitAsync(
                TimeSpan.FromSeconds(1),
                cancellationToken);
        }

        [Fact]
        public async Task ProtectedWorkTransferCannotAppearComplete()
        {
            using var gate = new InitialGenerationCompletionGate();
            using var stateLock = new ReaderWriterLockSlim();
            using var predicateAttempted = new ManualResetEventSlim(false);
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            int bufferWork = 1;
            int activeWork = 0;
            int predicateCalls = 0;

            Task waitTask = Task.Run(() => gate.WaitUntilComplete(() =>
            {
                Interlocked.Increment(ref predicateCalls);
                predicateAttempted.Set();
                stateLock.EnterReadLock();
                try
                {
                    return Volatile.Read(ref bufferWork) == 0 &&
                           Volatile.Read(ref activeWork) == 0;
                }
                finally
                {
                    stateLock.ExitReadLock();
                }
            }), cancellationToken);

            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref predicateCalls) >= 2,
                TimeSpan.FromSeconds(1)));
            predicateAttempted.Reset();

            stateLock.EnterWriteLock();
            try
            {
                Volatile.Write(ref bufferWork, 0);
                gate.NotifyCollectionBecameEmpty();
                Assert.True(predicateAttempted.Wait(
                    TimeSpan.FromSeconds(1),
                    cancellationToken));
                Assert.False(waitTask.IsCompleted);
                Volatile.Write(ref activeWork, 1);
            }
            finally
            {
                stateLock.ExitWriteLock();
            }

            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref predicateCalls) >= 4,
                TimeSpan.FromSeconds(1)));
            Assert.False(waitTask.IsCompleted);

            stateLock.EnterWriteLock();
            try
            {
                Volatile.Write(ref activeWork, 0);
                gate.NotifyCollectionBecameEmpty();
            }
            finally
            {
                stateLock.ExitWriteLock();
            }

            await waitTask.WaitAsync(
                TimeSpan.FromSeconds(1),
                cancellationToken);
        }
    }
}

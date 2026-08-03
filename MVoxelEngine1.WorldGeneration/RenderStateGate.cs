using System.Collections.Concurrent;

namespace MVoxelEngine1.WorldGeneration
{
    internal sealed class RenderStateGate : IDisposable
    {
        private readonly ReaderWriterLockSlim stateLock =
            new(LockRecursionPolicy.SupportsRecursion);

        public IDisposable AcquireReadScope()
        {
            EnterRead();
            return new Scope(stateLock, write: false);
        }

        public IDisposable AcquireWriteScope()
        {
            EnterWrite();
            return new Scope(stateLock, write: true);
        }

        public void EnterRead()
        {
            stateLock.EnterReadLock();
        }

        public void ExitRead()
        {
            stateLock.ExitReadLock();
        }

        public void EnterWrite()
        {
            stateLock.EnterWriteLock();
        }

        public void ExitWrite()
        {
            stateLock.ExitWriteLock();
        }

        public void MoveUnbuiltToActive<TKey, TValue>(
            ConcurrentDictionary<TKey, TValue> unbuilt,
            ConcurrentDictionary<TKey, TValue> active,
            ConcurrentDictionary<TKey, long> dirty,
            TKey key,
            TValue value)
            where TKey : notnull
        {
            EnterWrite();
            try
            {
                active[key] = value;
                unbuilt.TryRemove(key, out _);
                dirty.TryRemove(key, out _);
            }
            finally
            {
                ExitWrite();
            }
        }

        public void Dispose()
        {
            stateLock.Dispose();
        }

        private sealed class Scope : IDisposable
        {
            private ReaderWriterLockSlim? stateLock;
            private readonly bool write;

            public Scope(
                ReaderWriterLockSlim stateLock,
                bool write)
            {
                this.stateLock = stateLock;
                this.write = write;
            }

            public void Dispose()
            {
                ReaderWriterLockSlim? currentLock = Interlocked.Exchange(
                    ref stateLock,
                    null);
                if (currentLock is null)
                    return;

                if (write)
                    currentLock.ExitWriteLock();
                else
                    currentLock.ExitReadLock();
            }
        }
    }
}

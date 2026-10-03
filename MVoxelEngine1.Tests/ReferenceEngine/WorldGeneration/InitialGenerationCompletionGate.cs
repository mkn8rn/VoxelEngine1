using System;
using System.Threading;

namespace MVoxelEngine1.WorldGeneration
{
    internal sealed class InitialGenerationCompletionGate : IDisposable
    {
        private readonly ManualResetEventSlim stateChanged = new(false);

        public void NotifyCollectionBecameEmpty()
        {
            stateChanged.Set();
        }

        public void WaitUntilComplete(Func<bool> isComplete)
        {
            ArgumentNullException.ThrowIfNull(isComplete);

            while (!isComplete())
            {
                stateChanged.Reset();
                if (isComplete())
                    return;

                stateChanged.Wait();
            }
        }

        public void Dispose()
        {
            stateChanged.Dispose();
        }
    }
}

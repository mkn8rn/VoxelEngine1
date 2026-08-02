using System;
using System.Threading;

namespace MVoxelEngine1.WorldGeneration
{
    internal sealed class InitialSchedulingPassGate : IDisposable
    {
        private readonly ManualResetEventSlim completed = new(false);

        public void NotifyCompleted()
        {
            completed.Set();
        }

        public void WaitUntilCompleted()
        {
            completed.Wait();
        }

        public void Dispose()
        {
            completed.Dispose();
        }
    }
}

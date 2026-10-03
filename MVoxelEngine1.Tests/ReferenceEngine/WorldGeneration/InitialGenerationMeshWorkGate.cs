using System;
using System.Threading;

namespace MVoxelEngine1.WorldGeneration
{
    internal sealed class InitialGenerationMeshWorkGate
    {
        private int isDeferred;

        public bool ShouldSchedule =>
            Volatile.Read(ref isDeferred) == 0;

        public void BeginDeferral()
        {
            if (Interlocked.CompareExchange(
                    ref isDeferred,
                    1,
                    0) != 0)
            {
                throw new InvalidOperationException(
                    "Initial generation mesh work is already deferred.");
            }
        }

        public void CompleteDeferral()
        {
            if (Interlocked.CompareExchange(
                    ref isDeferred,
                    0,
                    1) != 1)
            {
                throw new InvalidOperationException(
                    "Initial generation mesh work is not deferred.");
            }
        }
    }
}

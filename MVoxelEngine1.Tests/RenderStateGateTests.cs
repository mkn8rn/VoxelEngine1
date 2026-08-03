using System.Collections.Concurrent;
using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Tests
{
    public class RenderStateGateTests
    {
        [Fact(Timeout = 5_000)]
        public async Task ReaderCannotMissChunkDuringStateMove()
        {
            using var gate = new RenderStateGate();
            var unbuilt = new ConcurrentDictionary<string, object>();
            var active = new ConcurrentDictionary<string, object>();
            var dirty = new ConcurrentDictionary<string, long>();
            using var activeCheckCompleted = new ManualResetEventSlim(false);
            using var releaseReader = new ManualResetEventSlim(false);
            using var writerStarted = new ManualResetEventSlim(false);
            CancellationToken cancellationToken =
                TestContext.Current.CancellationToken;
            const string Key = "chunk";
            const string PendingKey = "pending";
            const string ReplacedKey = "replaced";
            object value = new();
            object pendingValue = new();
            object staleValue = new();
            object replacementValue = new();
            unbuilt[Key] = value;
            unbuilt[PendingKey] = pendingValue;
            unbuilt[ReplacedKey] = replacementValue;
            dirty[Key] = 7;
            dirty[PendingKey] = 8;
            dirty[ReplacedKey] = 9;
            int movedCount = 0;

            Task<bool> reader = Task.Run(() =>
            {
                gate.EnterRead();
                try
                {
                    bool found = active.TryGetValue(Key, out _);
                    activeCheckCompleted.Set();
                    if (!releaseReader.Wait(
                        TimeSpan.FromSeconds(1),
                        cancellationToken))
                    {
                        throw new TimeoutException("The state reader was not released.");
                    }

                    return found || unbuilt.TryGetValue(Key, out _);
                }
                finally
                {
                    gate.ExitRead();
                }
            }, cancellationToken);

            Assert.True(activeCheckCompleted.Wait(
                TimeSpan.FromSeconds(1),
                cancellationToken));
            Task writer = Task.Run(() =>
            {
                writerStarted.Set();
                movedCount = gate.MoveCompletedUnbuiltToActive(
                    unbuilt,
                    active,
                    dirty,
                    new[] { Key, PendingKey, ReplacedKey },
                    new[] { value, null, staleValue });
            }, cancellationToken);

            Assert.True(writerStarted.Wait(
                TimeSpan.FromSeconds(1),
                cancellationToken));
            try
            {
                await Task.Delay(50, cancellationToken);
                Assert.False(writer.IsCompleted);
                releaseReader.Set();
                Assert.True(await reader.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    cancellationToken));
                await writer.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
            }
            finally
            {
                releaseReader.Set();
            }

            using IDisposable finalStateScope = gate.AcquireReadScope();
            Assert.Same(value, active[Key]);
            Assert.False(unbuilt.ContainsKey(Key));
            Assert.False(dirty.ContainsKey(Key));
            Assert.Same(pendingValue, unbuilt[PendingKey]);
            Assert.False(active.ContainsKey(PendingKey));
            Assert.Equal(8, dirty[PendingKey]);
            Assert.Same(replacementValue, unbuilt[ReplacedKey]);
            Assert.False(active.ContainsKey(ReplacedKey));
            Assert.Equal(9, dirty[ReplacedKey]);
            Assert.Equal(1, Volatile.Read(ref movedCount));
        }
    }
}

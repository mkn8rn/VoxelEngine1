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
            var unbuilt = new ConcurrentDictionary<string, int>();
            var active = new ConcurrentDictionary<string, int>();
            var dirty = new ConcurrentDictionary<string, long>();
            using var activeCheckCompleted = new ManualResetEventSlim(false);
            using var releaseReader = new ManualResetEventSlim(false);
            using var writerStarted = new ManualResetEventSlim(false);
            CancellationToken cancellationToken =
                TestContext.Current.CancellationToken;
            const string Key = "chunk";
            unbuilt[Key] = 42;
            dirty[Key] = 7;

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
                gate.MoveUnbuiltToActive(
                    unbuilt,
                    active,
                    dirty,
                    Key,
                    42);
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
            Assert.Equal(42, active[Key]);
            Assert.False(unbuilt.ContainsKey(Key));
            Assert.False(dirty.ContainsKey(Key));
        }
    }
}

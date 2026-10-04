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
            var unbuilt = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
            var active = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
            var dirty = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
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
            InitializeStateMoveFixture(unbuilt, dirty, Key, PendingKey, ReplacedKey, value, pendingValue, replacementValue);
            int movedCount = 0;

            Task<bool> reader = Task.Run(() =>
            {
                return ReadWhileWriterIsBlocked(gate, active, unbuilt, activeCheckCompleted, releaseReader, Key, cancellationToken);
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
                await Task.Delay(50, cancellationToken).ConfigureAwait(true);
                Assert.False(writer.IsCompleted);
                releaseReader.Set();
                Assert.True(await reader.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    cancellationToken).ConfigureAwait(true));
                await writer.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                releaseReader.Set();
            }
            ExtractReaderCannotMissChunkDuringStateMoveRegion(gate, unbuilt, active, dirty, Key, PendingKey, ReplacedKey, value, pendingValue, replacementValue, ref movedCount);
        }

        private static void InitializeStateMoveFixture(ConcurrentDictionary<string, object> unbuilt,
            ConcurrentDictionary<string, long> dirty, string Key, string PendingKey, string ReplacedKey,
            object value, object pendingValue, object replacementValue)
        {
            unbuilt[Key] = value;
            unbuilt[PendingKey] = pendingValue;
            unbuilt[ReplacedKey] = replacementValue;
            dirty[Key] = 7;
            dirty[PendingKey] = 8;
            dirty[ReplacedKey] = 9;
        }

        private static bool ReadWhileWriterIsBlocked(RenderStateGate gate,
            ConcurrentDictionary<string, object> active, ConcurrentDictionary<string, object> unbuilt,
            ManualResetEventSlim activeCheckCompleted, ManualResetEventSlim releaseReader,
            string Key, CancellationToken cancellationToken)
        {
            gate.EnterRead();
            try
            {
                bool found = active.ContainsKey(Key);
                activeCheckCompleted.Set();
                if (!releaseReader.Wait(
                    TimeSpan.FromSeconds(1),
                    cancellationToken))
                {
                    throw new TimeoutException("The state reader was not released.");
                }

                return found || unbuilt.ContainsKey(Key);
            }
            finally
            {
                gate.ExitRead();
            }
        }

        private static void ExtractReaderCannotMissChunkDuringStateMoveRegion(global::MVoxelEngine1.WorldGeneration.RenderStateGate gate, global::System.Collections.Concurrent.ConcurrentDictionary<string, object> unbuilt, global::System.Collections.Concurrent.ConcurrentDictionary<string, object> active, global::System.Collections.Concurrent.ConcurrentDictionary<string, long> dirty, string Key, string PendingKey, string ReplacedKey, object value, object pendingValue, object replacementValue, ref int movedCount)
        {
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

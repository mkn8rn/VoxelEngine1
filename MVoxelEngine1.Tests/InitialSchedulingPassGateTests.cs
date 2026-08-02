using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Tests
{
    public class InitialSchedulingPassGateTests
    {
        [Fact]
        public void NotificationBeforeWaitReturnsImmediately()
        {
            using var gate = new InitialSchedulingPassGate();

            gate.NotifyCompleted();
            gate.WaitUntilCompleted();
        }

        [Fact]
        public async Task WaitBlocksUntilNotification()
        {
            using var gate = new InitialSchedulingPassGate();
            using var waitStarted = new ManualResetEventSlim(false);
            CancellationToken cancellationToken =
                TestContext.Current.CancellationToken;

            Task waitTask = Task.Run(() =>
            {
                waitStarted.Set();
                gate.WaitUntilCompleted();
            }, cancellationToken);

            Assert.True(waitStarted.Wait(
                TimeSpan.FromSeconds(1),
                cancellationToken));
            Assert.False(waitTask.IsCompleted);

            gate.NotifyCompleted();
            await waitTask.WaitAsync(
                TimeSpan.FromSeconds(1),
                cancellationToken);
        }
    }
}

using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Tests
{
    public class InitialGenerationMeshWorkGateTests
    {
        [Fact]
        public void MeshWorkIsEnabledByDefault()
        {
            var gate = new InitialGenerationMeshWorkGate();

            Assert.True(gate.ShouldSchedule);
        }

        [Fact]
        public void DeferralDisablesMeshWorkUntilCompletion()
        {
            var gate = new InitialGenerationMeshWorkGate();

            gate.BeginDeferral();
            Assert.False(gate.ShouldSchedule);

            gate.CompleteDeferral();
            Assert.True(gate.ShouldSchedule);
        }

        [Fact]
        public void DuplicateDeferralIsRejected()
        {
            var gate = new InitialGenerationMeshWorkGate();
            gate.BeginDeferral();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                gate.BeginDeferral);

            Assert.Equal(
                "Initial generation mesh work is already deferred.",
                exception.Message);
        }

        [Fact]
        public void CompletionWithoutDeferralIsRejected()
        {
            var gate = new InitialGenerationMeshWorkGate();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                gate.CompleteDeferral);

            Assert.Equal(
                "Initial generation mesh work is not deferred.",
                exception.Message);
        }

        [Fact]
        public void DuplicateCompletionIsRejected()
        {
            var gate = new InitialGenerationMeshWorkGate();
            gate.BeginDeferral();
            gate.CompleteDeferral();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                gate.CompleteDeferral);

            Assert.Equal(
                "Initial generation mesh work is not deferred.",
                exception.Message);
        }
    }
}

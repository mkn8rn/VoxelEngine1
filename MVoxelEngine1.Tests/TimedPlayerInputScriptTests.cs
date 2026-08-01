using MVoxelEngine1.Infrastructure.Models.Simulation;

namespace MVoxelEngine1.Tests
{
    public class TimedPlayerInputScriptTests
    {
        [Fact]
        public void ParsesSequentialAndCombinedTimedInput()
        {
            IReadOnlyList<TimedPlayerInputStep> steps = TimedPlayerInputScript.Parse(
                "W:2,W+D:1.5,Space:3");

            Assert.Equal(3, steps.Count);
            Assert.Equal(PlayerInputKeys.W, steps[0].Keys);
            Assert.Equal(2, steps[0].DurationSeconds);
            Assert.Equal(PlayerInputKeys.W | PlayerInputKeys.D, steps[1].Keys);
            Assert.Equal(1.5, steps[1].DurationSeconds);
            Assert.Equal(PlayerInputKeys.Space, steps[2].Keys);
            Assert.Equal(3, steps[2].DurationSeconds);
        }

        [Theory]
        [InlineData("")]
        [InlineData("W")]
        [InlineData("W:0")]
        [InlineData("W:NaN")]
        [InlineData("Jump:2")]
        [InlineData("W+W:2")]
        public void RejectsInvalidTimedInput(string script)
        {
            Assert.Throws<FormatException>(() => TimedPlayerInputScript.Parse(script));
        }
    }
}

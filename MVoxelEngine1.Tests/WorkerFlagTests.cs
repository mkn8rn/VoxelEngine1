using System.Globalization;
using MVoxelEngine1.Infrastructure.Flags;
using MVoxelEngine1.WorldGeneration.Native;

namespace MVoxelEngine1.Tests;

public sealed class WorkerFlagTests
{
    [Theory]
    [InlineData("en-US", "0.5")]
    [InlineData("en-US", "0,5")]
    [InlineData("hr-HR", "0.5")]
    [InlineData("hr-HR", "0,5")]
    [InlineData("de-DE", "0.5")]
    [InlineData("de-DE", "0,5")]
    public void WorkerDecimalsHaveTheSameMeaningAcrossCultures(string culture, string value)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            ConsoleFlags.Parse([
                "--worldGenWorkersPerCore", value,
                "--WorldGenWorkersPerCoreInitial", value,
                "--meshRenderWorkersPerCore", value,
                "--MeshRenderWorkersPerCoreInitial", value]);
            Assert.Equal(0.5f, ConsoleFlags.consoleFlags.worldGenWorkersPerCore);
            Assert.Equal(0.5f, ConsoleFlags.consoleFlags.worldGenWorkersPerCoreInitial);
            Assert.Equal(0.5f, ConsoleFlags.consoleFlags.meshRenderWorkersPerCore);
            Assert.Equal(0.5f, ConsoleFlags.consoleFlags.meshRenderWorkersPerCoreInitial);
            Assert.Equal(Math.Max(1, Environment.ProcessorCount / 2), NativeGtrtPipeline.GetWorkerCount(0.5f));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            ConsoleFlags.Parse([]);
        }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("-0.01")]
    [InlineData("1e50")]
    public void InvalidWorkerMultipliersFailBeforeNativeStartup(string value)
    {
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ConsoleFlags.Parse(["--worldGenWorkersPerCore", value]));
        }
        finally
        {
            ConsoleFlags.Parse([]);
        }
    }

    [Theory]
    [InlineData("1,000.5")]
    [InlineData("1.000,5")]
    [InlineData("wrong")]
    public void WorkerMultipliersRejectThousandsSeparatorsAndMalformedNumbers(string value)
    {
        try
        {
            Assert.Throws<FormatException>(() =>
                ConsoleFlags.Parse(["--meshRenderWorkersPerCore", value]));
        }
        finally
        {
            ConsoleFlags.Parse([]);
        }
    }

    [Fact]
    public void UnsetAndZeroWorkerValuesKeepExistingFallbackBehavior()
    {
        try
        {
            ConsoleFlags.Parse(["--worldGenWorkersPerCore", " ", "--meshRenderWorkersPerCore", "0"]);
            Assert.Null(ConsoleFlags.consoleFlags.worldGenWorkersPerCore);
            Assert.Equal(0f, ConsoleFlags.consoleFlags.meshRenderWorkersPerCore);
            Assert.Equal(1, NativeGtrtPipeline.GetWorkerCount(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeGtrtPipeline.GetWorkerCount(float.MaxValue));
        }
        finally
        {
            ConsoleFlags.Parse([]);
        }
    }
}

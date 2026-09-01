using System.Reflection;
using Supprocom.OpenSimplexNoise;

namespace MVoxelEngine1.Tests
{
    public sealed class OpenSimplexNoisePackageIntegrationTests
    {
        private const string RepositoryCommit =
            "18a427ff0fb0736b0cc997b76e9a041c85986cfc";

        [Fact]
        public void PublishedPackageIdentityIsLoaded()
        {
            Assembly assembly = typeof(OpenSimplexNoise).Assembly;
            AssemblyName name = assembly.GetName();
            AssemblyInformationalVersionAttribute? information =
                assembly.GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>();

            Assert.Equal("Supprocom.OpenSimplexNoise", name.Name);
            Assert.Equal(new Version(0, 1, 1, 0), name.Version);
            Assert.Equal(
                $"0.1.1+{RepositoryCommit}",
                information?.InformationalVersion);
            Assert.Contains(
                assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
                metadata => metadata.Key == "RepositoryCommit" &&
                    metadata.Value == RepositoryCommit);
        }

        [Fact]
        public void PublishedPackagePreservesTerrainNoiseBits()
        {
            const int tableLength = OpenSimplexNoise.PermutationTableLength;
            Span<byte> state = stackalloc byte[
                tableLength * 4 + OpenSimplexNoise.SourceScratchLength];
            Span<byte> permutation = state[..tableLength];
            Span<byte> permutation2D = state.Slice(tableLength, tableLength);
            Span<byte> permutation3D = state.Slice(
                tableLength * 2,
                tableLength);
            Span<byte> permutation4D = state.Slice(
                tableLength * 3,
                tableLength);
            Span<byte> sourceScratch = state.Slice(
                tableLength * 4,
                OpenSimplexNoise.SourceScratchLength);
            OpenSimplexNoise.Initialize(
                123456,
                permutation,
                permutation2D,
                permutation3D,
                permutation4D,
                sourceScratch);

            Assert.Equal(
                0x3FCA7AC069022666UL,
                BitConverter.DoubleToUInt64Bits(
                    OpenSimplexNoise.Evaluate(
                        permutation,
                        permutation2D,
                        -0.125,
                        -17.5)));
        }
    }
}

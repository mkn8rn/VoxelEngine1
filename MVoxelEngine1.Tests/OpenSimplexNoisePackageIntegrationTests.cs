using System.Reflection;
using Supprocom.OpenSimplexNoise;

namespace MVoxelEngine1.Tests
{
    public sealed class OpenSimplexNoisePackageIntegrationTests
    {
        private const string RepositoryCommit =
            "58d0a5ffff8b192356863f17048915cfb3f01e3c";

        [Fact]
        public void PublishedPackageIdentityIsLoaded()
        {
            Assembly assembly = typeof(OpenSimplexNoise).Assembly;
            AssemblyName name = assembly.GetName();
            AssemblyInformationalVersionAttribute? information =
                assembly.GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>();

            Assert.Equal("Supprocom.OpenSimplexNoise", name.Name);
            Assert.Equal(new Version(0, 1, 0, 0), name.Version);
            Assert.Equal(
                $"0.1.0+{RepositoryCommit}",
                information?.InformationalVersion);
            Assert.Contains(
                assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
                metadata => metadata.Key == "RepositoryCommit" &&
                    metadata.Value == RepositoryCommit);
        }

        [Fact]
        public void PublishedPackagePreservesTerrainNoiseBits()
        {
            var noise = new OpenSimplexNoise(123456);

            Assert.Equal(
                0x3FCA7AC069022666UL,
                unchecked((ulong)BitConverter.DoubleToInt64Bits(
                    noise.Evaluate(-0.125, -17.5))));
        }
    }
}

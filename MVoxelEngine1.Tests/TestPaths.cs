using System.Reflection;

namespace MVoxelEngine1.Tests
{
    internal static class TestPaths
    {
        public static string GameDataRoot => Path.Combine(AppContext.BaseDirectory, "GameData");
        public static string RepositoryRoot => Path.GetFullPath(GetMetadata("RepositoryRoot"));
        public static string ResultsRoot { get; } = Path.Combine(Path.GetTempPath(), "MVoxelEngine1.Tests", "results", Guid.NewGuid().ToString("N"));

        public static string ApplicationExecutable
        {
            get
            {
                return Path.Combine(Path.GetDirectoryName(GetMetadata("ApplicationTargetPath"))!, "MVoxelEngine1.Application" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            }
        }

        private static string GetMetadata(string key) => typeof(TestPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value ?? throw new InvalidOperationException($"Missing build metadata: {key}.");
        public static TestWorkspace CreateWorkspace()
        {
            string root = Path.Combine(Path.GetTempPath(), "MVoxelEngine1.Tests", Guid.NewGuid().ToString("N"));
            string gameDataRoot = Path.Combine(root, "GameData");
            CopyDirectory(GameDataRoot, gameDataRoot);
            return new TestWorkspace(root, gameDataRoot);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (string directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}

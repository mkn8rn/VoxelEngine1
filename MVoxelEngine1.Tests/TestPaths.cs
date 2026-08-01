namespace MVoxelEngine1.Tests
{
    internal static class TestPaths
    {
        public static string GameDataRoot => Path.Combine(AppContext.BaseDirectory, "GameData");

        public static string RepositoryRoot
        {
            get
            {
                DirectoryInfo? directory = new(AppContext.BaseDirectory);
                while (directory is not null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "MVoxelEngine1.sln")))
                        return directory.FullName;

                    directory = directory.Parent;
                }

                throw new DirectoryNotFoundException("Repository root was not found.");
            }
        }

        public static string ApplicationExecutable
        {
            get
            {
#if DEBUG
                const string configuration = "Debug";
#else
                const string configuration = "Release";
#endif
                return Path.Combine(
                    RepositoryRoot,
                    "MVoxelEngine1.Application",
                    "bin",
                    configuration,
                    "net10.0",
                    "MVoxelEngine1.Application.exe");
            }
        }

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

    internal sealed class TestWorkspace(string root, string gameDataRoot) : IDisposable
    {
        public string Root { get; } = root;

        public string GameDataRoot { get; } = gameDataRoot;

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}

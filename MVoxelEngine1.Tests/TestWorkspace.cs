using System.Reflection;

namespace MVoxelEngine1.Tests
{
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

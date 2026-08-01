using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MVoxelEngine1.Infrastructure.Loaders;
using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Models.Terrain;

namespace MVoxelEngine1.Infrastructure.Diagnostics
{
    public static class RuntimeInputHasher
    {
        public static string HashGameInputs()
        {
            string gameDirectory = GameManager.settings.loadedGameDirectory;
            string savesDirectory = Path.GetFullPath(
                GameManager.settings.savesWorldDirectory);
            string savesPrefix = savesDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? savesDirectory
                : savesDirectory + Path.DirectorySeparatorChar;
            string[] files = Directory.EnumerateFiles(
                    gameDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path =>
                    !path.Equals(savesDirectory, StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith(savesPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(
                    path => Path.GetRelativePath(gameDirectory, path),
                    StringComparer.Ordinal)
                .ToArray();

            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(hash, "MVoxelEngine1.GameInputs.v1");
            byte[] buffer = new byte[128 * 1024];
            Span<byte> length = stackalloc byte[8];
            foreach (string path in files)
            {
                string relativePath = Path.GetRelativePath(gameDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                AppendString(hash, relativePath);
                var info = new FileInfo(path);
                BinaryPrimitives.WriteInt64LittleEndian(length, info.Length);
                hash.AppendData(length);
                using FileStream stream = File.OpenRead(path);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                    hash.AppendData(buffer.AsSpan(0, read));
            }

            return GetHex(hash);
        }

        public static string HashBlockRegistry()
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(hash, "MVoxelEngine1.RuntimeBlockRegistry.v1");
            Span<byte> encoded = stackalloc byte[5];
            Span<byte> state = stackalloc byte[4];
            foreach (BlockType block in
                     TerrainLoader.allBlockTypeObjects.OrderBy(block => block.ID))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(encoded, block.ID);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    encoded[2..],
                    (ushort)block.BaseType);
                encoded[4] = block.IsTransparent ? (byte)1 : (byte)0;
                hash.AppendData(encoded);
                AppendString(hash, block.UniqueName);
                AppendString(hash, block.Name);
                AppendString(hash, block.TextureFaceBase);
                AppendString(hash, block.TextureFaceTop);
                AppendString(hash, block.TextureFaceFront);
                AppendString(hash, block.TextureFaceBack);
                AppendString(hash, block.TextureFaceLeft);
                AppendString(hash, block.TextureFaceRight);
                AppendString(hash, block.TextureFaceBottom);
                BinaryPrimitives.WriteInt32LittleEndian(
                    state,
                    (int)block.StateOfMatter);
                hash.AppendData(state);
            }

            return GetHex(hash);
        }

        private static void AppendString(IncrementalHash hash, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        private static string GetHex(IncrementalHash hash) =>
            Convert.ToHexString(hash.GetHashAndReset());
    }
}

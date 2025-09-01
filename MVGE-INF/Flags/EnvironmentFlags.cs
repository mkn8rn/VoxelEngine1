using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using MVGE_INF.Models;

namespace MVGE_INF.Flags
{
    public class EnvironmentFlags
    {
        private static readonly string EnvFolder = Path.GetDirectoryName(typeof(EnvironmentFlags).Assembly.Location)!;
        private static readonly string EnvFileName = IsDevelopment() ? ".env.development" : ".env";
        private static readonly string EnvFilePath = Path.Combine(EnvFolder, EnvFileName);

        public static ProgramFlags environmentFlags { get; private set; } = new ProgramFlags();

        public static bool IsDevelopment()
        {
            var aspnetEnv = System.Environment.GetEnvironmentVariable("APP_ENVIRONMENT");
            return string.Equals(aspnetEnv, "Development", StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<string, string> ReadEnvironmentVariables()
        {
            var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(EnvFilePath))
                throw new FileNotFoundException($"Environment file not found: {EnvFilePath}");

            foreach (var line in File.ReadAllLines(EnvFilePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                    envVars[parts[0].Trim()] = parts[1].Trim();
            }
            return envVars;
        }

        public static void LoadEnvironmentFlags()
        {
            var env = ReadEnvironmentVariables();
            var flags = new ProgramFlags();

            foreach (var kvp in env)
            {
                FlagDescriptors.Apply(flags, kvp.Key, kvp.Value);
            }

            environmentFlags = flags;
        }
    }
}

using MVoxelEngine1.Infrastructure.Managers;
using MVoxelEngine1.Infrastructure.Flags;
using System;
using System.Runtime;

namespace MVoxelEngine1.Application
{
    class Program
    {
        static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("APP_ENVIRONMENT", "Development");
            ConsoleFlags.Parse(args);
            EnvironmentFlags.LoadEnvironmentFlags();
            FlagManager.ApplyFlags(args);

            using (Window game = new Window())
            {
                game.Run();
            }
        }
    }
}
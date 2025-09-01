using MVGE_GEN;
using MVGE_INF.Managers;
using MVGE_INF.Flags;
using System;
using System.Runtime;

namespace MVGE
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
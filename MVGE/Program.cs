using MVGE;
using MVGE.Managers;
using MVGE.Middleware;
using System;
using System.Runtime;

namespace GloriousTribesApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("APP_ENVIRONMENT", "Development");
            ConsoleFlagsMiddleware.Parse(args);
            EnvironmentFlagsMiddleware.LoadEnvironmentFlags();
            FlagManager.ApplyFlags(args);
            using (Window game = new Window())
            {
                game.Run();
            }
        }
    }
}
using MVGE_GEN;
using MVGE_INF.Managers;
using MVGE_INF.Middleware;
using System;
using System.Runtime;

namespace MVGE
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
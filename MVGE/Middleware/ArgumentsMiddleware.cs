using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVGE.Models;

namespace MVGE.Middleware
{
    public static class ArgumentsMiddleware
    {
        public static CommandLineFlags Flags { get; private set; } = new CommandLineFlags();

        public static void Parse(string[] args)
        {
            var flags = new CommandLineFlags();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--game")
                {
                    flags.game = args[i + 1];
                }
            }
            Flags = flags;
        }
    }
}

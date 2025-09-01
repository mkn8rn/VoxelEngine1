using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MVGE_INF.Models;

namespace MVGE_INF.Flags
{
    public static class ConsoleFlags
    {
        public static ProgramFlags consoleFlags { get; private set; } = new ProgramFlags();

        public static void Parse(string[] args)
        {
            var flags = new ProgramFlags();
            for (int i = 0; i < args.Length; i++)
            {
                var current = args[i];
                if (!current.StartsWith("--"))
                    continue;
                var name = current.Substring(2); // strip leading dashes
                // Expect a value unless next token is also a flag or missing
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    var value = args[i + 1];
                    FlagDescriptors.Apply(flags, name, value);
                    i++; // skip value token
                }
            }
            consoleFlags = flags;
        }
    }
}

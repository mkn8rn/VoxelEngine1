using System;
using System.Collections.Generic;
using System.Globalization;
using MVoxelEngine1.Infrastructure.Models;

namespace MVoxelEngine1.Infrastructure.Flags
{
    internal sealed class FlagDescriptor
    {
        public string Name { get; }

        private readonly Action<ProgramFlags, string> _apply;
        public FlagDescriptor(string name, Action<ProgramFlags, string> apply)
        {
            Name = name;
            _apply = apply;
        }

        public void Apply(ProgramFlags flags, string value) => _apply(flags, value);
    }
}

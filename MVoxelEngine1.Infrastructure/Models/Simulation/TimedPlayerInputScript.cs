using System.Globalization;

namespace MVoxelEngine1.Infrastructure.Models.Simulation
{
    [Flags]
    public enum PlayerInputKeys
    {
        None = 0,
        W = 1 << 0,
        A = 1 << 1,
        S = 1 << 2,
        D = 1 << 3,
        Space = 1 << 4,
        LeftShift = 1 << 5
    }

    public readonly record struct TimedPlayerInputStep(
        PlayerInputKeys Keys,
        double DurationSeconds);

    public static class TimedPlayerInputScript
    {
        public const string DefaultScript = "W:2,Space:3";

        private static readonly (string Name, PlayerInputKeys Key)[] knownKeys =
        {
            ("W", PlayerInputKeys.W),
            ("A", PlayerInputKeys.A),
            ("S", PlayerInputKeys.S),
            ("D", PlayerInputKeys.D),
            ("Space", PlayerInputKeys.Space),
            ("LeftShift", PlayerInputKeys.LeftShift)
        };

        public static IReadOnlyList<TimedPlayerInputStep> Parse(string? script)
        {
            if (string.IsNullOrWhiteSpace(script))
                throw new FormatException("The timed input script is empty.");

            string[] tokens = script.Split(',', StringSplitOptions.TrimEntries);
            var steps = new TimedPlayerInputStep[tokens.Length];

            for (int index = 0; index < tokens.Length; index++)
            {
                string token = tokens[index];
                string[] parts = token.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
                    throw new FormatException($"Input step '{token}' must use keys:seconds.");

                PlayerInputKeys keys = ParseKeys(parts[0], token);
                if (!double.TryParse(
                        parts[1],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double durationSeconds) ||
                    !double.IsFinite(durationSeconds) ||
                    durationSeconds <= 0)
                {
                    throw new FormatException($"Input step '{token}' has an invalid duration.");
                }

                steps[index] = new TimedPlayerInputStep(keys, durationSeconds);
            }

            return steps;
        }

        public static IReadOnlyList<string> GetKeyNames(PlayerInputKeys keys)
        {
            var names = new List<string>(knownKeys.Length);
            foreach ((string name, PlayerInputKeys key) in knownKeys)
            {
                if ((keys & key) != 0)
                    names.Add(name);
            }

            return names;
        }

        private static PlayerInputKeys ParseKeys(string value, string token)
        {
            PlayerInputKeys keys = PlayerInputKeys.None;
            string[] names = value.Split('+', StringSplitOptions.TrimEntries);
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new FormatException($"Input step '{token}' contains an empty key.");

                PlayerInputKeys key = PlayerInputKeys.None;
                foreach ((string knownName, PlayerInputKeys knownKey) in knownKeys)
                {
                    if (name.Equals(knownName, StringComparison.OrdinalIgnoreCase))
                    {
                        key = knownKey;
                        break;
                    }
                }

                if (key == PlayerInputKeys.None)
                    throw new FormatException($"Input step '{token}' contains unknown key '{name}'.");
                if ((keys & key) != 0)
                    throw new FormatException($"Input step '{token}' contains key '{name}' more than once.");

                keys |= key;
            }

            return keys;
        }
    }
}

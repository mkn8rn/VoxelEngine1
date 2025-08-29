namespace MVGE_INF.Models.Generation
{
    public static class BlockProperties
    {
        // Simple direct lookup (ushort id -> opaque flag). Can be replaced later with a struct array.
        private static readonly bool[] _opaque = new bool[ushort.MaxValue + 1];

        static BlockProperties()
        {
            // Backwards compatibility: treat every non-zero id as opaque unless overridden.
            for (int i = 1; i < _opaque.Length; i++) _opaque[i] = true;
        }

        /// Returns true if the block id should occlude adjacent faces.
        public static bool IsOpaque(ushort id) => id != 0 && _opaque[id];

        /// Override opacity flag for a block id (can be called during content init).
        public static void SetOpaque(ushort id, bool opaque)
        {
            if (id < _opaque.Length) _opaque[id] = opaque;
        }
    }
}

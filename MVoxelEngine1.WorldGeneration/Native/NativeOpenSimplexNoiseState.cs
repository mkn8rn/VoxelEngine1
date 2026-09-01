using Noise = Supprocom.OpenSimplexNoise.OpenSimplexNoise;

namespace MVoxelEngine1.WorldGeneration.Native;

internal ref struct NativeOpenSimplexNoiseState
{
    internal const int EvaluationTableByteCount =
        Noise.PermutationTableLength * 4;
    internal const int StateByteCount =
        EvaluationTableByteCount + Noise.SourceScratchLength;

    private Span<byte> state;

    internal NativeOpenSimplexNoiseState(Span<byte> state)
    {
        if (state.Length < StateByteCount)
        {
            throw new ArgumentException(
                "The native OpenSimplexNoise state is incomplete.",
                nameof(state));
        }

        this.state = state[..StateByteCount];
    }

    internal ReadOnlySpan<byte> EvaluationTables =>
        state[..EvaluationTableByteCount];

    internal ReadOnlySpan<byte> Permutation =>
        state[..Noise.PermutationTableLength];

    internal ReadOnlySpan<byte> Permutation2D =>
        state.Slice(
            Noise.PermutationTableLength,
            Noise.PermutationTableLength);

    internal void Initialize(long seed) =>
        Noise.Initialize(
            seed,
            GetTable(0),
            GetTable(1),
            GetTable(2),
            GetTable(3),
            state.Slice(
                EvaluationTableByteCount,
                Noise.SourceScratchLength));

    internal double Evaluate2D(double x, double y) =>
        Noise.Evaluate(Permutation, Permutation2D, x, y);

    private Span<byte> GetTable(int tableIndex) =>
        state.Slice(
            checked(tableIndex * Noise.PermutationTableLength),
            Noise.PermutationTableLength);
}

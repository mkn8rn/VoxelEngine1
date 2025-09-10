using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.Terrain.Sections
{
    internal partial class SectionRender
    {
        // ------------------------------------------------------------------------------------
        // Bitset utilities
        // ------------------------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void BitsetShiftLeft(ReadOnlySpan<ulong> src, int bits, Span<ulong> dst)
        {
            int wordShift = bits >> 6;
            int bitShift = bits & 63;
            for (int i = 63; i >= 0; i--)
            {
                ulong v = 0;
                int si = i - wordShift;
                if (si >= 0)
                {
                    v = src[si];
                    if (bitShift != 0)
                    {
                        ulong carry = (si - 1 >= 0) ? src[si - 1] : 0UL;
                        v = (v << bitShift) | (carry >> (64 - bitShift));
                    }
                }
                dst[i] = v;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void BitsetShiftRight(ReadOnlySpan<ulong> src, int bits, Span<ulong> dst)
        {
            int wordShift = bits >> 6;
            int bitShift = bits & 63;
            for (int i = 0; i < 64; i++)
            {
                ulong v = 0;
                int si = i + wordShift;
                if (si < 64)
                {
                    v = src[si];
                    if (bitShift != 0)
                    {
                        ulong carry = (si + 1 < 64) ? src[si + 1] : 0UL;
                        v = (v >> bitShift) | (carry << (64 - bitShift));
                    }
                }
                dst[i] = v;
            }
        }

        // Iterate all set bits in a 4096-bit mask (64 ulongs) and invoke a callback with the linear index (li).
        // Optional bounds check (lx/ly/lz mins/maxs) avoids per-caller duplicate guards.
        internal static void ForEachSetBit(
            Span<ulong> mask,
            int lxMin, int lxMax, int lyMin, int lyMax, int lzMin, int lzMax,
            Action<int /*li*/, int /*lx*/, int /*ly*/, int /*lz*/> onBit)
        {
            // decode tables exist in the class (EnsureLiDecode defined in another partial)
            EnsureLiDecode();
            for (int wi = 0; wi < 64; wi++)
            {
                ulong word = mask[wi];
                while (word != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int li = (wi << 6) + bit;

                    int ly = _lyFromLi[li];
                    int t = li >> 4;
                    int lx = t & 15;
                    int lz = t >> 4;

                    if (lx < lxMin || lx > lxMax || ly < lyMin || ly > lyMax || lz < lzMin || lz > lzMax) continue;
                    onBit(li, lx, ly, lz);
                }
            }
        }

        // Plane bit helper (used by boundary reintroduction logic across paths).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool PlaneBit(ulong[] plane, int index)
        {
            if (plane == null) return false;
            int w = index >> 6; int b = index & 63; if (w >= plane.Length) return false;
            return (plane[w] & (1UL << b)) != 0UL;
        }

    }
}

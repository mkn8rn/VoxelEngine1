using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MVoxelEngine1.Infrastructure.Models.Generation;
using MVoxelEngine1.WorldGeneration.Terrain;

namespace MVoxelEngine1.WorldGeneration.Native;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeColumnSummary
{
    internal int HasMaterial;
    internal int MinimumMaterialStart;
    internal int MaximumMaterialEnd;
    internal int AllColumnsHaveStone;
    internal int StoneStartMinimum;
    internal int StoneStartMaximum;
    internal int StoneEndMinimum;
    internal int StoneEndMaximum;
    internal int AllColumnsHaveSoil;
    internal int SoilStartMinimum;
    internal int SoilStartMaximum;
    internal int SoilEndMinimum;
    internal int SoilEndMaximum;
    internal int AllColumnsHaveWater;
    internal int WaterStartMinimum;
    internal int WaterStartMaximum;
    internal int WaterEndMinimum;
    internal int WaterEndMaximum;
    internal static NativeColumnSummary CreateEmpty() => new()
    {
        MinimumMaterialStart = int.MaxValue,
        MaximumMaterialEnd = int.MinValue,
        AllColumnsHaveStone = 1,
        StoneStartMinimum = int.MaxValue,
        StoneStartMaximum = int.MinValue,
        StoneEndMinimum = int.MaxValue,
        StoneEndMaximum = int.MinValue,
        AllColumnsHaveSoil = 1,
        SoilStartMinimum = int.MaxValue,
        SoilStartMaximum = int.MinValue,
        SoilEndMinimum = int.MaxValue,
        SoilEndMaximum = int.MinValue,
        AllColumnsHaveWater = 1,
        WaterStartMinimum = int.MaxValue,
        WaterStartMaximum = int.MinValue,
        WaterEndMinimum = int.MaxValue,
        WaterEndMaximum = int.MinValue
    };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(scoped ref readonly BlockColumnProfile profile)
    {
        bool hasStone = profile.StoneStart >= 0 && profile.StoneEnd >= profile.StoneStart;
        bool hasSoil = profile.SoilStart >= 0 && profile.SoilEnd >= profile.SoilStart;
        bool hasWater = profile.WaterStart >= 0 && profile.WaterEnd >= profile.WaterStart;
        if (hasStone)
        {
            HasMaterial = 1;
            if (profile.StoneStart < MinimumMaterialStart)
                MinimumMaterialStart = profile.StoneStart;
            if (profile.StoneEnd > MaximumMaterialEnd)
                MaximumMaterialEnd = profile.StoneEnd;
            if (profile.StoneStart < StoneStartMinimum)
                StoneStartMinimum = profile.StoneStart;
            if (profile.StoneStart > StoneStartMaximum)
                StoneStartMaximum = profile.StoneStart;
            if (profile.StoneEnd < StoneEndMinimum)
                StoneEndMinimum = profile.StoneEnd;
            if (profile.StoneEnd > StoneEndMaximum)
                StoneEndMaximum = profile.StoneEnd;
        }
        else
        {
            AllColumnsHaveStone = 0;
        }
        FinishAddPhase(in profile, hasSoil, hasWater);
    }

    private void FinishAddPhase(scoped in global::MVoxelEngine1.Infrastructure.Models.Generation.BlockColumnProfile profile, bool hasSoil, bool hasWater)
    {

        if (hasSoil)
        {
            HasMaterial = 1;
            if (profile.SoilStart < MinimumMaterialStart)
                MinimumMaterialStart = profile.SoilStart;
            if (profile.SoilEnd > MaximumMaterialEnd)
                MaximumMaterialEnd = profile.SoilEnd;
            if (profile.SoilStart < SoilStartMinimum)
                SoilStartMinimum = profile.SoilStart;
            if (profile.SoilStart > SoilStartMaximum)
                SoilStartMaximum = profile.SoilStart;
            if (profile.SoilEnd < SoilEndMinimum)
                SoilEndMinimum = profile.SoilEnd;
            if (profile.SoilEnd > SoilEndMaximum)
                SoilEndMaximum = profile.SoilEnd;
        }
        else
        {
            AllColumnsHaveSoil = 0;
        }

        if (hasWater)
        {
            HasMaterial = 1;
            if (profile.WaterStart < MinimumMaterialStart)
                MinimumMaterialStart = profile.WaterStart;
            if (profile.WaterEnd > MaximumMaterialEnd)
                MaximumMaterialEnd = profile.WaterEnd;
            if (profile.WaterStart < WaterStartMinimum)
                WaterStartMinimum = profile.WaterStart;
            if (profile.WaterStart > WaterStartMaximum)
                WaterStartMaximum = profile.WaterStart;
            if (profile.WaterEnd < WaterEndMinimum)
                WaterEndMinimum = profile.WaterEnd;
            if (profile.WaterEnd > WaterEndMaximum)
                WaterEndMaximum = profile.WaterEnd;
        }
        else
        {
            AllColumnsHaveWater = 0;
        }

    }
}

using System.Runtime.CompilerServices;
using MVoxelEngine1.Infrastructure.Models.Generation;

namespace MVoxelEngine1.WorldGeneration.Native;
internal ref struct NativeGeneratedFaceWriter
{
    private readonly NativeTerrainMaterialSet materials;
    private readonly Span<uint> opaqueWords;
    private readonly Span<uint> transparentWords;
    private readonly bool writes;
    private readonly uint definedMaterials;
    private int currentMaterial;
    private bool currentOpaque;
    internal NativeGeneratedFaceWriter(NativeTerrainMaterialSet materials)
    {
        this.materials = materials;
        definedMaterials = GetDefinedMaterials(in materials);
        opaqueWords = default;
        transparentWords = default;
        writes = false;
        currentMaterial = 0;
        currentOpaque = true;
        Valid = true;
        OpaqueWordCount = 0;
        OpaqueFaceCount = 0;
        TransparentWordCount = 0;
        TransparentFaceCount = 0;
    }

    internal NativeGeneratedFaceWriter(NativeTerrainMaterialSet materials, Span<uint> opaqueWords, Span<uint> transparentWords)
    {
        this.materials = materials;
        definedMaterials = GetDefinedMaterials(in materials);
        this.opaqueWords = opaqueWords;
        this.transparentWords = transparentWords;
        writes = true;
        currentMaterial = 0;
        currentOpaque = true;
        Valid = true;
        OpaqueWordCount = 0;
        OpaqueFaceCount = 0;
        TransparentWordCount = 0;
        TransparentFaceCount = 0;
    }

    internal bool Valid { get; private set; }
    internal int OpaqueWordCount { get; private set; }
    internal int OpaqueFaceCount { get; private set; }
    internal int TransparentWordCount { get; private set; }
    internal int TransparentFaceCount { get; private set; }

    internal void SelectMaterial(int material, bool opaque)
    {
        if ((uint)material >= 3)
        {
            Valid = false;
            return;
        }

        currentMaterial = material;
        currentOpaque = opaque;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitMaterialYRange(int material, bool opaque, byte direction, int x, int startY, int endY, int z)
    {
        Emit(material, opaque, direction, x, startY, z, extentU: 1, extentV: endY - startY + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitRectangle(byte direction, int x, int y, int z, int extentU, int extentV) => Emit(currentMaterial, currentOpaque, direction, x, y, z, extentU, extentV);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitYRange(byte direction, int x, int startY, int endY, int z) => EmitRectangle(direction, x, startY, z, extentU: 1, extentV: endY - startY + 1);
    // Profile callers establish byte-sized coordinates and positive extents by
    // clipping to validated chunk dimensions before reaching these entry points.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitClippedProfileRectangle(byte direction, int x, int y, int z, int extentU, int extentV) =>
        Emit(currentMaterial, currentOpaque, direction, x, y, z, extentU, extentV, clippedProfile: true);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitClippedProfileYRange(int material, bool opaque, byte direction, int x, int startY, int endY, int z) =>
        Emit(material, opaque, direction, x, startY, z, 1, endY - startY + 1, clippedProfile: true);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitBlockRectangle(scoped in NativeBlockDescriptor descriptor, byte direction, int x, int y, int z, int extentU, int extentV) => EmitDescriptor(in descriptor, descriptor.HasFlag(NativeBlockFlags.Opaque), direction, x, y, z, extentU, extentV);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Emit(int material, bool opaque, byte direction, int x, int y, int z, int extentU, int extentV, bool clippedProfile = false)
    {
        if (!Valid || (uint)material >= 3 || (definedMaterials & (1u << material)) == 0)
        {
            Valid = false;
            return;
        }

        NativeBlockDescriptor descriptor = material switch
        {
            0 => materials.Stone,
            1 => materials.Soil,
            2 => materials.Water,
            _ => default
        };
        if (clippedProfile)
            WriteRectangle(in descriptor, opaque, direction, x, y, z, extentU, extentV);
        else
            EmitDefinedDescriptor(in descriptor, opaque, direction, x, y, z, extentU, extentV);
    }

    private static uint GetDefinedMaterials(scoped in NativeTerrainMaterialSet materials)
    {
        NativeBlockDescriptor stone = materials.Stone;
        NativeBlockDescriptor soil = materials.Soil;
        NativeBlockDescriptor water = materials.Water;
        return (IsDefined(in stone) ? 1u : 0u) |
            (IsDefined(in soil) ? 2u : 0u) |
            (IsDefined(in water) ? 4u : 0u);
    }

    private static bool IsDefined(scoped in NativeBlockDescriptor descriptor) =>
        descriptor.Id != 0 && descriptor.HasFlag(NativeBlockFlags.Defined);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitDescriptor(scoped in NativeBlockDescriptor descriptor, bool opaque, byte direction, int x, int y, int z, int extentU, int extentV)
    {
        if (!IsDefined(in descriptor))
        {
            Valid = false;
            return;
        }
        EmitDefinedDescriptor(in descriptor, opaque, direction, x, y, z, extentU, extentV);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitDefinedDescriptor(scoped in NativeBlockDescriptor descriptor, bool opaque, byte direction, int x, int y, int z, int extentU, int extentV)
    {
        if (!Valid || direction >= 6 || (uint)x > byte.MaxValue || (uint)y > byte.MaxValue || (uint)z > byte.MaxValue || (uint)(extentU - 1) > byte.MaxValue || (uint)(extentV - 1) > byte.MaxValue)
        {
            Valid = false;
            return;
        }
        WriteRectangle(in descriptor, opaque, direction, x, y, z, extentU, extentV);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteRectangle(scoped in NativeBlockDescriptor descriptor, bool opaque, byte direction,
        int x, int y, int z, int extentU, int extentV)
    {
        uint position = (uint)x | ((uint)y << 8) | ((uint)z << 16) | ((uint)direction << 24);
        uint attributes = (uint)(extentU - 1) | ((uint)(extentV - 1) << 8) | ((uint)descriptor.GetTile(direction) << 16);
        int faceCount = extentU * extentV;
        if (opaque)
        {
            int wordIndex = OpaqueWordCount;
            if (writes)
            {
                if ((uint)(wordIndex + 1) >= (uint)opaqueWords.Length)
                {
                    Valid = false;
                    return;
                }

                opaqueWords[wordIndex] = position;
                opaqueWords[wordIndex + 1] = attributes;
            }

            OpaqueWordCount = wordIndex + 2;
            OpaqueFaceCount += faceCount;
            return;
        }

        int transparentWordIndex = TransparentWordCount;
        if (writes)
        {
            if ((uint)(transparentWordIndex + 1) >= (uint)transparentWords.Length)
            {
                Valid = false;
                return;
            }

            transparentWords[transparentWordIndex] = position;
            transparentWords[transparentWordIndex + 1] = attributes;
        }

        TransparentWordCount = transparentWordIndex + 2;
        TransparentFaceCount += faceCount;
    }
}

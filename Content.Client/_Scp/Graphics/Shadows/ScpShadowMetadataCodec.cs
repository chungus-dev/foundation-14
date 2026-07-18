// Scp added start - compact shadow-atlas metadata transfer.
using System;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._Scp.Graphics.Shadows;

internal static class ScpShadowMetadataCodec
{
    public static void Encode(Vector4 normalized, out Rgba32 first, out Rgba32 second)
    {
        var x = Encode16(normalized.X);
        var y = Encode16(normalized.Y);
        var z = Encode16(normalized.Z);
        var w = Encode16(normalized.W);
        first = Pack(x, y);
        second = Pack(z, w);
    }

    /// <summary>
    /// Encodes two normalized values followed by two exact signed pixel offsets.
    /// The offsets use a biased unsigned 16-bit representation so zero and every
    /// integer atlas displacement in the signed 16-bit range round-trip exactly.
    /// </summary>
    public static void EncodeWithSignedPixelOffsets(
        Vector2 normalized,
        Vector2i pixelOffsets,
        out Rgba32 first,
        out Rgba32 second)
    {
        first = Pack(Encode16(normalized.X), Encode16(normalized.Y));
        second = Pack(
            EncodeSignedPixelOffset(pixelOffsets.X),
            EncodeSignedPixelOffset(pixelOffsets.Y));
    }

    public static Vector2i DecodeSignedPixelOffsets(in Rgba32 encoded)
    {
        return new Vector2i(
            DecodeSignedPixelOffset(encoded.R, encoded.G),
            DecodeSignedPixelOffset(encoded.B, encoded.A));
    }

    public static Vector4 Decode(in Rgba32 first, in Rgba32 second)
    {
        return new Vector4(
            Decode16(first.R, first.G),
            Decode16(first.B, first.A),
            Decode16(second.R, second.G),
            Decode16(second.B, second.A));
    }

    public static float NormalizeSignedOffset(float offsetPixels, float extentPixels)
    {
        if (!float.IsFinite(offsetPixels))
            throw new ArgumentOutOfRangeException(nameof(offsetPixels));
        if (!float.IsFinite(extentPixels) || extentPixels <= 0f)
            throw new ArgumentOutOfRangeException(nameof(extentPixels));

        return Math.Clamp(offsetPixels / extentPixels, -1f, 1f) * 0.5f + 0.5f;
    }

    public static float DecodeSignedOffset(float normalized, float extentPixels)
    {
        if (!float.IsFinite(normalized))
            throw new ArgumentOutOfRangeException(nameof(normalized));
        if (!float.IsFinite(extentPixels) || extentPixels <= 0f)
            throw new ArgumentOutOfRangeException(nameof(extentPixels));

        return (Math.Clamp(normalized, 0f, 1f) * 2f - 1f) * extentPixels;
    }

    public static float NormalizeAffine(float value, float minimum, float extent)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (!float.IsFinite(minimum))
            throw new ArgumentOutOfRangeException(nameof(minimum));
        if (!float.IsFinite(extent) || extent < 0f)
            throw new ArgumentOutOfRangeException(nameof(extent));

        if (extent == 0f)
            return 0f;

        return Math.Clamp((value - minimum) / extent, 0f, 1f);
    }

    public static float DecodeAffine(float normalized, float minimum, float extent)
    {
        if (!float.IsFinite(normalized))
            throw new ArgumentOutOfRangeException(nameof(normalized));
        if (!float.IsFinite(minimum))
            throw new ArgumentOutOfRangeException(nameof(minimum));
        if (!float.IsFinite(extent) || extent < 0f)
            throw new ArgumentOutOfRangeException(nameof(extent));

        return minimum + Math.Clamp(normalized, 0f, 1f) * extent;
    }

    private static ushort Encode16(float normalized)
    {
        if (!float.IsFinite(normalized))
            throw new ArgumentOutOfRangeException(nameof(normalized));

        return (ushort) MathF.Round(Math.Clamp(normalized, 0f, 1f) * ushort.MaxValue);
    }

    private static ushort EncodeSignedPixelOffset(int offset)
    {
        if (offset is < short.MinValue or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(offset));

        return (ushort) (offset - short.MinValue);
    }

    private static Rgba32 Pack(ushort first, ushort second)
    {
        return new Rgba32(
            (byte) (first >> 8),
            (byte) first,
            (byte) (second >> 8),
            (byte) second);
    }

    private static float Decode16(byte high, byte low)
    {
        return ((high << 8) | low) / (float) ushort.MaxValue;
    }

    private static int DecodeSignedPixelOffset(byte high, byte low)
    {
        return ((high << 8) | low) + short.MinValue;
    }
}
// Scp added end - compact shadow-atlas metadata transfer.

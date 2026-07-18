using System.Numerics;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

internal static class ScpPersistentShadowMath
{
    public static bool IsDeferredMaskUpdateDue(int deferredFrames, int pendingAge)
    {
        if (deferredFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(deferredFrames));

        // A freshly rebuilt geometry batch follows the same deadline as every
        // other dirty mask. A compatible committed mask remains valid until this
        // replacement is drawn, while entries without one already sort first.
        return deferredFrames == 0 || pendingAge >= deferredFrames;
    }

    public static long GetDeferredWorkBudget(
        long remainingWork,
        int deferredFrames,
        int oldestAge)
    {
        if (remainingWork < 0)
            throw new ArgumentOutOfRangeException(nameof(remainingWork));
        if (deferredFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(deferredFrames));

        var boundedAge = Math.Clamp(oldestAge, 0, deferredFrames);
        // Treat the configured maximum as a hard fallback deadline, not as a
        // normal work slot. Under steady churn this finishes by age N - 1, while
        // age N remains available for a single oversized task or a sudden burst.
        var remainingFrames = Math.Max(1, deferredFrames - boundedAge);
        var budget = remainingWork / remainingFrames;
        if (remainingWork % remainingFrames != 0)
            budget++;

        // A dirty entry with empty geometry still has atlas area to clear. Keep
        // both budget dimensions progressing even when one of them is zero.
        return Math.Max(1L, budget);
    }

    public static ScpShadowBasis GetBasis(
        Angle maskRotation,
        Angle entityRotation,
        bool hasMask,
        bool maskAutoRotate)
    {
        var rotation = hasMask
            ? maskRotation + (maskAutoRotate ? entityRotation : Angle.Zero)
            : Angle.Zero;
        var right = rotation.RotateVec(Vector2.UnitX);
        return new ScpShadowBasis(right, new Vector2(-right.Y, right.X));
    }

    public static Vector2 WorldToLightUv(
        Vector2 worldPosition,
        Vector2 lightPosition,
        float radius,
        in ScpShadowBasis basis)
    {
        if (radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));

        var relative = worldPosition - lightPosition;
        var inverseDiameter = 0.5f / radius;
        return new Vector2(
            0.5f + Vector2.Dot(relative, basis.Right) * inverseDiameter,
            0.5f - Vector2.Dot(relative, basis.Up) * inverseDiameter);
    }

    public static Vector2 LightUvToWorld(
        Vector2 uv,
        Vector2 lightPosition,
        float radius,
        in ScpShadowBasis basis)
    {
        if (radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));

        return lightPosition +
            basis.Right * ((uv.X - 0.5f) * 2f * radius) -
            basis.Up * ((uv.Y - 0.5f) * 2f * radius);
    }

    public static Vector2 GetLightDiameterPixels(
        float radius,
        in ScpShadowBasis basis,
        in Matrix3x2 targetMatrix)
    {
        if (radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));

        var rightPixels = Vector2.TransformNormal(basis.Right, targetMatrix);
        var upPixels = Vector2.TransformNormal(basis.Up, targetMatrix);
        return new Vector2(
            2f * radius * rightPixels.Length(),
            2f * radius * upPixels.Length());
    }

    public static bool TryGetRequestedSize(
        Vector2 diameter,
        float softness,
        out int padding,
        out Vector2i requestedSize)
    {
        padding = 0;
        requestedSize = default;
        if (!float.IsFinite(diameter.X) ||
            !float.IsFinite(diameter.Y) ||
            diameter.X <= 0f ||
            diameter.Y <= 0f ||
            !float.IsFinite(softness) ||
            softness < 0f)
        {
            return false;
        }

        var paddingPixels = ScpLightingBatchPlanner.GetSoftShadowPaddingPixels(softness);
        if (!float.IsFinite(paddingPixels))
            return false;

        var roundedPadding = MathF.Ceiling(paddingPixels);
        var roundedWidth = MathF.Ceiling(diameter.X);
        var roundedHeight = MathF.Ceiling(diameter.Y);
        if (roundedPadding > int.MaxValue / 2f ||
            roundedWidth > int.MaxValue - roundedPadding * 2f ||
            roundedHeight > int.MaxValue - roundedPadding * 2f)
        {
            return false;
        }

        padding = (int) roundedPadding;
        requestedSize = new Vector2i(
            (int) roundedWidth + padding * 2,
            (int) roundedHeight + padding * 2);
        return true;
    }

    public static ScpPersistentShadowLayout GetLayout(
        ScpShadowAtlasSlot slot,
        Vector2 diameter,
        int padding)
    {
        if (diameter.X <= 0f || diameter.Y <= 0f)
            throw new ArgumentOutOfRangeException(nameof(diameter));
        if (padding < 0)
            throw new ArgumentOutOfRangeException(nameof(padding));

        var roundedWidth = checked((int) MathF.Ceiling(diameter.X));
        var roundedHeight = checked((int) MathF.Ceiling(diameter.Y));
        var paddedWidth = checked(roundedWidth + padding * 2);
        var paddedHeight = checked(roundedHeight + padding * 2);
        if (paddedWidth > slot.Width || paddedHeight > slot.Height)
            throw new ArgumentException("The allocated slot is smaller than the exact padded mask.", nameof(slot));

        var paddedLeft = slot.X + (slot.Width - paddedWidth) / 2;
        var paddedTop = slot.Y + (slot.Height - paddedHeight) / 2;
        var innerLeft = paddedLeft + padding + (roundedWidth - diameter.X) * 0.5f;
        var innerTop = paddedTop + padding + (roundedHeight - diameter.Y) * 0.5f;

        return new ScpPersistentShadowLayout(
            new UIBox2i(
                paddedLeft,
                paddedTop,
                paddedLeft + paddedWidth,
                paddedTop + paddedHeight),
            new Box2(
                innerLeft,
                innerTop,
                innerLeft + diameter.X,
                innerTop + diameter.Y));
    }

    public static Vector2 LightUvToAtlasPixel(Vector2 uv, in ScpPersistentShadowLayout layout)
    {
        return new Vector2(
            layout.InnerBounds.Left + uv.X * layout.InnerBounds.Width,
            layout.InnerBounds.Bottom + uv.Y * layout.InnerBounds.Height);
    }
}

internal readonly record struct ScpShadowBasis(Vector2 Right, Vector2 Up);

internal readonly record struct ScpPersistentShadowLayout(UIBox2i PaddedBounds, Box2 InnerBounds);

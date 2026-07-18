using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Pure helpers shared by the lighting renderer and its deterministic unit tests.
/// </summary>
internal static class ScpLightingBatchPlanner
{
    private const float IntersectionTolerance = 0.00001f;

    public const int GeometryBatchSize = 16;
    public const int MaxVerticesPerDraw = 65_529;
    public const int VerticesPerLight = 6;

    /// <summary>
    /// Returns a viewport-stable affine range for metadata light centres. Unlike
    /// fitting the range to the visible light set, this does not change when the
    /// camera translates or PVS reorders otherwise identical lights.
    /// </summary>
    public static Vector4 GetStableLightCenterDecode(
        Vector2i targetSize,
        Vector2 targetPixelScale,
        float maximumRadius,
        float maximumSoftness)
    {
        if (targetSize.X <= 0 || targetSize.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetSize));
        if (!float.IsFinite(targetPixelScale.X) ||
            !float.IsFinite(targetPixelScale.Y) ||
            targetPixelScale.X < 0f ||
            targetPixelScale.Y < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPixelScale));
        }

        if (!float.IsFinite(maximumRadius) || maximumRadius < 0f)
            throw new ArgumentOutOfRangeException(nameof(maximumRadius));

        var padding = GetSoftShadowPaddingPixels(maximumSoftness);
        var maximumPixelExtent = maximumRadius * targetPixelScale + new Vector2(padding);
        var normalizedExtent = maximumPixelExtent / (Vector2) targetSize;
        return new Vector4(
            -normalizedExtent.X,
            -normalizedExtent.Y,
            1f + normalizedExtent.X * 2f,
            1f + normalizedExtent.Y * 2f);
    }

    public static float GetSoftShadowPaddingPixels(float softness)
    {
        return 1f + 3f * Math.Clamp(softness, 0f, 4f);
    }

    public static int GetDrawCountForVertices(int vertexCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        return vertexCount == 0
            ? 0
            : (vertexCount - 1) / MaxVerticesPerDraw + 1;
    }

    public static int PackShelves(
        Vector2i[] rectangles,
        int rectangleCount,
        Vector2i pageSize,
        ScpAtlasPlacement[] placements)
    {
        if (rectangleCount < 0 || rectangleCount > rectangles.Length)
            throw new ArgumentOutOfRangeException(nameof(rectangleCount));
        if (pageSize.X <= 0 || pageSize.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (placements.Length < rectangleCount)
            throw new ArgumentException("The placement span is smaller than the rectangle span.", nameof(placements));
        if (rectangleCount == 0)
            return 0;

        var page = 0;
        var cursorX = 0;
        var cursorY = 0;
        var rowHeight = 0;

        for (var i = 0; i < rectangleCount; i++)
        {
            var size = rectangles[i];
            if (size.X <= 0 || size.Y <= 0 || size.X > pageSize.X || size.Y > pageSize.Y)
                throw new ArgumentOutOfRangeException(nameof(rectangles));

            if (cursorX + size.X > pageSize.X)
            {
                cursorX = 0;
                cursorY += rowHeight;
                rowHeight = 0;
            }

            if (cursorY + size.Y > pageSize.Y)
            {
                page++;
                cursorX = 0;
                cursorY = 0;
                rowHeight = 0;
            }

            placements[i] = new ScpAtlasPlacement(
                page,
                UIBox2i.FromDimensions(cursorX, cursorY, size.X, size.Y));
            cursorX += size.X;
            rowHeight = Math.Max(rowHeight, size.Y);
        }

        return page + 1;
    }

    public static UIBox2i GetPlacementUnion(ReadOnlySpan<ScpAtlasPlacement> placements)
    {
        if (placements.IsEmpty)
            throw new ArgumentException("At least one atlas placement is required.", nameof(placements));

        var first = placements[0].Bounds;
        var left = first.Left;
        var top = first.Top;
        var right = first.Right;
        var bottom = first.Bottom;

        for (var i = 1; i < placements.Length; i++)
        {
            var bounds = placements[i].Bounds;
            left = Math.Min(left, bounds.Left);
            top = Math.Min(top, bounds.Top);
            right = Math.Max(right, bounds.Right);
            bottom = Math.Max(bottom, bounds.Bottom);
        }

        return new UIBox2i(left, top, right, bottom);
    }

    public static Vector2 RelocateWorldPoint(
        Vector2 worldPoint,
        in Matrix3x2 worldToPixels,
        in Matrix3x2 pixelsToWorld,
        Vector2 atlasPixelOffset)
    {
        var pixelPoint = Vector2.Transform(worldPoint, worldToPixels);
        return RelocatePixelPoint(pixelPoint, pixelsToWorld, atlasPixelOffset);
    }

    public static Vector2 RelocatePixelPoint(
        Vector2 pixelPoint,
        in Matrix3x2 pixelsToWorld,
        Vector2 atlasPixelOffset)
    {
        return Vector2.Transform(pixelPoint + atlasPixelOffset, pixelsToWorld);
    }

    /// <summary>
    /// Converts world coordinates directly into pixels relative to a light's
    /// source rectangle. Whole-pixel camera translations cancel with an equal
    /// source-rectangle translation, allowing an exact atlas-geometry cache hit.
    /// </summary>
    public static Matrix3x2 GetSourceRelativeTargetMatrix(
        in Matrix3x2 targetMatrix,
        Vector2i sourceTopLeft)
    {
        var result = targetMatrix;
        result.M31 -= sourceTopLeft.X;
        result.M32 -= sourceTopLeft.Y;
        return result;
    }

    /// <summary>
    /// Creates a source rectangle whose local geometry is independent of camera
    /// translation. The returned phase restores the exact sub-pixel translation
    /// when the cached vertices are placed in the atlas.
    /// </summary>
    public static bool TryGetTranslationInvariantMaskBounds(
        Vector2 screenCenter,
        Vector2 pixelExtent,
        Vector2i targetSize,
        out UIBox2i source,
        out Vector2 localCenter,
        out Vector2 phase)
    {
        if (pixelExtent.X < 0f || pixelExtent.Y < 0f)
            throw new ArgumentOutOfRangeException(nameof(pixelExtent));
        if (targetSize.X <= 0 || targetSize.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetSize));

        // Match a nearest-pixel choice without discarding the fractional part.
        // The extra pixel on either side keeps clipping stable while phase moves
        // through [-0.5, 0.5).
        var integerCenter = new Vector2i(
            (int) MathF.Floor(screenCenter.X + 0.5f),
            (int) MathF.Floor(screenCenter.Y + 0.5f));
        var extent = new Vector2i(
            (int) MathF.Ceiling(pixelExtent.X),
            (int) MathF.Ceiling(pixelExtent.Y));
        var topLeft = integerCenter - extent - Vector2i.One;
        var size = extent * 2 + new Vector2i(2, 2);
        source = UIBox2i.FromDimensions(topLeft, size);
        localCenter = new Vector2(extent.X + 1f, extent.Y + 1f);
        phase = screenCenter - (Vector2) integerCenter;

        // At a viewport edge the old source rectangle is clamped. Its clipping
        // plane then moves relative to the light, so translation cannot be
        // removed from the exact cache key.
        return source.Left >= 0 &&
               source.Top >= 0 &&
               source.Right <= targetSize.X &&
               source.Bottom <= targetSize.Y;
    }

    /// <summary>
    /// Converts world coordinates to light-local atlas pixels. Camera translation
    /// is deliberately omitted; <paramref name="localCenter"/> and the phase from
    /// <see cref="TryGetTranslationInvariantMaskBounds"/> reconstruct the exact
    /// source-relative coordinates.
    /// </summary>
    public static Matrix3x2 GetLightRelativeTargetMatrix(
        in Matrix3x2 targetMatrix,
        Vector2 lightPosition,
        Vector2 localCenter)
    {
        var result = targetMatrix;
        result.M31 = localCenter.X -
                     lightPosition.X * targetMatrix.M11 -
                     lightPosition.Y * targetMatrix.M21;
        result.M32 = localCenter.Y -
                     lightPosition.X * targetMatrix.M12 -
                     lightPosition.Y * targetMatrix.M22;
        return result;
    }

    public static int ClipTriangle(
        Vector2 first,
        Vector2 second,
        Vector2 third,
        in UIBox2 bounds,
        Vector2[] scratchA,
        Vector2[] scratchB,
        Vector2[] output)
    {
        if (scratchA.Length < 8 || scratchB.Length < 8 || output.Length < 8)
            throw new ArgumentException("Triangle clipping requires three arrays with space for eight vertices.");

        scratchA[0] = first;
        scratchA[1] = second;
        scratchA[2] = third;

        var count = ClipEdge(scratchA, 3, scratchB, 0, bounds.Left, true);
        count = ClipEdge(scratchB, count, scratchA, 1, bounds.Top, true);
        count = ClipEdge(scratchA, count, scratchB, 0, bounds.Right, false);
        count = ClipEdge(scratchB, count, scratchA, 1, bounds.Bottom, false);
        if (!ReferenceEquals(scratchA, output))
            Array.Copy(scratchA, output, count);
        return count;
    }

    public static ScpTriangleBoundsRelation ClassifyTriangle(
        Vector2 first,
        Vector2 second,
        Vector2 third,
        in UIBox2 bounds)
    {
        var firstCode = GetClipCode(first, bounds);
        var secondCode = GetClipCode(second, bounds);
        var thirdCode = GetClipCode(third, bounds);
        var union = firstCode | secondCode | thirdCode;

        if (union == 0)
            return ScpTriangleBoundsRelation.Inside;

        return (firstCode & secondCode & thirdCode) != 0
            ? ScpTriangleBoundsRelation.Outside
            : ScpTriangleBoundsRelation.Intersecting;
    }

    public static ScpAxisCandidateRange GetAxisCandidateRange(
        ReadOnlySpan<float> sortedCenters,
        float center,
        float radius,
        float maximumHalfExtent)
    {
        if (radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (maximumHalfExtent < 0f)
            throw new ArgumentOutOfRangeException(nameof(maximumHalfExtent));

        // Circle.Intersects accepts nearly touching bounds through CloseToPercent.
        // Keep this broad phase at least as conservative as that exact check.
        var radiusSquared = radius * radius;
        var toleratedRadiusSquared = MathF.Max(
            radiusSquared + IntersectionTolerance,
            radiusSquared / (1f - IntersectionTolerance));
        var extent = MathF.Sqrt(toleratedRadiusSquared) + maximumHalfExtent;
        return new ScpAxisCandidateRange(
            LowerBound(sortedCenters, center - extent),
            UpperBound(sortedCenters, center + extent));
    }

    public static int GetStandardLightDrawCount(ScpStandardLightPlanKey[] lights)
    {
        if (lights.Length == 0)
            return 0;

        var groups = new Dictionary<ScpStandardLightPlanKey, int>();
        for (var i = 0; i < lights.Length; i++)
        {
            var key = lights[i];
            groups.TryGetValue(key, out var count);
            groups[key] = count + 1;
        }

        var draws = 0;
        foreach (var count in groups.Values)
            draws += GetDrawCountForVertices(count * VerticesPerLight);
        return draws;
    }

    /// <summary>
    /// Calculates the ideal draw count when every fixed geometry batch fits one atlas page.
    /// Rectangle packing and oversized mask-vertex splitting are tested separately.
    /// </summary>
    public static int GetIdealShadowLightDrawCount(ScpShadowLightPlanKey[] lights)
    {
        var draws = 0;
        var groups = new Dictionary<ScpShadowLightPlanKey, int>(GeometryBatchSize);

        for (var start = 0; start < lights.Length; start += GeometryBatchSize)
        {
            groups.Clear();
            var count = Math.Min(GeometryBatchSize, lights.Length - start);
            draws++; // One packed mask for this geometry batch.

            for (var i = 0; i < count; i++)
            {
                var key = lights[start + i];
                groups.TryGetValue(key, out var groupLights);
                groups[key] = groupLights + 1;
            }

            foreach (var groupLights in groups.Values)
                draws += GetDrawCountForVertices(groupLights * VerticesPerLight);
        }

        return draws;
    }

    private static int ClipEdge(
        Vector2[] input,
        int count,
        Vector2[] output,
        int axis,
        float limit,
        bool keepGreater)
    {
        if (count == 0)
            return 0;

        var outputCount = 0;
        var previous = input[count - 1];
        var previousAxis = axis == 0 ? previous.X : previous.Y;
        var previousInside = keepGreater ? previousAxis >= limit : previousAxis <= limit;

        for (var i = 0; i < count; i++)
        {
            var current = input[i];
            var currentAxis = axis == 0 ? current.X : current.Y;
            var currentInside = keepGreater ? currentAxis >= limit : currentAxis <= limit;

            if (currentInside != previousInside)
            {
                var amount = (limit - previousAxis) / (currentAxis - previousAxis);
                output[outputCount++] = Vector2.Lerp(previous, current, amount);
            }

            if (currentInside)
                output[outputCount++] = current;

            previous = current;
            previousAxis = currentAxis;
            previousInside = currentInside;
        }

        return outputCount;
    }

    private static int GetClipCode(Vector2 point, in UIBox2 bounds)
    {
        var result = 0;
        if (point.X < bounds.Left)
            result |= 1;
        else if (point.X > bounds.Right)
            result |= 2;

        if (point.Y < bounds.Top)
            result |= 4;
        else if (point.Y > bounds.Bottom)
            result |= 8;

        return result;
    }

    private static int LowerBound(ReadOnlySpan<float> sortedValues, float value)
    {
        var first = 0;
        var count = sortedValues.Length;
        while (count > 0)
        {
            var step = count / 2;
            var index = first + step;
            if (sortedValues[index] < value)
            {
                first = index + 1;
                count -= step + 1;
            }
            else
            {
                count = step;
            }
        }

        return first;
    }

    private static int UpperBound(ReadOnlySpan<float> sortedValues, float value)
    {
        var first = 0;
        var count = sortedValues.Length;
        while (count > 0)
        {
            var step = count / 2;
            var index = first + step;
            if (sortedValues[index] <= value)
            {
                first = index + 1;
                count -= step + 1;
            }
            else
            {
                count = step;
            }
        }

        return first;
    }
}

internal enum ScpTriangleBoundsRelation : byte
{
    Inside,
    Outside,
    Intersecting,
}

internal readonly record struct ScpAxisCandidateRange(int Start, int End)
{
    public int Count => End - Start;
}

internal readonly record struct ScpAtlasPlacement(int Page, UIBox2i Bounds);

internal readonly record struct ScpStandardLightPlanKey(int Texture, float CurveFactor);

internal readonly record struct ScpShadowLightPlanKey(
    int Texture,
    float Falloff,
    float CurveFactor,
    float Softness,
    bool HasProtection);

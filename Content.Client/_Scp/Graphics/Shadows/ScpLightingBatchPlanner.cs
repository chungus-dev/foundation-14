using System.Numerics;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Pure helpers used by the lighting renderer.
/// </summary>
internal static class ScpLightingBatchPlanner
{
    private const float IntersectionTolerance = 0.00001f;

    public const int GeometryBatchSize = 16;
    public const int MaxVerticesPerDraw = 65_529;

    public static float GetSoftShadowPaddingPixels(float softness)
    {
        return 1f + 3f * Math.Clamp(softness, 0f, 4f);
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

    public static Vector2 RelocatePixelPoint(
        Vector2 pixelPoint,
        in Matrix3x2 pixelsToWorld,
        Vector2 atlasPixelOffset)
    {
        return Vector2.Transform(pixelPoint + atlasPixelOffset, pixelsToWorld);
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

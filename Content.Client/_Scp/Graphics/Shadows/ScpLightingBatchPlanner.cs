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
    public const int GeometryBatchSize = 16;
    public const int MaxVerticesPerDraw = 65_529;
    public const int VerticesPerLight = 6;

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
}

internal readonly record struct ScpAtlasPlacement(int Page, UIBox2i Bounds);

internal readonly record struct ScpStandardLightPlanKey(int Texture, float CurveFactor);

internal readonly record struct ScpShadowLightPlanKey(
    int Texture,
    float Falloff,
    float CurveFactor,
    float Softness,
    bool HasProtection);

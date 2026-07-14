using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const float GeometryEpsilon = 0.0001f;
    private const float NearLightDistance = 1f / EyeManager.PixelsPerMeter;

    #region Geometry buffers

    private readonly List<Entity<SpriteComponent, TransformComponent>> _spriteCandidates = new(256);
    private readonly List<Entity<OccluderComponent, TransformComponent>> _occluderCandidates = new(256);
    private readonly List<DrawVertexUV2DColor> _casterVertices = new(2048);
    private readonly List<DrawVertexUV2DColor> _occluderVertices = new(2048);
    private readonly Vector2[] _boxContour = new Vector2[4];
    private Vector2[] _worldContour = new Vector2[32];

    #endregion

    #region Caster collection

    private void BuildCasterMask(in LightData light, MapId mapId)
    {
        _spriteCandidates.Clear();
        _casterVertices.Clear();

        var radius = new Vector2(light.Component.Radius);
        var queryBounds = new Box2(light.Position - radius, light.Position + radius);
        _spriteTree.QueryAabb(_spriteCandidates, mapId, queryBounds);

        for (var i = 0; i < _spriteCandidates.Count; i++)
        {
            var candidate = _spriteCandidates[i];
            if (!_shadowQuery.TryGetComponent(candidate.Owner, out var shadow))
                continue;

            var quality = shadow.Kind == ScpShadowCasterKind.Mob ? _mobQuality : _objectQuality;
            if (quality == ScpShadowQuality.Disabled)
                continue;

            var sprite = candidate.Comp1;
            if (!sprite.Visible ||
                sprite.ContainerOccluded ||
                sprite.Color.A == 0 ||
                _occluderQuery.TryGetComponent(candidate.Owner, out var occluder) && occluder.Enabled)
            {
                continue;
            }

            AppendCasterShadow(candidate, shadow, quality, light);
        }
    }

    private void AppendCasterShadow(
        Entity<SpriteComponent, TransformComponent> caster,
        ScpShadowCasterVisualsComponent shadow,
        ScpShadowQuality quality,
        in LightData light)
    {
        if (caster.Owner == light.Owner)
            return;

        var (position, rotation) = _transformSystem.GetWorldPositionRotation(caster.Comp2);
        var matrices = GetSpriteMatrices(caster.Comp1, position, rotation);
        TransformBounds(shadow.Bounds, matrices.Sprite, _boxContour);

        if (ContainsOrNear(_boxContour, light.Position, NearLightDistance))
            return;

        if (quality == ScpShadowQuality.Bounds)
        {
            AppendShadowVolume(_boxContour, light.Position, light.Component.Radius, _casterVertices);
            return;
        }

        var originalVertexCount = _casterVertices.Count;
        var foundContour = false;
        var sprite = caster.Comp1;
        var overrideDirection = sprite.EnableDirectionOverride ? sprite.DirectionOverride : (Direction?) null;

        if (sprite.AllLayers is not IReadOnlyList<ISpriteLayer> layers)
        {
            AppendShadowVolume(_boxContour, light.Position, light.Component.Radius, _casterVertices);
            return;
        }

        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var spriteLayer = layers[layerIndex];
            if (spriteLayer is not Layer layer ||
                !_spriteSystem.IsVisible(layer) ||
                layer.Blank ||
                layer.Color.A == 0)
            {
                continue;
            }

            var rsi = layer.ActualRsi;
            RSI.State? state = null;
            if (rsi != null)
                rsi.TryGetState(layer.State, out state);

            var direction = state == null
                ? RsiDirection.South
                : Layer.GetDirection(state.RsiDirections, matrices.ScreenAngle);

            layer.GetLayerDrawMatrix(direction, out var layerMatrix);

            if (overrideDirection != null && state != null)
                direction = overrideDirection.Value.Convert(state.RsiDirections);
            direction = direction.OffsetRsiDir(layer.DirOffset);

            var contours = ScpShadowContours.Empty;
            var hasContours = state != null && rsi != null
                ? _contourCache.TryGetContours(
                    rsi,
                    layer.State,
                    direction,
                    layer.AnimationFrame,
                    quality,
                    out contours)
                : layer.Texture != null && _contourCache.TryGetContours(layer.Texture, quality, out contours);

            if (!hasContours)
                continue;

            foundContour = true;
            var layerBase = GetLayerBaseMatrix(layer.RenderingStrategy, sprite.GranularLayersRendering, matrices);
            var worldMatrix = Matrix3x2.Multiply(layerMatrix, layerBase);

            for (var loopIndex = 0; loopIndex < contours.Loops.Length; loopIndex++)
            {
                var localLoop = contours.Loops[loopIndex];
                EnsureWorldContourCapacity(localLoop.Length);

                for (var vertex = 0; vertex < localLoop.Length; vertex++)
                    _worldContour[vertex] = Vector2.Transform(localLoop[vertex], worldMatrix);

                var worldLoop = _worldContour.AsSpan(0, localLoop.Length);
                if (ContainsOrNear(worldLoop, light.Position, NearLightDistance))
                {
                    _casterVertices.RemoveRange(originalVertexCount, _casterVertices.Count - originalVertexCount);
                    return;
                }

                AppendShadowVolume(worldLoop, light.Position, light.Component.Radius, _casterVertices);
            }
        }

        if (!foundContour)
            AppendShadowVolume(_boxContour, light.Position, light.Component.Radius, _casterVertices);
    }

    #endregion

    #region Stock occluder collection

    private void BuildOccluderMask(in LightData light, MapId mapId)
    {
        _occluderCandidates.Clear();
        _occluderVertices.Clear();

        var radius = new Vector2(light.Component.Radius);
        var queryBounds = new Box2(light.Position - radius, light.Position + radius);
        _occluderSystem.QueryAabb(_occluderCandidates, mapId, queryBounds);

        var maximumOccluders = Math.Max(_configuration.GetCVar(CVars.MaxOccluderCount), 1024);
        var occluderCount = Math.Min(_occluderCandidates.Count, maximumOccluders);
        for (var i = 0; i < occluderCount; i++)
        {
            var candidate = _occluderCandidates[i];
            if (!candidate.Comp1.Enabled)
                continue;

            var worldMatrix = _transformSystem.GetWorldMatrix(candidate.Comp2);
            TransformBounds(candidate.Comp1.BoundingBox, worldMatrix, _boxContour);
            AppendFilledContour(_boxContour, _occluderVertices);
            AppendShadowVolume(_boxContour, light.Position, light.Component.Radius, _occluderVertices);
        }
    }

    #endregion

    #region Sprite transforms

    private SpriteMatrices GetSpriteMatrices(SpriteComponent sprite, Vector2 worldPosition, Angle worldRotation)
    {
        var angle = (worldRotation + _eyeRotation).Reduced().FlipPositive();
        var cardinal = sprite is { NoRotation: false, SnapCardinals: true }
            ? angle.RoundToCardinalAngle()
            : Angle.Zero;

        var entityMatrix = Matrix3Helpers.CreateTransform(
            worldPosition,
            sprite.NoRotation ? -_eyeRotation : worldRotation - cardinal);
        var spriteMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        if (!sprite.GranularLayersRendering)
            return new SpriteMatrices(angle, spriteMatrix, spriteMatrix, spriteMatrix, spriteMatrix);

        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, worldRotation);
        var defaultMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, worldRotation - angle.RoundToCardinalAngle());
        var snapMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, -_eyeRotation);
        var noRotationMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        return new SpriteMatrices(angle, spriteMatrix, defaultMatrix, snapMatrix, noRotationMatrix);
    }

    private static Matrix3x2 GetLayerBaseMatrix(
        LayerRenderingStrategy strategy,
        bool granular,
        in SpriteMatrices matrices)
    {
        if (!granular)
            return matrices.Sprite;

        return strategy switch
        {
            LayerRenderingStrategy.UseSpriteStrategy => matrices.Sprite,
            LayerRenderingStrategy.Default => matrices.Default,
            LayerRenderingStrategy.SnapToCardinals => matrices.SnapToCardinals,
            LayerRenderingStrategy.NoRotation => matrices.NoRotation,
            _ => matrices.Sprite,
        };
    }

    private static void TransformBounds(Box2 bounds, Matrix3x2 matrix, Span<Vector2> result)
    {
        result[0] = Vector2.Transform(bounds.BottomLeft, matrix);
        result[1] = Vector2.Transform(bounds.BottomRight, matrix);
        result[2] = Vector2.Transform(bounds.TopRight, matrix);
        result[3] = Vector2.Transform(bounds.TopLeft, matrix);
    }

    #endregion

    #region Shadow volumes

    private static void AppendShadowVolume(
        ReadOnlySpan<Vector2> contour,
        Vector2 lightPosition,
        float lightRadius,
        List<DrawVertexUV2DColor> vertices)
    {
        if (contour.Length < 2)
            return;

        var signedArea = 0f;
        for (var i = 0; i < contour.Length; i++)
        {
            var next = (i + 1) % contour.Length;
            signedArea += contour[i].X * contour[next].Y - contour[next].X * contour[i].Y;
        }

        var winding = signedArea >= 0f ? 1f : -1f;
        for (var i = 0; i < contour.Length; i++)
        {
            var next = (i + 1) % contour.Length;
            var start = contour[i];
            var end = contour[next];
            var edge = end - start;
            var outward = new Vector2(edge.Y, -edge.X) * winding;

            if (Vector2.Dot(lightPosition - (start + end) * 0.5f, outward) > 0f)
                continue;

            var farStart = ProjectToRadius(start, lightPosition, lightRadius);
            var farEnd = ProjectToRadius(end, lightPosition, lightRadius);
            AppendQuad(start, end, farEnd, farStart, vertices);
        }
    }

    private static Vector2 ProjectToRadius(Vector2 vertex, Vector2 lightPosition, float lightRadius)
    {
        var offset = vertex - lightPosition;
        var distanceSquared = offset.LengthSquared();
        if (distanceSquared <= GeometryEpsilon * GeometryEpsilon ||
            distanceSquared >= lightRadius * lightRadius)
        {
            return vertex;
        }

        return lightPosition + offset * (lightRadius / MathF.Sqrt(distanceSquared));
    }

    private static void AppendFilledContour(
        ReadOnlySpan<Vector2> contour,
        List<DrawVertexUV2DColor> vertices)
    {
        if (contour.Length < 3)
            return;

        for (var i = 1; i < contour.Length - 1; i++)
        {
            vertices.Add(new DrawVertexUV2DColor(contour[0], Color.White));
            vertices.Add(new DrawVertexUV2DColor(contour[i], Color.White));
            vertices.Add(new DrawVertexUV2DColor(contour[i + 1], Color.White));
        }
    }

    private static void AppendQuad(
        Vector2 bottomLeft,
        Vector2 bottomRight,
        Vector2 topRight,
        Vector2 topLeft,
        List<DrawVertexUV2DColor> vertices)
    {
        vertices.Add(new DrawVertexUV2DColor(bottomLeft, Color.White));
        vertices.Add(new DrawVertexUV2DColor(bottomRight, Color.White));
        vertices.Add(new DrawVertexUV2DColor(topRight, Color.White));
        vertices.Add(new DrawVertexUV2DColor(bottomLeft, Color.White));
        vertices.Add(new DrawVertexUV2DColor(topRight, Color.White));
        vertices.Add(new DrawVertexUV2DColor(topLeft, Color.White));
    }

    #endregion

    #region Point and edge checks

    private static bool ContainsOrNear(ReadOnlySpan<Vector2> contour, Vector2 point, float maximumDistance)
    {
        if (contour.Length < 2)
            return false;

        var inside = false;
        var maximumDistanceSquared = maximumDistance * maximumDistance;
        var previous = contour[^1];

        for (var i = 0; i < contour.Length; i++)
        {
            var current = contour[i];
            if (DistanceToSegmentSquared(point, previous, current) <= maximumDistanceSquared)
                return true;

            if ((current.Y > point.Y) != (previous.Y > point.Y) &&
                point.X < (previous.X - current.X) * (point.Y - current.Y) /
                (previous.Y - current.Y) + current.X)
            {
                inside = !inside;
            }

            previous = current;
        }

        return inside;
    }

    private static float DistanceToSegmentSquared(Vector2 point, Vector2 start, Vector2 end)
    {
        var edge = end - start;
        var lengthSquared = edge.LengthSquared();
        if (lengthSquared <= GeometryEpsilon * GeometryEpsilon)
            return Vector2.DistanceSquared(point, start);

        var amount = Math.Clamp(Vector2.Dot(point - start, edge) / lengthSquared, 0f, 1f);
        return Vector2.DistanceSquared(point, start + edge * amount);
    }

    private void EnsureWorldContourCapacity(int length)
    {
        if (_worldContour.Length >= length)
            return;

        Array.Resize(ref _worldContour, Math.Max(length, _worldContour.Length * 2));
    }

    #endregion

    #region Cached sprite matrices

    private readonly record struct SpriteMatrices(
        Angle ScreenAngle,
        Matrix3x2 Sprite,
        Matrix3x2 Default,
        Matrix3x2 SnapToCardinals,
        Matrix3x2 NoRotation);

    #endregion
}

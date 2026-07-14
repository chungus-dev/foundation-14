using System.Numerics;
using System.Runtime.InteropServices;
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

    #region Frame geometry cache

    private readonly List<Entity<SpriteComponent, TransformComponent>> _spriteCandidates = new(256);
    private readonly List<Entity<OccluderComponent, TransformComponent>> _occluderCandidates = new(256);
    private readonly List<CachedCaster> _frameCasters = new(256);
    private readonly List<CachedContour> _frameContours = new(512);
    private readonly List<Vector2> _frameContourVertices = new(4096);
    private readonly List<CachedOccluder> _frameOccluders = new(256);
    private readonly List<Vector2> _frameOccluderVertices = new(1024);
    private readonly List<ProtectedSpriteLayer> _protectedSpriteLayers = new(512);
    private readonly List<DrawVertexUV2DColor> _casterVertices = new(2048);
    private readonly List<DrawVertexUV2DColor> _localShadowVertices = new(1024);
    private readonly List<DrawVertexUV2DColor> _occluderVertices = new(2048);
    private readonly Vector2[] _boxContour = new Vector2[4];
    private Vector2[] _worldContour = new Vector2[32];

    private Box2 GetFrameQueryBounds(Box2 worldAabb)
    {
        var result = worldAabb;

        for (var i = 0; i < _lights.Count; i++)
        {
            var light = _lights[i];
            var radius = new Vector2(light.Component.Radius);
            result = result.Union(new Box2(light.Position - radius, light.Position + radius));
        }

        return result;
    }

    private Box2 GetFrameOccluderQueryBounds(Box2 worldAabb)
    {
        var result = worldAabb;

        // Match Clyde's occluder selection bounds: the viewport plus the centres of
        // every selected light. Light radii must not expand this query because that
        // would change which occluders survive the global cap.
        for (var i = 0; i < _lights.Count; i++)
            result = result.ExtendToContain(_lights[i].Position);

        return result;
    }

    private void BuildFrameOccluderCache(MapId mapId, Box2 queryBounds)
    {
        _occluderCandidates.Clear();
        _frameOccluders.Clear();
        _frameOccluderVertices.Clear();

        _occluderSystem.QueryAabb(_occluderCandidates, mapId, queryBounds);

        // Clyde applies this limit once while building the viewport's shared
        // occlusion geometry and never allows fewer than 1024 entries.
        var maximumOccluders = Math.Max(1024, _configuration.GetCVar(CVars.MaxOccluderCount));

        for (var i = 0; i < _occluderCandidates.Count && _frameOccluders.Count < maximumOccluders; i++)
        {
            var candidate = _occluderCandidates[i];
            if (!candidate.Comp1.Enabled)
                continue;

            var worldMatrix = _transformSystem.GetWorldMatrix(candidate.Comp2);
            TransformBounds(candidate.Comp1.BoundingBox, worldMatrix, _boxContour);

            var vertexStart = _frameOccluderVertices.Count;
            var minimum = new Vector2(float.PositiveInfinity);
            var maximum = new Vector2(float.NegativeInfinity);
            for (var vertex = 0; vertex < _boxContour.Length; vertex++)
            {
                var point = _boxContour[vertex];
                _frameOccluderVertices.Add(point);
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
            }

            _frameOccluders.Add(new CachedOccluder(
                vertexStart,
                new Box2(minimum, maximum),
                _transformSystem.GetInvWorldMatrix(candidate.Comp2),
                candidate.Comp1.BoundingBox,
                GetWinding(_boxContour)));
        }
    }

    private void BuildFrameCache(MapId mapId, Box2 queryBounds)
    {
        _spriteCandidates.Clear();
        _frameCasters.Clear();
        _frameContours.Clear();
        _frameContourVertices.Clear();
        _protectedSpriteLayers.Clear();

        _spriteTree.QueryAabb(_spriteCandidates, mapId, queryBounds);

        for (var i = 0; i < _spriteCandidates.Count; i++)
        {
            var candidate = _spriteCandidates[i];
            var sprite = candidate.Comp1;
            if (!sprite.Visible || sprite.ContainerOccluded || sprite.Color.A == 0f)
                continue;

            var isForeground = _foregroundQuery.HasComp(candidate.Owner);
            var isCaster = _shadowQuery.TryGetComponent(candidate.Owner, out var shadow);
            var quality = ScpShadowQuality.Disabled;

            if (isCaster)
            {
                quality = shadow!.Kind == ScpShadowCasterKind.Mob ? _mobQuality : _objectQuality;
                isCaster = quality != ScpShadowQuality.Disabled &&
                    (!_occluderQuery.TryGetComponent(candidate.Owner, out var occluder) || !occluder.Enabled);
            }

            if (!isCaster && !isForeground)
                continue;

            CacheSprite(candidate, isCaster ? shadow : null, quality, isForeground || isCaster);
        }
    }

    private void CacheSprite(
        Entity<SpriteComponent, TransformComponent> candidate,
        ScpShadowCasterVisualsComponent? shadow,
        ScpShadowQuality quality,
        bool protectSprite)
    {
        var sprite = candidate.Comp1;
        var (position, rotation) = _transformSystem.GetWorldPositionRotation(candidate.Comp2);
        var matrices = GetSpriteMatrices(sprite, position, rotation);

        var contourStart = _frameContours.Count;
        var hasContourBounds = false;
        var contourBounds = default(Box2);

        if (shadow != null)
        {
            TransformBounds(shadow.Bounds, matrices.Sprite, _boxContour);
            if (quality == ScpShadowQuality.Bounds)
            {
                contourBounds = CacheContour(_boxContour);
                hasContourBounds = true;
            }
        }

        if (sprite.AllLayers is IReadOnlyList<ISpriteLayer> layers)
        {
            var overrideDirection = sprite.EnableDirectionOverride ? sprite.DirectionOverride : (Direction?) null;

            for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                if (layers[layerIndex] is not Layer layer ||
                    !_spriteSystem.IsVisible(layer) ||
                    layer.Blank ||
                    layer.Color.A == 0f)
                {
                    continue;
                }

                var rsi = layer.ActualRsi;
                RSI.State? state = null;
                if (rsi != null)
                    rsi.TryGetState(layer.State, out state);

                var matrixDirection = state == null
                    ? RsiDirection.South
                    : Layer.GetDirection(state.RsiDirections, matrices.ScreenAngle);
                layer.GetLayerDrawMatrix(matrixDirection, out var layerMatrix);

                var drawDirection = matrixDirection;
                if (overrideDirection != null && state != null)
                    drawDirection = overrideDirection.Value.Convert(state.RsiDirections);
                drawDirection = drawDirection.OffsetRsiDir(layer.DirOffset);

                var layerBase = GetLayerBaseMatrix(layer.RenderingStrategy, sprite.GranularLayersRendering, matrices);
                var worldMatrix = Matrix3x2.Multiply(layerMatrix, layerBase);

                if (protectSprite)
                {
                    var texture = state?.GetFrame(drawDirection, layer.AnimationFrame) ??
                        layer.Texture ??
                        _spriteSystem.GetFallbackTexture();
                    var textureSize = texture.Size / (float) EyeManager.PixelsPerMeter;
                    var quad = Box2.FromDimensions(textureSize / -2f, textureSize);
                    _protectedSpriteLayers.Add(new ProtectedSpriteLayer(
                        texture,
                        worldMatrix,
                        quad,
                        sprite.Color * layer.Color));
                }

                if (shadow == null || quality is ScpShadowQuality.Bounds or ScpShadowQuality.Disabled)
                    continue;

                var contours = ScpShadowContours.Empty;
                var hasContours = state != null && rsi != null
                    ? _contourCache.TryGetContours(
                        rsi,
                        layer.State,
                        drawDirection,
                        layer.AnimationFrame,
                        quality,
                        out contours)
                    : layer.Texture != null && _contourCache.TryGetContours(layer.Texture, quality, out contours);

                if (!hasContours)
                    continue;

                for (var loopIndex = 0; loopIndex < contours.Loops.Length; loopIndex++)
                {
                    var localLoop = contours.Loops[loopIndex];
                    EnsureWorldContourCapacity(localLoop.Length);

                    for (var vertex = 0; vertex < localLoop.Length; vertex++)
                        _worldContour[vertex] = Vector2.Transform(localLoop[vertex], worldMatrix);

                    var cachedBounds = CacheContour(_worldContour.AsSpan(0, localLoop.Length));
                    contourBounds = hasContourBounds ? contourBounds.Union(cachedBounds) : cachedBounds;
                    hasContourBounds = true;
                }
            }
        }

        if (shadow == null)
            return;

        if (_frameContours.Count == contourStart)
        {
            TransformBounds(shadow.Bounds, matrices.Sprite, _boxContour);
            contourBounds = CacheContour(_boxContour);
            hasContourBounds = true;
        }

        if (hasContourBounds)
        {
            _frameCasters.Add(new CachedCaster(
                candidate.Owner,
                contourStart,
                _frameContours.Count - contourStart,
                contourBounds));
        }
    }

    private Box2 CacheContour(ReadOnlySpan<Vector2> contour)
    {
        var vertexStart = _frameContourVertices.Count;
        var minimum = new Vector2(float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity);
        var signedArea = 0f;

        for (var i = 0; i < contour.Length; i++)
        {
            var vertex = contour[i];
            var next = contour[(i + 1) % contour.Length];
            _frameContourVertices.Add(vertex);
            minimum = Vector2.Min(minimum, vertex);
            maximum = Vector2.Max(maximum, vertex);
            signedArea += vertex.X * next.Y - next.X * vertex.Y;
        }

        var bounds = new Box2(minimum, maximum);
        _frameContours.Add(new CachedContour(
            vertexStart,
            contour.Length,
            signedArea >= 0f ? 1f : -1f,
            bounds));
        return bounds;
    }

    #endregion

    #region Caster mask

    private void BuildCasterMask(in LightData light, EntityUid? excludedCaster, EntityUid? onlyCaster)
    {
        _casterVertices.Clear();
        var lightCircle = new Circle(light.Position, light.Component.Radius);

        for (var i = 0; i < _frameCasters.Count; i++)
        {
            var caster = _frameCasters[i];
            if (caster.Owner == light.Owner ||
                excludedCaster == caster.Owner ||
                onlyCaster != null && onlyCaster != caster.Owner ||
                !lightCircle.Intersects(caster.Bounds))
            {
                continue;
            }

            for (var contourIndex = 0; contourIndex < caster.ContourCount; contourIndex++)
            {
                var contour = _frameContours[caster.ContourStart + contourIndex];
                if (!lightCircle.Intersects(contour.Bounds))
                    continue;

                var vertices = CollectionsMarshal.AsSpan(_frameContourVertices)
                    .Slice(contour.VertexStart, contour.VertexCount);
                var projectionOrigin = GetSafeProjectionOrigin(
                    vertices,
                    contour.Winding,
                    light.Position,
                    NearLightDistance);
                AppendShadowVolume(
                    vertices,
                    contour.Winding,
                    projectionOrigin,
                    light.Position,
                    light.Component.Radius,
                    _casterVertices);
            }
        }
    }

    #endregion

    #region Stock occluder mask

    private void BuildOccluderMask(in LightData light)
    {
        _occluderVertices.Clear();

        var lightCircle = new Circle(light.Position, light.Component.Radius);
        for (var i = 0; i < _frameOccluders.Count; i++)
        {
            var occluder = _frameOccluders[i];
            if (!lightCircle.Intersects(occluder.Bounds))
                continue;

            var vertices = CollectionsMarshal.AsSpan(_frameOccluderVertices)
                .Slice(occluder.VertexStart, 4);
            AppendFilledContour(vertices, _occluderVertices);

            var projectionOrigin = GetSafeProjectionOrigin(
                vertices,
                occluder.Winding,
                light.Position,
                NearLightDistance);
            AppendShadowVolume(
                vertices,
                occluder.Winding,
                projectionOrigin,
                light.Position,
                light.Component.Radius,
                _occluderVertices);
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
        float winding,
        Vector2 projectionOrigin,
        Vector2 lightPosition,
        float lightRadius,
        List<DrawVertexUV2DColor> vertices)
    {
        if (contour.Length < 2)
            return;

        for (var i = 0; i < contour.Length; i++)
        {
            var next = (i + 1) % contour.Length;
            var start = contour[i];
            var end = contour[next];
            var edge = end - start;
            var outward = new Vector2(edge.Y, -edge.X) * winding;

            if (Vector2.Dot(projectionOrigin - (start + end) * 0.5f, outward) > 0f)
                continue;

            var farStart = ProjectToLightRadius(start, projectionOrigin, lightPosition, lightRadius);
            var farEnd = ProjectToLightRadius(end, projectionOrigin, lightPosition, lightRadius);
            AppendQuad(start, end, farEnd, farStart, vertices);
        }
    }

    private static Vector2 ProjectToLightRadius(
        Vector2 vertex,
        Vector2 projectionOrigin,
        Vector2 lightPosition,
        float lightRadius)
    {
        var direction = vertex - projectionOrigin;
        var directionLengthSquared = direction.LengthSquared();
        if (directionLengthSquared <= GeometryEpsilon * GeometryEpsilon)
            return vertex;

        var originOffset = projectionOrigin - lightPosition;
        var b = 2f * Vector2.Dot(originOffset, direction);
        var c = originOffset.LengthSquared() - lightRadius * lightRadius;
        var discriminant = b * b - 4f * directionLengthSquared * c;
        if (discriminant <= 0f)
            return vertex;

        var amount = (-b + MathF.Sqrt(discriminant)) / (2f * directionLengthSquared);
        if (!float.IsFinite(amount) || amount <= 1f)
            return vertex;

        return projectionOrigin + direction * amount;
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

    /// <summary>
    /// Moves a virtual projection origin outside a contour when a light touches or enters it.
    /// The real light position remains unchanged for attenuation and range clipping.
    /// </summary>
    private static Vector2 GetSafeProjectionOrigin(
        ReadOnlySpan<Vector2> contour,
        float winding,
        Vector2 lightPosition,
        float minimumDistance)
    {
        if (contour.Length < 2)
            return lightPosition;

        var inside = false;
        var nearestDistanceSquared = float.PositiveInfinity;
        var nearestPoint = lightPosition;
        var nearestOutward = Vector2.Zero;
        var previous = contour[^1];

        for (var i = 0; i < contour.Length; i++)
        {
            var current = contour[i];
            var edge = current - previous;
            var edgeLengthSquared = edge.LengthSquared();
            if (edgeLengthSquared > GeometryEpsilon * GeometryEpsilon)
            {
                var amount = Math.Clamp(
                    Vector2.Dot(lightPosition - previous, edge) / edgeLengthSquared,
                    0f,
                    1f);
                var point = previous + edge * amount;
                var distanceSquared = Vector2.DistanceSquared(lightPosition, point);
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestPoint = point;
                    nearestOutward = Vector2.Normalize(new Vector2(edge.Y, -edge.X) * winding);
                }
            }

            if ((current.Y > lightPosition.Y) != (previous.Y > lightPosition.Y) &&
                lightPosition.X < (previous.X - current.X) * (lightPosition.Y - current.Y) /
                (previous.Y - current.Y) + current.X)
            {
                inside = !inside;
            }

            previous = current;
        }

        if (!inside && nearestDistanceSquared > minimumDistance * minimumDistance)
            return lightPosition;

        if (nearestOutward.LengthSquared() <= GeometryEpsilon * GeometryEpsilon)
            return lightPosition;

        var result = nearestPoint + nearestOutward * (minimumDistance + GeometryEpsilon);
        return float.IsFinite(result.X) && float.IsFinite(result.Y) ? result : lightPosition;
    }

    private static float GetWinding(ReadOnlySpan<Vector2> contour)
    {
        var signedArea = 0f;
        for (var i = 0; i < contour.Length; i++)
        {
            var next = (i + 1) % contour.Length;
            signedArea += contour[i].X * contour[next].Y - contour[next].X * contour[i].Y;
        }

        return signedArea >= 0f ? 1f : -1f;
    }

    private void EnsureWorldContourCapacity(int length)
    {
        if (_worldContour.Length >= length)
            return;

        Array.Resize(ref _worldContour, Math.Max(length, _worldContour.Length * 2));
    }

    #endregion

    #region Cached geometry types

    private readonly record struct CachedCaster(
        EntityUid Owner,
        int ContourStart,
        int ContourCount,
        Box2 Bounds);

    private readonly record struct CachedContour(
        int VertexStart,
        int VertexCount,
        float Winding,
        Box2 Bounds);

    private readonly record struct CachedOccluder(
        int VertexStart,
        Box2 Bounds,
        Matrix3x2 InverseWorldMatrix,
        Box2 LocalBounds,
        float Winding);

    private readonly record struct ProtectedSpriteLayer(
        Texture Texture,
        Matrix3x2 WorldMatrix,
        Box2 Quad,
        Color Modulate);

    private readonly record struct SpriteMatrices(
        Angle ScreenAngle,
        Matrix3x2 Sprite,
        Matrix3x2 Default,
        Matrix3x2 SnapToCardinals,
        Matrix3x2 NoRotation);

    #endregion
}

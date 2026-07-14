using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const float GeometryEpsilon = 0.0001f;
    private const float NearLightDistance = 1f / EyeManager.PixelsPerMeter;
    private const float SpriteAlphaThreshold = 0.1f;

    #region Frame geometry cache

    private readonly List<CachedCaster> _frameCasters = new(256);
    private readonly List<CachedContour> _frameContours = new(512);
    private readonly List<Vector2> _frameContourVertices = new(4096);
    private readonly List<CachedOccluder> _frameOccluders = new(256);
    private readonly List<Vector2> _frameOccluderVertices = new(1024);
    private readonly List<ProtectedSpriteLayer> _protectedSpriteLayers = new(512);
    private readonly HashSet<EntityUid> _frameSpriteEntities = new(256);
    private readonly List<Box2> _spriteQueryBounds = new(16);
    private static readonly Color InsideMaskColor = new(1f, 0f, 0f, 1f);
    private static readonly Color OutsideMaskColor = new(0f, 1f, 0f, 1f);
    private static readonly Color BothMaskColor = new(1f, 1f, 0f, 1f);
    private static readonly Color OccluderMaskColor = new(0f, 0f, 1f, 1f);

    private readonly Dictionary<EntityUid, Vector2> _foregroundProjectionPositions = new(32);
    private readonly Vector2[] _boxContour = new Vector2[4];
    private Vector2[] _worldContour = new Vector2[32];
    private bool _hasOutsideFovCasters;
    private bool _outsideMaskMatchesInside;

    private Box2 GetFrameOccluderQueryBounds(Box2 worldAabb)
    {
        var result = worldAabb;

        // Match Clyde's occluder selection bounds: the viewport plus the centres of
        // every selected light. Light radii must not expand this query because that
        // would change which occluders survive the global cap.
        for (var i = 0; i < _lights.Count; i++)
            result = result.ExtendToContain(_lights[i].Position);

        for (var i = 0; i < _nonShadowLightPositions.Count; i++)
            result = result.ExtendToContain(_nonShadowLightPositions[i]);

        return result;
    }

    private void BuildFrameOccluderCache(MapId mapId, Box2 queryBounds)
    {
        _frameOccluders.Clear();
        _frameOccluderVertices.Clear();

        var state = new OccluderQueryState(this);
        foreach (var (treeUid, tree) in _occluderSystem.GetIntersectingTrees(mapId, queryBounds))
        {
            var localBounds = _transformSystem.GetInvWorldMatrix(treeUid).TransformBox(queryBounds);
            tree.Tree.QueryAabb(ref state, QueryOccluder, localBounds);
            if (_frameOccluders.Count >= _system.MaxOccluders)
                break;
        }
    }

    private static bool QueryOccluder(
        ref OccluderQueryState state,
        in ComponentTreeEntry<OccluderComponent> entry)
    {
        var overlay = state.Overlay;
        if (overlay._frameOccluders.Count >= overlay._system.MaxOccluders)
            return false;

        var occluder = entry.Component;
        if (!occluder.Enabled)
            return true;

        var worldMatrix = overlay._transformSystem.GetWorldMatrix(entry.Transform);
        TransformBounds(occluder.BoundingBox, worldMatrix, overlay._boxContour);

        var vertexStart = overlay._frameOccluderVertices.Count;
        var minimum = new Vector2(float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity);
        for (var vertex = 0; vertex < overlay._boxContour.Length; vertex++)
        {
            var point = overlay._boxContour[vertex];
            overlay._frameOccluderVertices.Add(point);
            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }

        overlay._frameOccluders.Add(new CachedOccluder(
            vertexStart,
            new Box2(minimum, maximum),
            GetWinding(overlay._boxContour)));
        return overlay._frameOccluders.Count < overlay._system.MaxOccluders;
    }

    private void BuildFrameCache(MapId mapId, Box2 viewportBounds)
    {
        _frameCasters.Clear();
        _frameContours.Clear();
        _frameContourVertices.Clear();
        _protectedSpriteLayers.Clear();
        _frameSpriteEntities.Clear();
        _spriteQueryBounds.Clear();
        _foregroundProjectionPositions.Clear();
        _hasOutsideFovCasters = false;
        _outsideMaskMatchesInside = true;

        AddSpriteQueryBounds(viewportBounds);

        for (var i = 0; i < _lights.Count; i++)
        {
            var light = _lights[i];
            var radius = new Vector2(light.Radius);
            AddSpriteQueryBounds(new Box2(light.Position - radius, light.Position + radius));
        }

        for (var i = 0; i < _spriteQueryBounds.Count; i++)
            QuerySpriteBounds(mapId, _spriteQueryBounds[i], viewportBounds);
    }

    private void AddSpriteQueryBounds(Box2 bounds)
    {
        for (var i = 0; i < _spriteQueryBounds.Count; i++)
        {
            var current = _spriteQueryBounds[i];
            if (current.Contains(bounds))
                return;

            if (!current.Intersects(bounds))
                continue;

            bounds = current.Union(bounds);
            _spriteQueryBounds.RemoveAt(i);
            i = -1;
        }

        _spriteQueryBounds.Add(bounds);
    }

    private void QuerySpriteBounds(MapId mapId, Box2 queryBounds, Box2 viewportBounds)
    {
        var state = new SpriteQueryState(this, viewportBounds);
        foreach (var (treeUid, tree) in _spriteTree.GetIntersectingTrees(mapId, queryBounds))
        {
            var localBounds = _transformSystem.GetInvWorldMatrix(treeUid).TransformBox(queryBounds);
            tree.Tree.QueryAabb(ref state, QuerySprite, localBounds, true);
        }
    }

    private static bool QuerySprite(
        ref SpriteQueryState state,
        in ComponentTreeEntry<SpriteComponent> entry)
    {
        var overlay = state.Overlay;
        var sprite = entry.Component;
        if (!sprite.Visible || sprite.ContainerOccluded || sprite.Color.A == 0f)
            return true;

        if (!overlay._frameSpriteEntities.Add(entry.Uid))
            return true;

        var isForeground = overlay._foregroundQuery.HasComp(entry.Uid);
        var isCaster = overlay._shadowQuery.TryGetComponent(entry.Uid, out var shadow);
        var quality = ScpShadowQuality.Disabled;

        if (isCaster)
        {
            quality = shadow!.Kind == ScpShadowCasterKind.Mob
                ? overlay._system.MobQuality
                : overlay._system.ObjectQuality;
            isCaster = quality != ScpShadowQuality.Disabled &&
                (!overlay._occluderQuery.TryGetComponent(entry.Uid, out var occluder) || !occluder.Enabled);
        }

        if (!isCaster && !isForeground)
            return true;

        overlay.CacheSprite(
            entry,
            isCaster ? shadow : null,
            quality,
            isForeground,
            state.ViewportBounds);
        return true;
    }

    private void CacheSprite(
        Entity<SpriteComponent, TransformComponent> candidate,
        ScpShadowCasterVisualsComponent? shadow,
        ScpShadowQuality quality,
        bool isForeground,
        Box2 viewportBounds)
    {
        var sprite = candidate.Comp1;
        var (position, rotation) = _transformSystem.GetWorldPositionRotation(candidate.Comp2);
        var matrices = GetSpriteMatrices(sprite, position, rotation);

        var contourStart = _frameContours.Count;
        var hasContourBounds = false;
        var contourBounds = default(Box2);
        var hasOpaqueBounds = false;
        var opaqueBounds = default(Box2);
        var spriteFovAlpha = 1f;
        var spriteFovAlphaReady = !_directionalFovActive;

        if (shadow != null)
        {
            if (quality == ScpShadowQuality.Bounds)
            {
                TransformBounds(shadow.Bounds, matrices.Sprite, _boxContour);
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
                    sprite.Color.A * layer.Color.A < SpriteAlphaThreshold)
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

                var texture = state?.GetFrame(drawDirection, layer.AnimationFrame) ??
                    layer.Texture ??
                    _spriteSystem.GetFallbackTexture();
                var textureSize = texture.Size / (float) EyeManager.PixelsPerMeter;
                var quad = Box2.FromDimensions(textureSize / -2f, textureSize);

                if (worldMatrix.TransformBox(quad).Intersects(viewportBounds))
                {
                    if (!spriteFovAlphaReady)
                    {
                        spriteFovAlpha = GetSpriteDirectionalFovAlpha(candidate.Owner, candidate.Comp2);
                        if (MathF.Abs(sprite.Color.A - sprite.Color.A * spriteFovAlpha) <= 0.01f)
                            spriteFovAlpha = 1f;
                        spriteFovAlphaReady = true;
                    }

                    var modulate = sprite.Color * layer.Color;
                    modulate = modulate.WithAlpha(modulate.A * spriteFovAlpha);
                    if (modulate.A >= SpriteAlphaThreshold)
                    {
                        _protectedSpriteLayers.Add(new ProtectedSpriteLayer(
                            texture,
                            worldMatrix,
                            quad,
                            modulate));
                    }
                }

                if (isForeground)
                {
                    var localOpaqueBounds = default(Box2);
                    var hasLayerBounds = state != null && rsi != null
                        ? _contourCache.TryGetOpaqueBounds(
                            rsi,
                            layer.State,
                            drawDirection,
                            layer.AnimationFrame,
                            out localOpaqueBounds)
                        : layer.Texture != null &&
                        _contourCache.TryGetOpaqueBounds(layer.Texture, out localOpaqueBounds);

                    if (hasLayerBounds)
                    {
                        var worldOpaqueBounds = worldMatrix.TransformBox(localOpaqueBounds);
                        opaqueBounds = hasOpaqueBounds
                            ? opaqueBounds.Union(worldOpaqueBounds)
                            : worldOpaqueBounds;
                        hasOpaqueBounds = true;
                    }
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
                        out contours)
                    : layer.Texture != null && _contourCache.TryGetContours(layer.Texture, out contours);

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

        if (isForeground && hasOpaqueBounds)
            _foregroundProjectionPositions[candidate.Owner] = opaqueBounds.Center;

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
            var fovVisibility = GetCasterDirectionalFovVisibility(
                candidate.Owner,
                candidate.Comp2);
            _frameCasters.Add(new CachedCaster(
                candidate.Owner,
                contourStart,
                _frameContours.Count - contourStart,
                contourBounds,
                fovVisibility));
            _hasOutsideFovCasters |= (fovVisibility & DirectionalFovVisibility.Outside) != 0;
            _outsideMaskMatchesInside &= fovVisibility == DirectionalFovVisibility.Both;
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

    private void BuildCasterMasks(
        in LightData light,
        bool buildOutsideMask,
        LightGeometryBuffer geometry)
    {
        var lightCircle = new Circle(light.Position, light.Radius);
        var projectionPosition = light.ProjectionPosition;

        for (var i = 0; i < _frameCasters.Count; i++)
        {
            var caster = _frameCasters[i];
            if (caster.Owner == light.Owner ||
                !lightCircle.Intersects(caster.Bounds))
            {
                continue;
            }

            var renderInside = (caster.FovVisibility & DirectionalFovVisibility.Inside) != 0;
            var renderOutside = buildOutsideMask &&
                (caster.FovVisibility & DirectionalFovVisibility.Outside) != 0;

            if (!renderInside && !renderOutside)
                continue;

            var maskColor = renderInside
                ? renderOutside ? BothMaskColor : InsideMaskColor
                : OutsideMaskColor;

            for (var contourIndex = 0; contourIndex < caster.ContourCount; contourIndex++)
            {
                var contour = _frameContours[caster.ContourStart + contourIndex];
                if (!lightCircle.Intersects(contour.Bounds))
                    continue;

                var vertices = CollectionsMarshal.AsSpan(_frameContourVertices)
                    .Slice(contour.VertexStart, contour.VertexCount);

                // Only the overlapping sprite layer is geometrically ambiguous. Skipping
                // the whole multi-layer caster makes the body disappear when a hand touches a lamp.
                if (contour.Bounds.Enlarged(NearLightDistance).Contains(projectionPosition) &&
                    ContainsOrNear(vertices, projectionPosition, NearLightDistance))
                {
                    continue;
                }

                var vertexStart = geometry.Vertices.Count;
                if (!AppendShadowVolume(
                    vertices,
                    contour.Winding,
                    projectionPosition,
                    light.Position,
                    light.Radius,
                    geometry.Vertices,
                    maskColor))
                {
                    continue;
                }

                geometry.ExtendCasterBounds(vertexStart, renderInside, renderOutside);
                geometry.HasInsideMask |= renderInside;
                geometry.HasOutsideMask |= renderOutside;
            }
        }
    }

    #endregion

    #region Stock occluder mask

    private void BuildOccluderMask(in LightData light, LightGeometryBuffer geometry)
    {
        var lightCircle = new Circle(light.Position, light.Radius);
        for (var i = 0; i < _frameOccluders.Count; i++)
        {
            var occluder = _frameOccluders[i];
            if (!lightCircle.Intersects(occluder.Bounds))
                continue;

            var vertices = CollectionsMarshal.AsSpan(_frameOccluderVertices)
                .Slice(occluder.VertexStart, 4);
            geometry.HasOccluderMask |= AppendFilledContour(
                vertices,
                geometry.Vertices,
                OccluderMaskColor);

            var projectionOrigin = occluder.Bounds.Enlarged(NearLightDistance).Contains(light.Position)
                ? GetSafeOccluderProjectionOrigin(
                    vertices,
                    occluder.Winding,
                    light.Position,
                    NearLightDistance)
                : light.Position;
            geometry.HasOccluderMask |= AppendShadowVolume(
                vertices,
                occluder.Winding,
                projectionOrigin,
                light.Position,
                light.Radius,
                geometry.Vertices,
                OccluderMaskColor);
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

    private static bool AppendShadowVolume(
        ReadOnlySpan<Vector2> contour,
        float winding,
        Vector2 projectionOrigin,
        Vector2 lightPosition,
        float lightRadius,
        List<DrawVertexUV2DColor> vertices,
        Color color)
    {
        if (contour.Length < 2)
            return false;

        var appended = false;

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
            AppendQuad(start, end, farEnd, farStart, vertices, color);
            appended = true;
        }

        return appended;
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

        if (projectionOrigin == lightPosition)
        {
            var radialAmount = lightRadius / MathF.Sqrt(directionLengthSquared);
            if (!float.IsFinite(radialAmount) || radialAmount <= 1f)
                return vertex;

            return lightPosition + direction * radialAmount;
        }

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

    private static bool AppendFilledContour(
        ReadOnlySpan<Vector2> contour,
        List<DrawVertexUV2DColor> vertices,
        Color color)
    {
        if (contour.Length < 3)
            return false;

        for (var i = 1; i < contour.Length - 1; i++)
        {
            vertices.Add(new DrawVertexUV2DColor(contour[0], color));
            vertices.Add(new DrawVertexUV2DColor(contour[i], color));
            vertices.Add(new DrawVertexUV2DColor(contour[i + 1], color));
        }

        return true;
    }

    private static void AppendQuad(
        Vector2 bottomLeft,
        Vector2 bottomRight,
        Vector2 topRight,
        Vector2 topLeft,
        List<DrawVertexUV2DColor> vertices,
        Color color)
    {
        vertices.Add(new DrawVertexUV2DColor(bottomLeft, color));
        vertices.Add(new DrawVertexUV2DColor(bottomRight, color));
        vertices.Add(new DrawVertexUV2DColor(topRight, color));
        vertices.Add(new DrawVertexUV2DColor(bottomLeft, color));
        vertices.Add(new DrawVertexUV2DColor(topRight, color));
        vertices.Add(new DrawVertexUV2DColor(topLeft, color));
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
            var edge = current - previous;
            var edgeLengthSquared = edge.LengthSquared();
            if (edgeLengthSquared > GeometryEpsilon * GeometryEpsilon)
            {
                var amount = Math.Clamp(Vector2.Dot(point - previous, edge) / edgeLengthSquared, 0f, 1f);
                var nearest = previous + edge * amount;
                if (Vector2.DistanceSquared(point, nearest) <= maximumDistanceSquared)
                    return true;
            }

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

    /// <summary>
    /// Moves a virtual projection origin outside a contour when a light touches or enters it.
    /// The real light position remains unchanged for attenuation and range clipping.
    /// </summary>
    private static Vector2 GetSafeOccluderProjectionOrigin(
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
        Box2 Bounds,
        DirectionalFovVisibility FovVisibility);

    private readonly record struct CachedContour(
        int VertexStart,
        int VertexCount,
        float Winding,
        Box2 Bounds);

    private readonly record struct CachedOccluder(
        int VertexStart,
        Box2 Bounds,
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

    private readonly struct OccluderQueryState(ScpShadowCasterOverlay overlay)
    {
        public readonly ScpShadowCasterOverlay Overlay = overlay;
    }

    private readonly struct SpriteQueryState(ScpShadowCasterOverlay overlay, Box2 viewportBounds)
    {
        public readonly ScpShadowCasterOverlay Overlay = overlay;
        public readonly Box2 ViewportBounds = viewportBounds;
    }

    #endregion
}

using System.Numerics;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterSystem
{
    // Keep the squared distance finite. Circle.Intersects uses an approximate
    // comparison, and float infinity would compare equal to itself.
    private static readonly Vector2 SuppressedLightOffset = new(1e10f, 1e10f);

    #region Viewport light snapshot

    [Dependency] private ILightManager _lightManager = default!;
    [Dependency] private IResourceCache _resourceCache = default!;

    private readonly List<ScpShadowLightData> _viewportLights = new(128);
    private readonly List<SuppressedLight> _suppressedLights = new(256);

    private LightTreeSystem _lightTree = default!;
    private SharedMapSystem _mapSystem = default!;
    private SharedTransformSystem _transformSystem = default!;
    private EntityQuery<MapComponent> _mapQuery;
    private IClydeViewport? _activeViewport;

    internal List<ScpShadowLightData> ViewportLights => _viewportLights;

    private void InitializeViewportLighting()
    {
        _lightTree = EntityManager.System<LightTreeSystem>();
        _mapSystem = EntityManager.System<SharedMapSystem>();
        _transformSystem = EntityManager.System<SharedTransformSystem>();
        _mapQuery = GetEntityQuery<MapComponent>();
    }

    private void ShutdownViewportLighting()
    {
        RestoreSuppressedLights();
        _viewportLights.Clear();
        _activeViewport = null;
    }

    internal bool BeginViewportLighting(IClydeViewport viewport)
    {
        if (!ContentLightingEnabled || _activeViewport != null)
            return false;

        var eye = viewport.Eye;
        if (eye == null || !eye.DrawLight || eye.Position.MapId == MapId.Nullspace)
            return false;

        if (!_lightManager.Enabled || !_lightManager.DrawLighting)
            return false;

        var mapUid = _mapSystem.GetMapOrInvalid(eye.Position.MapId);
        if (!_mapQuery.TryGetComponent(mapUid, out var map) || !map.LightingEnabled)
            return false;

        _viewportLights.Clear();
        _suppressedLights.Clear();
        _activeViewport = viewport;

        try
        {
            var worldBounds = GetViewportWorldBounds(viewport, eye);
            var worldAabb = worldBounds.CalcBoundingBox();
            var state = new LightSnapshotQueryState(this, worldAabb);
            var queryBounds = worldAabb.Enlarged(MaxLightRadius);

            foreach (var (treeUid, tree) in _lightTree.GetIntersectingTrees(eye.Position.MapId, queryBounds))
            {
                var localBounds = _transformSystem.GetInvWorldMatrix(treeUid).TransformBox(worldBounds);
                tree.Tree.QueryAabb(ref state, QueryAndSuppressLight, localBounds);
            }

            ApplyShadowLightLimit(state.ShadowLights);
            return true;
        }
        catch
        {
            RestoreSuppressedLights();
            _viewportLights.Clear();
            _activeViewport = null;
            throw;
        }
    }

    internal void EndViewportLighting()
    {
        RestoreSuppressedLights();
        _activeViewport = null;
    }

    internal bool IsLightingViewport(IClydeViewport viewport)
    {
        return ReferenceEquals(_activeViewport, viewport);
    }

    private static bool QueryAndSuppressLight(
        ref LightSnapshotQueryState state,
        in ComponentTreeEntry<PointLightComponent> entry)
    {
        var system = state.System;
        var light = entry.Component;
        var originalOffset = light.Offset;

        system._suppressedLights.Add(new SuppressedLight(light, originalOffset));

        var (entityPosition, entityRotation) =
            system._transformSystem.GetWorldPositionRotation(entry.Transform);
        var lightPosition = entityPosition + entityRotation.RotateVec(originalOffset);

        if (state.AcceptedLights < system.MaxLights &&
            light.Enabled &&
            !light.ContainerOccluded &&
            new Circle(lightPosition, light.Radius).Intersects(state.WorldAabb))
        {
            state.AcceptedLights++;
            if (light.CastShadows)
                state.ShadowLights++;

            var mask = light.MaskPath == null
                ? null
                : system._resourceCache.GetResource<TextureResource>(light.MaskPath).Texture;

            system._viewportLights.Add(new ScpShadowLightData(
                entry.Uid,
                lightPosition,
                lightPosition,
                entityRotation,
                light.Color,
                mask,
                light.Rotation,
                light.Radius,
                light.Energy,
                light.Falloff,
                light.CurveFactor,
                light.Softness,
                light.MaskAutoRotate,
                light.CastShadows,
                Vector2.DistanceSquared(lightPosition, state.WorldAabb.Center)));
        }

        // ponytail: Clyde has no per-viewport PointLight gate. Its callback reads the
        // live offset after the tree query, so a temporary finite sentinel culls the
        // entry without dirtying network state or rebuilding the tree.
        light.Offset = SuppressedLightOffset;
        return true;
    }

    private void ApplyShadowLightLimit(int shadowLights)
    {
        if (shadowLights <= MaxShadowLights)
            return;

        _viewportLights.Sort(ScpLightCapacityComparer.Instance);
        var shadowStart = _viewportLights.Count - shadowLights;
        _viewportLights.Sort(
            shadowStart,
            shadowLights,
            ScpShadowDistanceComparer.Instance);
        _viewportLights.RemoveRange(
            shadowStart + MaxShadowLights,
            shadowLights - MaxShadowLights);
    }

    private void RestoreSuppressedLights()
    {
        for (var i = 0; i < _suppressedLights.Count; i++)
        {
            var suppressed = _suppressedLights[i];
            suppressed.Component.Offset = suppressed.Offset;
        }

        _suppressedLights.Clear();
    }

    private static Box2Rotated GetViewportWorldBounds(IClydeViewport viewport, IEye eye)
    {
        var size = viewport.Size / viewport.RenderScale /
            EyeManager.PixelsPerMeter * eye.Zoom;
        var bounds = Box2.CenteredAround(
            eye.Position.Position + eye.Offset,
            size);
        return new Box2Rotated(bounds, -eye.Rotation, bounds.Center);
    }

    private readonly record struct SuppressedLight(
        PointLightComponent Component,
        Vector2 Offset);

    private struct LightSnapshotQueryState(
        ScpShadowCasterSystem system,
        Box2 worldAabb)
    {
        public readonly ScpShadowCasterSystem System = system;
        public readonly Box2 WorldAabb = worldAabb;
        public int AcceptedLights;
        public int ShadowLights;
    }

    private sealed class ScpLightCapacityComparer : IComparer<ScpShadowLightData>
    {
        public static readonly ScpLightCapacityComparer Instance = new();

        public int Compare(ScpShadowLightData left, ScpShadowLightData right)
        {
            if (left.CastShadows == right.CastShadows)
                return 0;

            return left.CastShadows ? 1 : -1;
        }
    }

    private sealed class ScpShadowDistanceComparer : IComparer<ScpShadowLightData>
    {
        public static readonly ScpShadowDistanceComparer Instance = new();

        public int Compare(ScpShadowLightData left, ScpShadowLightData right)
        {
            return left.DistanceSquared.CompareTo(right.DistanceSquared);
        }
    }

    #endregion
}

internal readonly record struct ScpShadowLightData(
    EntityUid Owner,
    Vector2 Position,
    Vector2 ProjectionPosition,
    Angle EntityRotation,
    Color Color,
    Texture? Mask,
    Angle MaskRotation,
    float Radius,
    float Energy,
    float Falloff,
    float CurveFactor,
    float Softness,
    bool MaskAutoRotate,
    bool CastShadows,
    float DistanceSquared);

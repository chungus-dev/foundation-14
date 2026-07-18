using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Timing;

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
    // Scp added - keep MaxLights selection independent from LightTree/PVS traversal order.
    private readonly List<ScpPointLightCandidate> _viewportLightCandidates = new(128);
    private static readonly Comparison<ScpPointLightCandidate> PointLightCandidateComparison =
        ScpPointLightCandidateComparer.Instance.Compare;
    private static readonly Comparison<ScpShadowLightData> LightCapacityComparison =
        ScpLightCapacityComparer.Instance.Compare;
    private static readonly Comparison<ScpShadowLightData> ShadowDistanceComparison =
        ScpShadowDistanceComparer.Instance.Compare;
    private bool _viewportLightCandidatesHeapified;
    private readonly List<SuppressedLight> _suppressedLights = new(256);
    private List<Entity<MapGridComponent>> _intersectingLightTreeGrids = new(4);

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
        _viewportLightCandidates.Clear();
        _viewportLightCandidatesHeapified = false;
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

        // With both content shadow qualities disabled there is no sprite shadow
        // work to add. Let Clyde render its native lights and occluders directly;
        // suppressing them only to rebuild the same occluder pass in content costs
        // an extra light-tree query and an extra geometry pass.
        if (_lightManager.DrawShadows &&
            MobQuality == ScpShadowQuality.Disabled &&
            ObjectQuality == ScpShadowQuality.Disabled)
        {
            _viewportLights.Clear();
            return false;
        }

        var mapUid = _mapSystem.GetMapOrInvalid(eye.Position.MapId);
        if (!_mapQuery.TryGetComponent(mapUid, out var map) || !map.LightingEnabled)
            return false;

        using var profile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.Snapshot")
            : (Robust.Shared.Profiling.ProfManager.GroupGuard?) null;

        _viewportLights.Clear();
        _viewportLightCandidates.Clear();
        _viewportLightCandidatesHeapified = false;
        _suppressedLights.Clear();
        _activeViewport = viewport;

        try
        {
            var worldBounds = GetViewportWorldBounds(viewport, eye);
            var worldAabb = worldBounds.CalcBoundingBox();
            var state = new LightSnapshotQueryState(this, worldAabb);
            var queryBounds = worldAabb.Enlarged(MaxLightRadius);

            _lightTree.UpdateTreePositions();
            _intersectingLightTreeGrids.Clear();
            _mapSystem.FindGridsIntersecting(
                eye.Position.MapId,
                queryBounds,
                ref _intersectingLightTreeGrids,
                includeMap: false);
            for (var i = 0; i < _intersectingLightTreeGrids.Count; i++)
            {
                var treeUid = _intersectingLightTreeGrids[i].Owner;
                if (!TryComp(treeUid, out LightTreeComponent? tree))
                    continue;

                var localBounds = _transformSystem.GetInvWorldMatrix(treeUid).TransformBox(worldBounds);
                tree.Tree.QueryAabb(ref state, QueryAndSuppressLight, localBounds);
            }

            if (TryComp(mapUid, out LightTreeComponent? mapTree))
            {
                var localBounds = _transformSystem.GetInvWorldMatrix(mapUid).TransformBox(worldBounds);
                mapTree.Tree.QueryAabb(ref state, QueryAndSuppressLight, localBounds);
            }

            FinalizeViewportLights();
            return true;
        }
        catch
        {
            RestoreSuppressedLights();
            _viewportLights.Clear();
            _viewportLightCandidates.Clear();
            _activeViewport = null;
            throw;
        }
    }

    internal void EndViewportLighting()
    {
        using var profile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.Restore")
            : (Robust.Shared.Profiling.ProfManager.GroupGuard?) null;
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

        if (light.Enabled &&
            !light.ContainerOccluded &&
            new Circle(lightPosition, light.Radius).Intersects(state.WorldAabb))
        {
            // Scp added start - retain only the deterministic best K visible lights.
            var candidate = new ScpPointLightCandidate(
                entry.Uid,
                light.CreationTick,
                light,
                lightPosition,
                entityRotation,
                Vector2.DistanceSquared(lightPosition, state.WorldAabb.Center));
            system.ConsiderViewportLight(in candidate);
            // Scp added end
        }

        // ponytail: Clyde has no per-viewport PointLight gate. Its callback reads the
        // live offset after the tree query, so a temporary finite sentinel culls the
        // entry without dirtying network state or rebuilding the tree.
        light.Offset = SuppressedLightOffset;
        return true;
    }

    // Scp added start - bounded, allocation-free top-K for stable PVS churn behavior.
    private void ConsiderViewportLight(in ScpPointLightCandidate candidate)
    {
        if (MaxLights <= 0)
            return;

        if (_viewportLightCandidates.Count < MaxLights)
        {
            _viewportLightCandidates.Add(candidate);
            return;
        }

        // Most viewports are below MaxLights. Build the bounded max-heap only
        // when a query actually overflows instead of maintaining it for every
        // ordinary light and then sorting the same list again.
        if (!_viewportLightCandidatesHeapified)
        {
            HeapifyViewportLights();
            _viewportLightCandidatesHeapified = true;
        }

        // The max-heap root is the worst currently selected light.
        if (ScpPointLightCandidateComparer.Instance.Compare(candidate, _viewportLightCandidates[0]) >= 0)
            return;

        _viewportLightCandidates[0] = candidate;
        SiftViewportLightDown(0);
    }

    private void HeapifyViewportLights()
    {
        for (var index = _viewportLightCandidates.Count / 2 - 1; index >= 0; index--)
            SiftViewportLightDown(index);
    }

    private void SiftViewportLightDown(int index)
    {
        var count = _viewportLightCandidates.Count;
        var candidate = _viewportLightCandidates[index];
        while (true)
        {
            var left = index * 2 + 1;
            if (left >= count)
                break;

            var worstChild = left;
            var right = left + 1;
            if (right < count &&
                ScpPointLightCandidateComparer.Instance.Compare(
                    _viewportLightCandidates[right],
                    _viewportLightCandidates[left]) > 0)
            {
                worstChild = right;
            }

            if (ScpPointLightCandidateComparer.Instance.Compare(candidate, _viewportLightCandidates[worstChild]) >= 0)
                break;

            _viewportLightCandidates[index] = _viewportLightCandidates[worstChild];
            index = worstChild;
        }

        _viewportLightCandidates[index] = candidate;
    }

    private void FinalizeViewportLights()
    {
        // Stable ordering is required when MaxLights actually cuts the set. Below
        // the cap, frame order owns no cache state: persistent/wide shadows sort by
        // identity themselves and standard-light blending is purely additive.
        if (_viewportLightCandidatesHeapified)
            _viewportLightCandidates.Sort(PointLightCandidateComparison);

        var shadowLights = 0;
        for (var i = 0; i < _viewportLightCandidates.Count; i++)
        {
            var candidate = _viewportLightCandidates[i];
            var light = candidate.Component;
            if (light.CastShadows)
                shadowLights++;

            var mask = light.MaskPath == null
                ? null
                : _resourceCache.GetResource<TextureResource>(light.MaskPath).Texture;

            _viewportLights.Add(new ScpShadowLightData(
                candidate.Owner,
                candidate.CreationTick,
                candidate.Position,
                candidate.Position,
                candidate.EntityRotation,
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
                candidate.DistanceSquared));
        }

        _viewportLightCandidates.Clear();
        _viewportLightCandidatesHeapified = false;
        ApplyShadowLightLimit(shadowLights);
    }
    // Scp added end

    private void ApplyShadowLightLimit(int shadowLights)
    {
        if (shadowLights <= MaxShadowLights)
            return;

        _viewportLights.Sort(LightCapacityComparison);
        var shadowStart = _viewportLights.Count - shadowLights;
        CollectionsMarshal.AsSpan(_viewportLights)
            .Slice(shadowStart, shadowLights)
            .Sort(ShadowDistanceComparison);
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
    }

    // Scp added start - stable PointLight identity is the final top-K tie-break.
    private readonly record struct ScpPointLightCandidate(
        EntityUid Owner,
        GameTick CreationTick,
        PointLightComponent Component,
        Vector2 Position,
        Angle EntityRotation,
        float DistanceSquared);

    private sealed class ScpPointLightCandidateComparer : IComparer<ScpPointLightCandidate>
    {
        public static readonly ScpPointLightCandidateComparer Instance = new();

        public int Compare(ScpPointLightCandidate left, ScpPointLightCandidate right)
        {
            var comparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
            if (comparison != 0)
                return comparison;

            comparison = left.Owner.CompareTo(right.Owner);
            return comparison != 0
                ? comparison
                : left.CreationTick.CompareTo(right.CreationTick);
        }
    }
    // Scp added end

    private sealed class ScpLightCapacityComparer : IComparer<ScpShadowLightData>
    {
        public static readonly ScpLightCapacityComparer Instance = new();

        public int Compare(ScpShadowLightData left, ScpShadowLightData right)
        {
            if (left.CastShadows != right.CastShadows)
                return left.CastShadows ? 1 : -1;

            return CompareStablePriority(left, right);
        }
    }

    private sealed class ScpShadowDistanceComparer : IComparer<ScpShadowLightData>
    {
        public static readonly ScpShadowDistanceComparer Instance = new();

        public int Compare(ScpShadowLightData left, ScpShadowLightData right)
        {
            return CompareStablePriority(left, right);
        }
    }

    // Scp added - stabilize both the total-light and shadow-light capacity cuts.
    private static int CompareStablePriority(ScpShadowLightData left, ScpShadowLightData right)
    {
        var comparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
        if (comparison != 0)
            return comparison;

        comparison = left.Owner.CompareTo(right.Owner);
        return comparison != 0
            ? comparison
            : left.CreationTick.CompareTo(right.CreationTick);
    }

    #endregion
}

internal readonly record struct ScpShadowLightData(
    EntityUid Owner,
    GameTick CreationTick,
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

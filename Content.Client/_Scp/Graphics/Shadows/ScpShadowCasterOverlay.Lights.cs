using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared;
using Robust.Shared.ComponentTrees;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    #region Light query state

    private readonly List<Entity<PointLightComponent, TransformComponent>> _lightCandidates = new(128);
    private readonly List<LightData> _lights = new(128);
    private readonly DrawVertexUV2D[] _lightQuad = new DrawVertexUV2D[6];

    #endregion

    #region Light selection

    private int GatherLights(MapId mapId, Box2Rotated worldBounds, Box2 worldAabb)
    {
        _lightCandidates.Clear();
        _lights.Clear();

        var maxLights = Math.Max(0, _configuration.GetCVar(CVars.MaxLightCount));
        var maxShadowLights = Math.Max(0, _configuration.GetCVar(CVars.MaxShadowcastingLights));
        if (maxLights == 0 || maxShadowLights == 0)
            return 0;

        var maxRadius = _configuration.GetCVar(CVars.MaxLightRadius);
        var candidates = _lightCandidates;
        foreach (var (treeUid, tree) in _lightTree.GetIntersectingTrees(mapId, worldAabb.Enlarged(maxRadius)))
        {
            var localBounds = _transformSystem.GetInvWorldMatrix(treeUid).TransformBox(worldBounds);
            tree.Tree.QueryAabb(
                ref candidates,
                static (ref List<Entity<PointLightComponent, TransformComponent>> state,
                    in ComponentTreeEntry<PointLightComponent> entry) =>
                {
                    state.Add(entry);
                    return true;
                },
                localBounds);
        }

        var acceptedLights = 0;
        for (var i = 0; i < _lightCandidates.Count && acceptedLights < maxLights; i++)
        {
            var candidate = _lightCandidates[i];
            var light = candidate.Comp1;
            if (!light.Enabled || light.ContainerOccluded)
                continue;

            var (position, rotation) = _transformSystem.GetWorldPositionRotation(candidate.Comp2);
            position += rotation.RotateVec(light.Offset);

            if (!new Circle(position, light.Radius).Intersects(worldAabb))
                continue;

            acceptedLights++;
            // Keep every shadow-casting light in the capacity selection, even if it currently
            // has no visible contribution. Clyde applies MaxShadowcastingLights before drawing too.
            if (!light.CastShadows)
                continue;

            _lights.Add(new LightData(
                candidate.Owner,
                light,
                position,
                rotation,
                Vector2.DistanceSquared(position, worldAabb.Center)));
        }

        if (_lights.Count > maxShadowLights)
        {
            _lights.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));
            _lights.RemoveRange(maxShadowLights, _lights.Count - maxShadowLights);
        }

        return _lights.Count;
    }

    private int FilterVisibleLights()
    {
        var visibleCount = 0;
        for (var i = 0; i < _lights.Count; i++)
        {
            var light = _lights[i];
            if (!IsLightHardFovVisible(light.Position))
                continue;

            var directionalVisibility = GetDirectionalSourceVisibility(light.Position);
            if (directionalVisibility <= 0f && !_renderLocalFovException)
                continue;

            _lights[visibleCount++] = light with { DirectionalVisibility = directionalVisibility };
        }

        if (visibleCount < _lights.Count)
            _lights.RemoveRange(visibleCount, _lights.Count - visibleCount);

        return visibleCount;
    }

    #endregion

    #region Light contribution

    private ShaderInstance GetContributionShader(
        in LightData light,
        CachedResources resources,
        float visibility,
        bool localContribution)
    {
        var softness = _configuration.GetCVar(CVars.LightSoftShadows)
            ? Math.Clamp(light.Component.Softness, 0f, 4f)
            : 0f;

        var casterMask = resources.CasterMask!;
        var occluderMask = resources.OccluderMask!;
        var localCenter = Vector2.Transform(light.Position, _targetMatrix);
        var targetSize = (Vector2) casterMask.Size;
        var lightCenterUv = localCenter / targetSize;
        lightCenterUv.Y = 1f - lightCenterUv.Y;

        return resources.GetContributionShader(
            _contributionPrototype,
            light.Owner,
            localContribution,
            casterMask.Texture,
            occluderMask.Texture,
            light.Component.Color,
            light.Component.Radius,
            light.Component.Energy * visibility,
            light.Component.Falloff,
            light.Component.CurveFactor,
            softness,
            lightCenterUv);
    }

    private void SetLightQuad(in LightData light)
    {
        var rotation = light.Component.MaskPath == null
            ? Angle.Zero
            : light.Component.Rotation + (light.Component.MaskAutoRotate ? light.Rotation : Angle.Zero);
        var radius = light.Component.Radius;

        var bottomLeft = light.Position + rotation.RotateVec(new Vector2(-radius, -radius));
        var bottomRight = light.Position + rotation.RotateVec(new Vector2(radius, -radius));
        var topRight = light.Position + rotation.RotateVec(new Vector2(radius, radius));
        var topLeft = light.Position + rotation.RotateVec(new Vector2(-radius, radius));

        _lightQuad[0] = new DrawVertexUV2D(bottomLeft, new Vector2(0f, 1f));
        _lightQuad[1] = new DrawVertexUV2D(bottomRight, new Vector2(1f, 1f));
        _lightQuad[2] = new DrawVertexUV2D(topRight, new Vector2(1f, 0f));
        _lightQuad[3] = new DrawVertexUV2D(bottomLeft, new Vector2(0f, 1f));
        _lightQuad[4] = new DrawVertexUV2D(topRight, new Vector2(1f, 0f));
        _lightQuad[5] = new DrawVertexUV2D(topLeft, new Vector2(0f, 0f));
    }

    #endregion

    #region Cached light data

    private readonly record struct LightData(
        EntityUid Owner,
        PointLightComponent Component,
        Vector2 Position,
        Angle Rotation,
        float DistanceSquared,
        float DirectionalVisibility = 1f);

    #endregion
}

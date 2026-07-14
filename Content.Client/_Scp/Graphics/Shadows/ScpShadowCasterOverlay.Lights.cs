using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared;
using Robust.Shared.ComponentTrees;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    #region Light query state

    private readonly List<LightData> _lights = new(128);
    private readonly List<Vector2> _nonShadowLightPositions = new(128);
    private readonly DrawVertexUV2D[] _lightQuad = new DrawVertexUV2D[6];

    #endregion

    #region Light selection

    private int GatherLights(MapId mapId, Box2Rotated worldBounds, Box2 worldAabb)
    {
        _lights.Clear();
        _nonShadowLightPositions.Clear();

        if (_maxLights == 0 || _maxShadowLights == 0)
            return 0;

        var state = new LightQueryState(this, worldAabb);
        foreach (var (treeUid, tree) in _lightTree.GetIntersectingTrees(
                     mapId,
                     worldAabb.Enlarged(_maxLightRadius)))
        {
            var localBounds = _transformSystem.GetInvWorldMatrix(treeUid).TransformBox(worldBounds);
            tree.Tree.QueryAabb(ref state, QueryLight, localBounds);
            if (state.AcceptedLights >= _maxLights)
                break;
        }

        if (_lights.Count > _maxShadowLights)
        {
            _lights.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));
            _lights.RemoveRange(_maxShadowLights, _lights.Count - _maxShadowLights);
        }

        return _lights.Count;
    }

    private static bool QueryLight(
        ref LightQueryState state,
        in ComponentTreeEntry<PointLightComponent> entry)
    {
        var overlay = state.Overlay;
        if (state.AcceptedLights >= overlay._maxLights)
            return false;

        var light = entry.Component;
        if (!light.Enabled || light.ContainerOccluded)
            return true;

        var (position, rotation) = overlay._transformSystem.GetWorldPositionRotation(entry.Transform);
        if (light.Offset != Vector2.Zero)
            position += rotation.RotateVec(light.Offset);
        if (!new Circle(position, light.Radius).Intersects(state.WorldAabb))
            return true;

        state.AcceptedLights++;
        if (light.CastShadows)
        {
            overlay._lights.Add(new LightData(
                entry.Uid,
                light,
                position,
                position,
                rotation,
                Vector2.DistanceSquared(position, state.WorldAabb.Center)));
        }
        else
        {
            overlay._nonShadowLightPositions.Add(position);
        }

        return state.AcceptedLights < overlay._maxLights;
    }

    private void ApplyProjectionPositions()
    {
        for (var i = 0; i < _lights.Count; i++)
        {
            var light = _lights[i];
            if (_foregroundProjectionPositions.TryGetValue(light.Owner, out var projectionPosition))
                _lights[i] = light with { ProjectionPosition = projectionPosition };
        }
    }

    private float GetLightSoftness(in LightData light)
    {
        return _softShadows ? Math.Clamp(light.Component.Softness, 0f, 4f) : 0f;
    }

    #endregion

    #region Light contribution

    private ShaderInstance GetContributionShader(
        in LightData light,
        CachedResources resources,
        float softness)
    {
        var casterMask = resources.CasterMask!;
        var occluderMask = resources.OccluderMask!;
        var localCenter = Vector2.Transform(light.Position, _targetMatrix);
        var targetSize = (Vector2) casterMask.Size;
        var lightCenterUv = localCenter / targetSize;
        lightCenterUv.Y = 1f - lightCenterUv.Y;

        return resources.GetContributionShader(
            _contributionPrototype,
            light.Owner,
            casterMask.Texture,
            occluderMask.Texture,
            light.Component.Color,
            light.Component.Radius,
            light.Component.Energy,
            light.Component.Falloff,
            light.Component.CurveFactor,
            softness,
            lightCenterUv);
    }

    private void SetLightQuad(in LightData light)
    {
        var radius = light.Component.Radius;
        var right = new Vector2(radius, 0f);
        if (light.Component.MaskPath != null)
        {
            var rotation = light.Component.Rotation +
                (light.Component.MaskAutoRotate ? light.Rotation : Angle.Zero);
            right = rotation.RotateVec(right);
        }

        var up = new Vector2(-right.Y, right.X);
        var bottomLeft = light.Position - right - up;
        var bottomRight = light.Position + right - up;
        var topRight = light.Position + right + up;
        var topLeft = light.Position - right + up;

        _lightQuad[0].Position = bottomLeft;
        _lightQuad[1].Position = bottomRight;
        _lightQuad[2].Position = topRight;
        _lightQuad[3].Position = bottomLeft;
        _lightQuad[4].Position = topRight;
        _lightQuad[5].Position = topLeft;
    }

    private void InitializeLightQuad()
    {
        _lightQuad[0].UV = new Vector2(0f, 1f);
        _lightQuad[1].UV = new Vector2(1f, 1f);
        _lightQuad[2].UV = new Vector2(1f, 0f);
        _lightQuad[3].UV = new Vector2(0f, 1f);
        _lightQuad[4].UV = new Vector2(1f, 0f);
        _lightQuad[5].UV = new Vector2(0f, 0f);
    }

    #endregion

    #region Cached light data

    private readonly record struct LightData(
        EntityUid Owner,
        PointLightComponent Component,
        Vector2 Position,
        Vector2 ProjectionPosition,
        Angle Rotation,
        float DistanceSquared);

    private struct LightQueryState(ScpShadowCasterOverlay overlay, Box2 worldAabb)
    {
        public readonly ScpShadowCasterOverlay Overlay = overlay;
        public readonly Box2 WorldAabb = worldAabb;
        public int AcceptedLights;
    }

    #endregion
}

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

        if (_system.MaxLights == 0 || _system.MaxShadowLights == 0)
            return 0;

        var state = new LightQueryState(this, worldAabb);
        foreach (var (treeUid, tree) in _lightTree.GetIntersectingTrees(
                     mapId,
                     worldAabb.Enlarged(_system.MaxLightRadius)))
        {
            var localBounds = _transformSystem.GetInvWorldMatrix(treeUid).TransformBox(worldBounds);
            tree.Tree.QueryAabb(ref state, QueryLight, localBounds);
            if (state.AcceptedLights >= _system.MaxLights)
                break;
        }

        if (_lights.Count > _system.MaxShadowLights)
        {
            _lights.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));
            _lights.RemoveRange(_system.MaxShadowLights, _lights.Count - _system.MaxShadowLights);
        }

        return _lights.Count;
    }

    private static bool QueryLight(
        ref LightQueryState state,
        in ComponentTreeEntry<PointLightComponent> entry)
    {
        var overlay = state.Overlay;
        if (state.AcceptedLights >= overlay._system.MaxLights)
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
        if (light.CastShadows && light.Radius > 0f && light.Energy > 0f)
        {
            overlay._lights.Add(new LightData(
                entry.Uid,
                position,
                position,
                rotation,
                light.Color,
                light.MaskPath,
                light.Rotation,
                light.Radius,
                light.Energy,
                light.Falloff,
                light.CurveFactor,
                light.Softness,
                light.MaskAutoRotate,
                Vector2.DistanceSquared(position, state.WorldAabb.Center)));
        }
        else
        {
            overlay._nonShadowLightPositions.Add(position);
        }

        return state.AcceptedLights < overlay._system.MaxLights;
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
        return _system.SoftShadows ? Math.Clamp(light.Softness, 0f, 4f) : 0f;
    }

    #endregion

    #region Light contribution

    private ShaderInstance GetContributionShader(
        in LightData light,
        CachedResources resources,
        float softness,
        bool outsideFov,
        bool hasOccluders)
    {
        var shadowMask = resources.ShadowMask!;
        var localCenter = Vector2.Transform(light.Position, _targetMatrix);
        var targetSize = (Vector2) shadowMask.Size;
        var lightCenterUv = localCenter / targetSize;
        lightCenterUv.Y = 1f - lightCenterUv.Y;

        return resources.GetContributionShader(
            _contributionPrototype,
            light.Owner,
            shadowMask.Texture,
            light.Color,
            light.Radius,
            light.Energy,
            light.Falloff,
            light.CurveFactor,
            softness,
            outsideFov,
            hasOccluders,
            lightCenterUv);
    }

    private void SetLightQuad(in LightData light, Box2 casterBounds, float softness)
    {
        var radius = light.Radius;
        if (light.MaskPath == null)
        {
            var radiusVector = new Vector2(radius);
            var lightBounds = new Box2(
                light.Position - radiusVector,
                light.Position + radiusVector);
            var padding = (1f + 3f * softness) * _worldUnitsPerMaskPixel;
            var bounds = casterBounds.Enlarged(padding).Intersect(lightBounds);
            var inverseDiameter = 0.5f / radius;
            var uvLeft = (bounds.Left - lightBounds.Left) * inverseDiameter;
            var uvRight = (bounds.Right - lightBounds.Left) * inverseDiameter;
            var uvBottom = 1f - (bounds.Bottom - lightBounds.Bottom) * inverseDiameter;
            var uvTop = 1f - (bounds.Top - lightBounds.Bottom) * inverseDiameter;

            SetLightQuadPositions(
                bounds.BottomLeft,
                bounds.BottomRight,
                bounds.TopRight,
                bounds.TopLeft);
            SetLightQuadUvs(uvLeft, uvBottom, uvRight, uvTop);
            return;
        }

        var right = new Vector2(radius, 0f);
        var rotation = light.MaskRotation +
            (light.MaskAutoRotate ? light.EntityRotation : Angle.Zero);
        right = rotation.RotateVec(right);

        var up = new Vector2(-right.Y, right.X);
        var bottomLeft = light.Position - right - up;
        var bottomRight = light.Position + right - up;
        var topRight = light.Position + right + up;
        var topLeft = light.Position - right + up;

        SetLightQuadPositions(bottomLeft, bottomRight, topRight, topLeft);
        SetLightQuadUvs(0f, 1f, 1f, 0f);
    }

    private void InitializeLightQuad()
    {
        SetLightQuadUvs(0f, 1f, 1f, 0f);
    }

    private void SetLightQuadPositions(
        Vector2 bottomLeft,
        Vector2 bottomRight,
        Vector2 topRight,
        Vector2 topLeft)
    {
        _lightQuad[0].Position = bottomLeft;
        _lightQuad[1].Position = bottomRight;
        _lightQuad[2].Position = topRight;
        _lightQuad[3].Position = bottomLeft;
        _lightQuad[4].Position = topRight;
        _lightQuad[5].Position = topLeft;
    }

    private void SetLightQuadUvs(float left, float bottom, float right, float top)
    {
        _lightQuad[0].UV = new Vector2(left, bottom);
        _lightQuad[1].UV = new Vector2(right, bottom);
        _lightQuad[2].UV = new Vector2(right, top);
        _lightQuad[3].UV = new Vector2(left, bottom);
        _lightQuad[4].UV = new Vector2(right, top);
        _lightQuad[5].UV = new Vector2(left, top);
    }

    #endregion

    #region Cached light data

    private readonly record struct LightData(
        EntityUid Owner,
        Vector2 Position,
        Vector2 ProjectionPosition,
        Angle EntityRotation,
        Color Color,
        string? MaskPath,
        Angle MaskRotation,
        float Radius,
        float Energy,
        float Falloff,
        float CurveFactor,
        float Softness,
        bool MaskAutoRotate,
        float DistanceSquared);

    private struct LightQueryState(ScpShadowCasterOverlay overlay, Box2 worldAabb)
    {
        public readonly ScpShadowCasterOverlay Overlay = overlay;
        public readonly Box2 WorldAabb = worldAabb;
        public int AcceptedLights;
    }

    #endregion
}

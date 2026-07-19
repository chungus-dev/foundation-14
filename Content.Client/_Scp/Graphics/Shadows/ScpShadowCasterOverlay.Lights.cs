using System.Numerics;
using Robust.Client.Graphics;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    #region Light frame state

    private readonly DrawVertexUV2D[] _lightQuad = new DrawVertexUV2D[6];

    #endregion

    #region Light preparation

    private void ApplyAlphaProjectionPositions()
    {
        for (var i = 0; i < _system.ViewportLights.Count; i++)
        {
            var light = _system.ViewportLights[i];
            if (_alphaProjectionPositions.TryGetValue(light.Owner, out var projectionPosition))
                _system.ViewportLights[i] = light with { ProjectionPosition = projectionPosition };
        }
    }

    private float GetLightSoftness(in ScpShadowLightData light)
    {
        return _system.SoftShadows ? Math.Clamp(light.Softness, 0f, 4f) : 0f;
    }

    #endregion

    #region Light contribution

    private void AppendLightQuad(
        List<DrawVertexUV2DColor> vertices,
        in ScpShadowLightData light,
        Vector2 parameters)
    {
        SetLightQuad(light);

        var color = Color.FromSrgb(light.Color).WithAlpha(light.Energy);
        for (var i = 0; i < _lightQuad.Length; i++)
        {
            var source = _lightQuad[i];
            vertices.Add(new DrawVertexUV2DColor(source.Position, source.UV, color)
            {
                UV2 = parameters,
            });
        }
    }

    private void SetLightQuad(in ScpShadowLightData light)
    {
        var radius = light.Radius;
        if (light.Mask == null)
        {
            var offset = new Vector2(radius);
            SetLightQuadPositions(
                light.Position - offset,
                light.Position + new Vector2(radius, -radius),
                light.Position + offset,
                light.Position + new Vector2(-radius, radius));
            return;
        }

        var right = new Vector2(radius, 0f);
        var rotation = light.MaskRotation +
            (light.MaskAutoRotate ? light.EntityRotation : Angle.Zero);
        right = rotation.RotateVec(right);

        var up = new Vector2(-right.Y, right.X);
        SetLightQuadPositions(
            light.Position - right - up,
            light.Position + right - up,
            light.Position + right + up,
            light.Position - right + up);
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
}

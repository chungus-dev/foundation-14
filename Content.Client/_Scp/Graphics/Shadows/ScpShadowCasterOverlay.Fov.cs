using System.Numerics;
using Content.Shared._Scp.Vision.FOV;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const float FovAdditionalMarginMeters = 0.4f;

    #region FOV frame state

    private EntityUid? _localPlayerCaster;
    private EyeComponent? _directionalEye;
    private FieldOfViewComponent? _directionalFov;
    private TransformComponent? _directionalTransform;
    private Vector2 _eyeWorldPosition;
    private Vector2 _directionalViewerPosition;
    private Vector2 _directionalFovOffset;
    private float _directionalIgnoreRadiusPixels;
    private float _directionalIgnoreFeatherPixels;
    private bool _directionalFovActive;
    private bool _hardFovActive;
    private bool _renderLocalFovException;
    private readonly CompositeFovShaderState _normalCompositeFovState = new();
    private readonly CompositeFovShaderState _localCompositeFovState = new();

    private void PrepareFovContext(in OverlayDrawArgs args, IEye eye)
    {
        _eyeWorldPosition = eye.Position.Position;
        _hardFovActive = _lightManager.DrawHardFov && eye.DrawFov;
        _directionalFovActive = false;
        _renderLocalFovException = false;
        _localPlayerCaster = null;
        _directionalEye = null;
        _directionalFov = null;
        _directionalTransform = null;

        if (_fieldOfViewManagement.PlayerEntity is not { } player ||
            !ReferenceEquals(args.Viewport.Eye, player.Comp1.Eye))
        {
            return;
        }

        _localPlayerCaster = player.Owner;
        _directionalEye = player.Comp1;
        _directionalFov = player.Comp2;
        _directionalTransform = player.Comp3;
        _directionalViewerPosition = _transformSystem.GetWorldPosition(player.Comp3);
        _directionalFovActive = true;

        if (!_localPlayerShadowOutsideFov ||
            !_shadowQuery.TryGetComponent(player.Owner, out var shadow))
        {
            return;
        }

        var quality = shadow.Kind == ScpShadowCasterKind.Mob ? _mobQuality : _objectQuality;
        _renderLocalFovException = quality != ScpShadowQuality.Disabled;
    }

    private void PrepareFovRenderParameters(IClydeViewport viewport, Vector2 lightScale)
    {
        if (!_directionalFovActive ||
            _localPlayerCaster is not { } localPlayer ||
            _directionalEye == null ||
            _directionalFov == null ||
            _directionalTransform == null)
        {
            return;
        }

        _directionalFovOffset = GetDirectionalFovOffset(
            localPlayer,
            _directionalTransform,
            _directionalEye) * lightScale;

        var pixelScale = (lightScale.X + lightScale.Y) * 0.5f;
        _directionalIgnoreRadiusPixels =
            (_directionalFov.ConeIgnoreRadius + FovAdditionalMarginMeters) *
            EyeManager.PixelsPerMeter /
            _directionalEye.Zoom.X *
            pixelScale;
        _directionalIgnoreFeatherPixels =
            (_directionalFov.ConeIgnoreFeather + FovAdditionalMarginMeters) *
            EyeManager.PixelsPerMeter /
            _directionalEye.Zoom.X *
            pixelScale;
    }

    private Vector2 GetDirectionalFovOffset(
        EntityUid localPlayer,
        TransformComponent transform,
        EyeComponent eye)
    {
        if (eye.Offset == Vector2.Zero)
            return Vector2.Zero;

        var playerCoordinates = _transformSystem.GetMapCoordinates(localPlayer, transform);
        var offsetCoordinates = new MapCoordinates(
            playerCoordinates.Position + eye.Offset,
            playerCoordinates.MapId);
        var playerScreen = _eyeManager.MapToScreen(playerCoordinates);
        var offsetScreen = _eyeManager.MapToScreen(offsetCoordinates);
        var offset = playerScreen.Position - offsetScreen.Position;
        return offset * new Vector2(1f, -1f);
    }

    #endregion

    #region Directional FOV

    private float GetDirectionalSourceVisibility(Vector2 lightPosition)
    {
        if (!_directionalFovActive || _directionalFov == null)
            return 1f;

        var offset = lightPosition - _directionalViewerPosition;
        var distance = offset.Length();
        var ignoreRadius = _directionalFov.ConeIgnoreRadius + FovAdditionalMarginMeters;
        var ignoreFeather = _directionalFov.ConeIgnoreFeather + FovAdditionalMarginMeters;
        var radialVisibility = ignoreFeather <= GeometryEpsilon
            ? distance <= ignoreRadius ? 1f : 0f
            : 1f - Math.Clamp((distance - ignoreRadius) / ignoreFeather, 0f, 1f);

        if (offset.LengthSquared() <= GeometryEpsilon * GeometryEpsilon)
            return 1f;

        var angleDifference = offset.ToWorldAngle() - _directionalFov.CurrentAngle;
        var cosine = MathF.Cos((float) angleDifference.Theta);
        var coneLimit = MathF.Cos(MathHelper.DegreesToRadians(_directionalFov.Angle * 0.5f + 5f));
        var coneFeather = MathHelper.DegreesToRadians(_directionalFov.AngleFeather) * 0.5f;
        var angularVisibility = SmoothStep(coneLimit, coneLimit + coneFeather, cosine);
        return Math.Max(radialVisibility, angularVisibility);
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        if (maximum <= minimum + GeometryEpsilon)
            return value >= maximum ? 1f : 0f;

        var amount = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return amount * amount * (3f - 2f * amount);
    }

    private void SetCompositeFovParameters()
    {
        SetCompositeFovParameters(
            _subtractShader,
            _normalCompositeFovState,
            _directionalFovActive ? 1 : 0);
        SetCompositeFovParameters(
            _localSubtractShader,
            _localCompositeFovState,
            _directionalFovActive ? 2 : 0);
    }

    private void SetCompositeFovParameters(
        ShaderInstance shader,
        CompositeFovShaderState state,
        int mode)
    {
        if (state.Mode != mode)
        {
            state.Mode = mode;
            shader.SetParameter("directionalFovMode", mode);
        }

        if (mode == 0 || _directionalFov == null)
            return;

        var parametersDirty = false;
        state.SetParameter(0, _directionalFov.Angle, ref parametersDirty);
        state.SetParameter(1, _directionalFov.AngleFeather, ref parametersDirty);
        state.SetParameter(2, _directionalIgnoreRadiusPixels, ref parametersDirty);
        state.SetParameter(3, _directionalIgnoreFeatherPixels, ref parametersDirty);
        state.SetParameter(4, (float) _directionalFov.CurrentAngle.Theta, ref parametersDirty);
        if (parametersDirty)
            shader.SetParameter("directionalFovParameters", state.Parameters);

        if (state.Offset[0] != _directionalFovOffset)
        {
            state.Offset[0] = _directionalFovOffset;
            shader.SetParameter("directionalFovOffset", state.Offset);
        }
    }

    #endregion

    #region Hard FOV

    private bool IsLightHardFovVisible(Vector2 lightPosition)
    {
        if (!_hardFovActive)
            return true;

        var direction = lightPosition - _eyeWorldPosition;
        var distance = direction.Length();
        if (distance <= NearLightDistance)
            return true;

        var rayEnd = lightPosition - direction / distance * NearLightDistance;
        var minimum = Vector2.Min(_eyeWorldPosition, rayEnd);
        var maximum = Vector2.Max(_eyeWorldPosition, rayEnd);
        var queryBounds = new Box2(minimum, maximum).Enlarged(GeometryEpsilon);

        for (var i = 0; i < _frameOccluders.Count; i++)
        {
            var occluder = _frameOccluders[i];
            if (!occluder.Bounds.Intersects(queryBounds))
                continue;

            var localStart = Vector2.Transform(_eyeWorldPosition, occluder.InverseWorldMatrix);
            var localEnd = Vector2.Transform(rayEnd, occluder.InverseWorldMatrix);
            if (SegmentIntersectsBox(localStart, localEnd, occluder.LocalBounds))
                return false;
        }

        return true;
    }

    private static bool SegmentIntersectsBox(Vector2 start, Vector2 end, Box2 box)
    {
        var direction = end - start;
        var minimum = 0f;
        var maximum = 1f;

        return ClipSegmentAxis(start.X, direction.X, box.Left, box.Right, ref minimum, ref maximum) &&
            ClipSegmentAxis(start.Y, direction.Y, box.Bottom, box.Top, ref minimum, ref maximum);
    }

    private static bool ClipSegmentAxis(
        float start,
        float direction,
        float boxMinimum,
        float boxMaximum,
        ref float segmentMinimum,
        ref float segmentMaximum)
    {
        if (MathF.Abs(direction) <= GeometryEpsilon)
            return start >= boxMinimum && start <= boxMaximum;

        var inverse = 1f / direction;
        var first = (boxMinimum - start) * inverse;
        var second = (boxMaximum - start) * inverse;
        if (first > second)
            (first, second) = (second, first);

        segmentMinimum = Math.Max(segmentMinimum, first);
        segmentMaximum = Math.Min(segmentMaximum, second);
        return segmentMinimum <= segmentMaximum;
    }

    #endregion

    #region Stencil setup

    private void ConfigureStencilShaders()
    {
        _subtractShader.Stencil = new StencilParameters
        {
            Enabled = true,
            Ref = 0x80,
            ReadMask = 0x80,
            WriteMask = 0,
            Func = StencilFunc.Equal,
            Op = StencilOp.Keep,
        };
        _localSubtractShader.Stencil = new StencilParameters
        {
            Enabled = true,
            Ref = 0xFE,
            ReadMask = 0xFF,
            WriteMask = 0,
            Func = StencilFunc.Equal,
            Op = StencilOp.Keep,
        };
        _stencilShader.Stencil = new StencilParameters
        {
            Enabled = true,
            Ref = 0,
            ReadMask = 0xFF,
            WriteMask = 0xFF,
            Func = StencilFunc.Always,
            Op = StencilOp.Replace,
        };
        _localStencilShader.Stencil = new StencilParameters
        {
            Enabled = true,
            Ref = 0xFE,
            ReadMask = 0x80,
            WriteMask = 0xFF,
            Func = StencilFunc.Equal,
            Op = StencilOp.Replace,
        };
    }

    #endregion

    #region Cached shader parameters

    private sealed class CompositeFovShaderState
    {
        public int Mode = int.MinValue;
        public readonly float[] Parameters =
            [float.NaN, float.NaN, float.NaN, float.NaN, float.NaN];
        public readonly Vector2[] Offset = [new(float.NaN)];

        public void SetParameter(int index, float value, ref bool dirty)
        {
            if (Parameters[index] == value)
                return;

            Parameters[index] = value;
            dirty = true;
        }
    }

    #endregion
}

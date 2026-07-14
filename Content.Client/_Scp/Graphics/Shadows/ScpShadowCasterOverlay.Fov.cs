using System.Numerics;
using Content.Shared._Scp.Vision.FOV;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const float FovAdditionalMarginMeters = 0.4f;
    private const float MinimumFovFeatherPixels = 0.0001f;
    private const float MinimumFovConeThresholdSpan = 0.0001f;
    private const int HardFovStencilBit = 0x80;
    private const int InsideProtectionStencilBit = 0x01;
    private const int OutsideProtectionStencilBit = 0x02;

    #region FOV frame state

    private EntityUid? _localPlayerCaster;
    private EyeComponent? _directionalEye;
    private FieldOfViewComponent? _directionalFov;
    private TransformComponent? _directionalTransform;
    private Vector2 _directionalFovOffset;
    private Vector2 _directionalViewDirection;
    private Vector2 _directionalRadialParameters;
    private Vector2 _directionalConeThresholds;
    private bool _directionalFovActive;
    private bool _renderLocalFovException;
    private readonly CompositeFovShaderState _normalCompositeFovState = new();
    private readonly CompositeFovShaderState _outsideCompositeFovState = new();

    private void PrepareFovContext(in OverlayDrawArgs args, IEye eye)
    {
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
        _directionalFovActive = true;

        if (!_system.LocalPlayerShadowOutsideFov ||
            !_shadowQuery.TryGetComponent(player.Owner, out var shadow))
        {
            return;
        }

        var quality = shadow.Kind == ScpShadowCasterKind.Mob
            ? _system.MobQuality
            : _system.ObjectQuality;
        _renderLocalFovException = quality != ScpShadowQuality.Disabled;
    }

    private DirectionalFovVisibility GetSpriteDirectionalFovVisibility(
        EntityUid uid,
        TransformComponent transform)
    {
        if (!_directionalFovActive)
            return DirectionalFovVisibility.Inside;

        if (!_fovOccludableQuery.TryGetComponent(uid, out var occludable))
            return DirectionalFovVisibility.Both;

        if (occludable.Source == _localPlayerCaster ||
            transform.Anchored && !occludable.OccludeIfAnchored)
        {
            return DirectionalFovVisibility.Both;
        }

        return occludable.Inverted
            ? DirectionalFovVisibility.Outside
            : DirectionalFovVisibility.Inside;
    }

    private DirectionalFovVisibility GetCasterDirectionalFovVisibility(
        EntityUid caster,
        TransformComponent transform)
    {
        var visibility = GetSpriteDirectionalFovVisibility(caster, transform);
        if (caster != _localPlayerCaster)
            return visibility;

        visibility |= DirectionalFovVisibility.Inside;
        return _renderLocalFovException
            ? visibility | DirectionalFovVisibility.Outside
            : visibility & ~DirectionalFovVisibility.Outside;
    }

    private float GetSpriteDirectionalFovAlpha(EntityUid uid, TransformComponent transform)
    {
        if (!_directionalFovActive ||
            !_fovOccludableQuery.TryGetComponent(uid, out var occludable) ||
            occludable.Source == _localPlayerCaster ||
            transform.Anchored && !occludable.OccludeIfAnchored ||
            _localPlayerCaster is not { } localPlayer ||
            _directionalFov == null ||
            _directionalTransform == null)
        {
            return 1f;
        }

        var alpha = _fieldOfViewSystem.GetVisibilityAlpha(
            (localPlayer, _directionalTransform),
            (uid, transform),
            _directionalFov.Angle,
            _directionalFov.AngleFeather,
            true,
            _directionalFov.ConeIgnoreRadius,
            _directionalFov.ConeIgnoreFeather);
        return occludable.Inverted ? 1f - alpha : alpha;
    }

    private void PrepareFovRenderParameters(Vector2 lightScale)
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
        var ignoreRadiusPixels =
            (_directionalFov.ConeIgnoreRadius + FovAdditionalMarginMeters) *
            EyeManager.PixelsPerMeter /
            _directionalEye.Zoom.X *
            pixelScale;
        var ignoreFeatherPixels = MathF.Max(
            (_directionalFov.ConeIgnoreFeather + FovAdditionalMarginMeters) *
            EyeManager.PixelsPerMeter /
            _directionalEye.Zoom.X *
            pixelScale,
            MinimumFovFeatherPixels);

        var viewAngle = (float) _directionalFov.CurrentAngle.Theta;
        _directionalViewDirection = new Vector2(MathF.Sin(viewAngle), -MathF.Cos(viewAngle));
        _directionalRadialParameters = new Vector2(
            ignoreFeatherPixels,
            ignoreRadiusPixels / ignoreFeatherPixels);

        var coneLimit = MathF.Cos(MathHelper.DegreesToRadians(_directionalFov.Angle * 0.5f + 5f));
        _directionalConeThresholds = new Vector2(
            coneLimit,
            coneLimit + MathF.Max(
                MathHelper.DegreesToRadians(_directionalFov.AngleFeather) * 0.5f,
                MinimumFovConeThresholdSpan));
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

    #region Directional FOV composite

    private void SetCompositeFovParameters()
    {
        SetCompositeFovParameters(
            _subtractShader,
            _normalCompositeFovState,
            _directionalFovActive ? 1 : 0);
        SetCompositeFovParameters(
            _outsideSubtractShader,
            _outsideCompositeFovState,
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

        if (mode == 0)
            return;

        var parametersDirty = false;
        state.SetParameter(0, _directionalFovOffset, ref parametersDirty);
        state.SetParameter(1, _directionalViewDirection, ref parametersDirty);
        state.SetParameter(2, _directionalRadialParameters, ref parametersDirty);
        state.SetParameter(3, _directionalConeThresholds, ref parametersDirty);
        if (parametersDirty)
            shader.SetParameter("directionalFovParameters", state.Parameters);
    }

    #endregion

    #region Stencil setup

    private void ConfigureStencilShaders()
    {
        // Hard FOV leaves all bits set in visible pixels. The two low bits gate
        // inside/outside composites and are cleared together under visible sprites.
        _subtractShader.Stencil = new StencilParameters
        {
            Enabled = true,
            Ref = HardFovStencilBit | InsideProtectionStencilBit,
            ReadMask = HardFovStencilBit | InsideProtectionStencilBit,
            WriteMask = 0,
            Func = StencilFunc.Equal,
            Op = StencilOp.Keep,
        };
        _outsideSubtractShader.Stencil = new StencilParameters
        {
            Enabled = true,
            Ref = HardFovStencilBit | OutsideProtectionStencilBit,
            ReadMask = HardFovStencilBit | OutsideProtectionStencilBit,
            WriteMask = 0,
            Func = StencilFunc.Equal,
            Op = StencilOp.Keep,
        };
        _stencilShader.Stencil = new StencilParameters
        {
            Enabled = true,
            Ref = HardFovStencilBit,
            ReadMask = HardFovStencilBit,
            WriteMask = InsideProtectionStencilBit | OutsideProtectionStencilBit,
            Func = StencilFunc.Equal,
            Op = StencilOp.Replace,
        };
    }

    #endregion

    #region Cached shader parameters

    private sealed class CompositeFovShaderState
    {
        public int Mode = int.MinValue;
        public readonly Vector2[] Parameters =
            [new(float.NaN), new(float.NaN), new(float.NaN), new(float.NaN)];

        public void SetParameter(int index, Vector2 value, ref bool dirty)
        {
            if (Parameters[index] == value)
                return;

            Parameters[index] = value;
            dirty = true;
        }
    }

    [Flags]
    private enum DirectionalFovVisibility : byte
    {
        None = 0,
        Inside = 1 << 0,
        Outside = 1 << 1,
        Both = Inside | Outside,
    }

    #endregion
}

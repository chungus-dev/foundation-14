using System.Numerics;
using Content.Shared._Scp.Vision.FOV;
using Robust.Client.Graphics;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const float FovAdditionalMarginMeters = 0.4f;
    private const float MinimumFovFeatherPixels = 0.0001f;
    private const float MinimumFovConeThresholdSpan = 0.0001f;

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

    private void PrepareFovContext(in OverlayDrawArgs args)
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
            _directionalEye,
            viewport) * lightScale;

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
        EyeComponent eye,
        IClydeViewport viewport)
    {
        if (eye.Offset == Vector2.Zero)
            return Vector2.Zero;

        var playerCoordinates = _transformSystem.GetMapCoordinates(localPlayer, transform);
        var playerScreen = viewport.WorldToLocal(playerCoordinates.Position);
        var offsetScreen = viewport.WorldToLocal(playerCoordinates.Position + eye.Offset);
        var offset = playerScreen - offsetScreen;
        return offset * new Vector2(1f, -1f);
    }

    #endregion

    #region Cached FOV classification

    [Flags]
    private enum DirectionalFovVisibility : byte
    {
        Inside = 1 << 0,
        Outside = 1 << 1,
        Both = Inside | Outside,
    }

    #endregion
}

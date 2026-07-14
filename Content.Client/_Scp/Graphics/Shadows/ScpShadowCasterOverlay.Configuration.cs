using Content.Shared._Scp.ScpCCVars;
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    // Keeps configuration reads and subscription lifetime out of the render-path partial.
    #region Cached configuration

    private ConfigurationMultiSubscriptionBuilder _configurationSubscription = default!;

    private ScpShadowQuality _mobQuality;
    private ScpShadowQuality _objectQuality;
    private bool _localPlayerShadowOutsideFov;
    private int _maxLights;
    private int _maxShadowLights;
    private int _maxOccluders;
    private float _maxLightRadius;
    private bool _softShadows;
    private bool _lightBlur;

    private void SubscribeConfiguration()
    {
        _configurationSubscription = _configuration.SubscribeMultiple()
            .OnValueChanged(
                ScpCCVars.MobShadowQuality,
                value => _mobQuality = ClampQuality(value),
                true)
            .OnValueChanged(
                ScpCCVars.ObjectShadowQuality,
                value => _objectQuality = ClampQuality(value),
                true)
            .OnValueChanged(
                ScpCCVars.LocalPlayerShadowOutsideFov,
                value => _localPlayerShadowOutsideFov = value,
                true)
            .OnValueChanged(CVars.MaxLightCount, value => _maxLights = Math.Max(0, value), true)
            .OnValueChanged(
                CVars.MaxShadowcastingLights,
                value => _maxShadowLights = Math.Max(0, value),
                true)
            .OnValueChanged(CVars.MaxOccluderCount, value => _maxOccluders = Math.Max(1024, value), true)
            .OnValueChanged(CVars.MaxLightRadius, value => _maxLightRadius = Math.Max(0f, value), true)
            .OnValueChanged(CVars.LightSoftShadows, value => _softShadows = value, true)
            .OnValueChanged(CVars.LightBlur, value => _lightBlur = value, true);
    }

    private static ScpShadowQuality ClampQuality(int value)
    {
        return (ScpShadowQuality) Math.Clamp(
            value,
            (int) ScpShadowQuality.Disabled,
            (int) ScpShadowQuality.Sprite);
    }

    #endregion
}

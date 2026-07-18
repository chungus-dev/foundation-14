using Content.Shared._Scp.ScpCCVars;
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterSystem
{
    #region Cached configuration

    [Dependency] private IConfigurationManager _configuration = default!;

    private ConfigurationMultiSubscriptionBuilder _configurationSubscription = default!;

    internal bool ContentLightingEnabled { get; private set; }
    internal ScpShadowQuality MobQuality { get; private set; }
    internal ScpShadowQuality ObjectQuality { get; private set; }
    internal bool LocalPlayerShadowOutsideFov { get; private set; }
    internal int MaxLights { get; private set; }
    internal int MaxShadowLights { get; private set; }
    internal int MaxOccluders { get; private set; }
    internal float MaxLightRadius { get; private set; }
    internal bool SoftShadows { get; private set; }
    internal bool LightBlur { get; private set; }
    internal bool PersistentShadowAtlas { get; private set; }
    internal int MaxDeferredShadowFrames { get; private set; }

    private void InitializeConfiguration()
    {
        NormalizeQualityCVar(ScpCCVars.MobShadowQuality);
        NormalizeQualityCVar(ScpCCVars.ObjectShadowQuality);

        _configurationSubscription = _configuration.SubscribeMultiple()
            .OnValueChanged(
                ScpCCVars.ContentLighting,
                value => ContentLightingEnabled = value,
                true)
            .OnValueChanged(
                ScpCCVars.MobShadowQuality,
                value => MobQuality = ClampQuality(value),
                true)
            .OnValueChanged(
                ScpCCVars.ObjectShadowQuality,
                value => ObjectQuality = ClampQuality(value),
                true)
            .OnValueChanged(
                ScpCCVars.LocalPlayerShadowOutsideFov,
                value => LocalPlayerShadowOutsideFov = value,
                true)
            .OnValueChanged(CVars.MaxLightCount, value => MaxLights = Math.Max(0, value), true)
            .OnValueChanged(
                CVars.MaxShadowcastingLights,
                value => MaxShadowLights = Math.Max(0, value),
                true)
            .OnValueChanged(CVars.MaxOccluderCount, value => MaxOccluders = Math.Max(1024, value), true)
            .OnValueChanged(CVars.MaxLightRadius, value => MaxLightRadius = Math.Max(0f, value), true)
            .OnValueChanged(CVars.LightSoftShadows, value => SoftShadows = value, true)
            .OnValueChanged(CVars.LightBlur, value => LightBlur = value, true)
            .OnValueChanged(
                ScpCCVars.ContentShadowPersistentAtlas,
                value => PersistentShadowAtlas = value,
                true)
            .OnValueChanged(
                ScpCCVars.ContentShadowMaxDeferredFrames,
                value => MaxDeferredShadowFrames = Math.Clamp(value, 0, 3),
                true);
    }

    private void ShutdownConfiguration()
    {
        _configurationSubscription.Dispose();
    }

    private void NormalizeQualityCVar(CVarDef<int> cVar)
    {
        var value = _configuration.GetCVar(cVar);
        var normalized = (int) ClampQuality(value);
        if (normalized != value)
            _configuration.SetCVar(cVar, normalized);
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

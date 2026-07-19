using Content.Shared._Scp.ScpCCVars;
using JetBrains.Annotations;
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterSystem
{
    #region Cached configuration

    [Dependency] private IConfigurationManager _cfg = default!;

    [PublicAPI] public bool ContentLightingEnabled { get; private set; }
    [PublicAPI] public ScpShadowQuality MobQuality { get; private set; }
    [PublicAPI] public ScpShadowQuality ObjectQuality { get; private set; }
    [PublicAPI] public bool LocalPlayerShadowOutsideFov { get; private set; }
    [PublicAPI] public int MaxLights { get; private set; }
    [PublicAPI] public int MaxShadowLights { get; private set; }
    [PublicAPI] public int MaxOccluders { get; private set; }
    [PublicAPI] public float MaxLightRadius { get; private set; }
    [PublicAPI] public bool SoftShadows { get; private set; }

    private void InitializeConfiguration()
    {
        NormalizeQualityCVar(ScpCCVars.MobShadowQuality);
        NormalizeQualityCVar(ScpCCVars.ObjectShadowQuality);

        Subs.CVar(_cfg,
            ScpCCVars.ContentLighting,
            value => ContentLightingEnabled = value,
            true);
        Subs.CVar(_cfg,
            ScpCCVars.MobShadowQuality,
            value => MobQuality = ClampQuality(value),
            true);
        Subs.CVar(_cfg,
            ScpCCVars.ObjectShadowQuality,
            value => ObjectQuality = ClampQuality(value),
            true);
        Subs.CVar(_cfg,
            ScpCCVars.LocalPlayerShadowOutsideFov,
            value => LocalPlayerShadowOutsideFov = value,
            true);
        Subs.CVar(_cfg,
            CVars.MaxLightCount,
            value => MaxLights = Math.Max(0, value),
            true);
        Subs.CVar(_cfg,
            CVars.MaxShadowcastingLights,
            value => MaxShadowLights = Math.Max(0, value),
            true);
        Subs.CVar(_cfg,
            CVars.MaxOccluderCount,
            value => MaxOccluders = Math.Max(1024, value),
            true);
        Subs.CVar(_cfg,
            CVars.MaxLightRadius,
            value => MaxLightRadius = Math.Max(0f, value),
            true);
        Subs.CVar(_cfg,
            CVars.LightSoftShadows,
            value => SoftShadows = value,
            true);
    }

    private void NormalizeQualityCVar(CVarDef<int> cVar)
    {
        var value = _cfg.GetCVar(cVar);
        var normalized = (int) ClampQuality(value);
        if (normalized != value)
            _cfg.SetCVar(cVar, normalized);
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

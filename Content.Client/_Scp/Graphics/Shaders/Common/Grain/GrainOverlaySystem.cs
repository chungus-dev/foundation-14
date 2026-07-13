using Content.Shared._Scp.Graphics.Shaders;
using Content.Shared._Scp.Graphics.Shaders.Grain;
using Robust.Shared.Configuration;
using Content.Shared._Scp.ScpCCVars;
using Robust.Client.Player;
using Robust.Shared.Player;

namespace Content.Client._Scp.Graphics.Shaders.Common.Grain;

public sealed partial class GrainOverlaySystem : ComponentOverlaySystem<GrainOverlay, GrainOverlayComponent>
{
    [Dependency] private SharedShaderStrengthSystem _shaderStrength = default!;
    [Dependency] private CompatibilityModeActiveWarningSystem _compatibility = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _player = default!;

    public override void Initialize()
    {
        base.Initialize();

        Overlay = new GrainOverlay();

        SubscribeLocalEvent<GrainOverlayComponent, AfterAutoHandleStateEvent>(OnAdditionalStrengthChanged);

        Subs.CVar(_cfg, ScpCCVars.GrainToggleOverlay, ToggleGrainOverlay, true);
        Subs.CVar(_cfg, ScpCCVars.GrainStrength, SetBaseStrength, true);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        Overlay.Dispose();
    }

    protected override void OnPlayerAttached(Entity<GrainOverlayComponent> ent, ref LocalPlayerAttachedEvent args)
    {
        base.OnPlayerAttached(ent, ref args);

        ToggleGrainOverlay(_cfg.GetCVar(ScpCCVars.GrainToggleOverlay));
        SetBaseStrength(_cfg.GetCVar(ScpCCVars.GrainStrength));
    }

    private void OnAdditionalStrengthChanged(Entity<GrainOverlayComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (_player.LocalEntity != ent)
            return;

        Overlay.CurrentStrength = ent.Comp.CurrentStrength;
    }

    private void ToggleGrainOverlay(bool option)
    {
        if (_compatibility.IsCompatibilityModeEnabled && !_compatibility.CompabilityUseShaders)
            return;

        Enabled = option;

        ToggleOverlay(option);
    }

    private void SetBaseStrength(int value)
    {
        var player = _player.LocalEntity;

        if (!player.HasValue)
            return;

        if (!_shaderStrength.TrySetBaseStrength<GrainOverlayComponent>(player.Value, value, out var component))
            return;

        Overlay.CurrentStrength = component.CurrentStrength;
    }
}

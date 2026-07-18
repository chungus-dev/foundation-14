using Content.Shared.CCVar;
using Robust.Client.Graphics;
using Robust.Shared; // Scp edit - read the light blur CVar.
using Robust.Shared.Configuration;

namespace Content.Client.Light.EntitySystems;

public sealed partial class PlanetLightSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfgManager = default!;
    [Dependency] private IOverlayManager _overlayMan = default!;

    /// <summary>
    /// Enables / disables the ambient occlusion overlay.
    /// </summary>
    public bool AmbientOcclusion
    {
        get => _ambientOcclusion;
        set
        {
            if (_ambientOcclusion == value)
                return;

            _ambientOcclusion = value;

            if (value)
            {
                _overlayMan.AddOverlay(new AmbientOcclusionOverlay());
            }
            // Scp edit start - release ambient occlusion render targets when disabling the overlay.
            else if (_overlayMan.TryGetOverlay<AmbientOcclusionOverlay>(out var overlay))
            {
                _overlayMan.RemoveOverlay(overlay);
                overlay.Dispose();
            }
            // Scp edit end
        }
    }

    private bool _ambientOcclusion;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GetClearColorEvent>(OnClearColor);

        // Scp edit start - keep CVar subscriptions bound to the system lifetime.
        Subs.CVar(_cfgManager, CCVars.AmbientOcclusion, value => AmbientOcclusion = value, true);
        Subs.CVar(_cfgManager, CVars.LightBlur, OnLightBlurChanged, true);
        // Scp edit end

        _overlayMan.AddOverlay(new BeforeLightTargetOverlay());
        _overlayMan.AddOverlay(new RoofOverlay(EntityManager));
        _overlayMan.AddOverlay(new TileEmissionOverlay(EntityManager));
        _overlayMan.AddOverlay(new SunShadowOverlay());
        _overlayMan.AddOverlay(new AfterLightTargetOverlay());
    }

    // Scp added start - avoid the content blur pass and release its target when disabled.
    private void OnLightBlurChanged(bool enabled)
    {
        if (enabled)
        {
            if (!_overlayMan.HasOverlay<LightBlurOverlay>())
                _overlayMan.AddOverlay(new LightBlurOverlay());
            return;
        }

        if (!_overlayMan.TryGetOverlay<LightBlurOverlay>(out var overlay))
            return;

        _overlayMan.RemoveOverlay(overlay);
        overlay.Dispose();
    }
    // Scp added end

    private void OnClearColor(ref GetClearColorEvent ev)
    {
        ev.Color = Color.Transparent;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        // Scp edit start - dispose optional viewport resources.
        AmbientOcclusion = false;
        OnLightBlurChanged(false);
        // Scp edit end
        _overlayMan.RemoveOverlay<BeforeLightTargetOverlay>();
        _overlayMan.RemoveOverlay<RoofOverlay>();
        _overlayMan.RemoveOverlay<TileEmissionOverlay>();
        _overlayMan.RemoveOverlay<SunShadowOverlay>();
        _overlayMan.RemoveOverlay<AfterLightTargetOverlay>();
    }
}

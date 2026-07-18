using Content.Client.Clickable;
using Robust.Client.Graphics;
using Robust.Shared.Profiling;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Owns the client-only entity-shadow overlay.
/// </summary>
public sealed partial class ScpShadowCasterSystem : EntitySystem
{
    #region Dependencies

    [Dependency] private IClickMapManager _clickMaps = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private ProfManager _prof = default!;

    #endregion

    private ScpShadowCasterOverlay _overlay = default!;

    internal ScpShadowContourCache ContourCache { get; private set; } = default!;

    #region EntitySystem lifecycle

    public override void Initialize()
    {
        base.Initialize();

        InitializeConfiguration();
        InitializeViewportLighting();
        ContourCache = new ScpShadowContourCache(_clickMaps);
        _overlay = new ScpShadowCasterOverlay(this);
        _overlayManager.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        _overlayManager.RemoveOverlay(_overlay);
        _overlay.Dispose();
        ShutdownViewportLighting();
        ShutdownConfiguration();

        base.Shutdown();
    }

    #endregion
}

using Content.Client.Clickable;
using Robust.Client.Graphics;
using Robust.Shared.Profiling;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Owns the client-only entity-shadow overlay.
/// </summary>
public sealed partial class ScpShadowCasterSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private ProfManager _prof = default!;
    [Dependency] public ScpShadowContourCache ContourCache = default!;

    private ScpShadowCasterOverlay? _overlay;

    #region EntitySystem lifecycle

    public override void Initialize()
    {
        base.Initialize();

        InitializeConfiguration();

        _overlay = new ScpShadowCasterOverlay(this);
        _overlayManager.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        if (_overlay != null)
        {
            _overlayManager.RemoveOverlay(_overlay);
            _overlay.Dispose();
        }

        ShutdownViewportLighting();

        base.Shutdown();
    }

    #endregion
}

using Robust.Client.Graphics;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Owns the client-only entity-shadow overlay.
/// </summary>
public sealed partial class ScpShadowCasterSystem : EntitySystem
{
    #region Dependencies

    [Dependency] private IOverlayManager _overlayManager = default!;

    #endregion

    private ScpShadowCasterOverlay _overlay = default!;

    #region EntitySystem lifecycle

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new ScpShadowCasterOverlay();
        _overlayManager.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        _overlayManager.RemoveOverlay(_overlay);
        _overlay.Dispose();

        base.Shutdown();
    }

    #endregion
}

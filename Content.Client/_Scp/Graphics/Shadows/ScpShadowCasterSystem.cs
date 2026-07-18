using Content.Client.Clickable;
using Robust.Client.GameObjects;
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
    [Dependency] private IComponentFactory _componentFactory = default!;
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
        InitializeSpriteIndex();
        EntityManager.ComponentRemoved += OnComponentRemoved;
        EntityManager.EntityDeleted += OnEntityDeleted;
        ContourCache = new ScpShadowContourCache(_clickMaps);
        _overlay = new ScpShadowCasterOverlay(this);
        _overlayManager.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        EntityManager.EntityDeleted -= OnEntityDeleted;
        EntityManager.ComponentRemoved -= OnComponentRemoved;
        _overlayManager.RemoveOverlay(_overlay);
        _overlay.Dispose();
        ShutdownSpriteIndex();
        ShutdownViewportLighting();
        ShutdownConfiguration();

        base.Shutdown();
    }

    #endregion

    private void OnComponentRemoved(RemovedComponentEventArgs args)
    {
        if (args.BaseArgs.Component is PointLightComponent light)
            _overlay?.RemovePointLight(args.BaseArgs.Owner, light.CreationTick);
    }

    private void OnEntityDeleted(Entity<MetaDataComponent> entity)
    {
        RemoveDeletedSpriteIndexEntity(entity.Owner, entity.Comp.NetEntity);
        _overlay?.RemoveGeometrySource(entity.Owner, entity.Comp.NetEntity);
    }
}

internal enum ScpPersistentFallbackReason : byte
{
    None,
    HardShadow,
    OversizedCell,
    KnownLayoutFailure,
    LayoutOverflow,
    AllocationFailure,
    CpuBudget,
}

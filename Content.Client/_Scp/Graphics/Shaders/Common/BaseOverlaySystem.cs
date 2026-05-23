using JetBrains.Annotations;
using Robust.Client.Graphics;

namespace Content.Client._Scp.Graphics.Shaders.Common;

/// <summary>
/// Базовая система для быстрого разворачивания шейдеров и оверлеев.
/// Устраняет проблемы копипасты и расхождения API для переключения шейдеров.
/// </summary>
/// <typeparam name="T">Оверлей, которым будет оперировать система.</typeparam>
/// <remarks>
/// Так как песочница не дает создавать оверлей внутри этой системы,
/// то каждая система-наследник обязана создавать оверлей вручную в своем методе инициализации
/// </remarks>
/// <seealso cref="Overlay"/>
/// <seealso cref="OverlayManager"/>
/// <br/> TODO: Поддержка nullability для <see cref="Overlay"/>
/// <br/> TODO: Базовый класс для всех оверлеев, использующих эту систему.
public abstract class BaseOverlaySystem<T> : EntitySystem where T : Overlay
{
    [Dependency] protected readonly IOverlayManager OverlayManager = default!;
    [Dependency] protected readonly CompatibilityModeActiveWarningSystem Compatibility = default!;

    protected T Overlay = default!;
    [PublicAPI] public bool Enabled = true;
    [PublicAPI] public bool DisableOnCompatibilityMode = true;

    [PublicAPI] public bool DisposeOnShutdown = true;

    public override void Shutdown()
    {
        base.Shutdown();

        if (!DisposeOnShutdown)
            return;

        OverlayManager.RemoveOverlay(Overlay);
        Overlay.Dispose();
    }

    #region Public API

    [PublicAPI]
    public void ToggleOverlay()
    {
        if (OverlayManager.HasOverlay<T>())
            RemoveOverlay();
        else
            AddOverlay();
    }

    [PublicAPI]
    public void ToggleOverlay(bool enable)
    {
        var exists = OverlayManager.HasOverlay<T>();

        if (enable && !exists)
            AddOverlay();
        else if (!enable && exists)
            RemoveOverlay();
    }

    [PublicAPI]
    public bool TryAddOverlay()
    {
        if (OverlayManager.HasOverlay<T>())
            return false;

        AddOverlay();
        return true;
    }

    [PublicAPI]
    public void AddOverlay()
    {
        if (!Enabled)
            return;

        if (!Compatibility.ShouldUseShaders && DisableOnCompatibilityMode)
            return;

        OverlayManager.AddOverlay(Overlay);
    }

    [PublicAPI]
    public bool TryRemoveOverlay()
    {
        if (!OverlayManager.HasOverlay<T>())
            return false;

        RemoveOverlay();
        return true;
    }

    [PublicAPI]
    public void RemoveOverlay()
    {
        OverlayManager.RemoveOverlay(Overlay);
    }

    #endregion
}

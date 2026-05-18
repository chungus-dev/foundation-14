using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Anomaly.Scp096.Protection;

/// <summary>
/// Компонент, защищающий пользователя от возможности увидеть SCP-096.
/// </summary>
/// TODO: Избавиться от проверки каждый тик, вместо этого использовать Cooldown поле и TimeSpan
[RegisterComponent, NetworkedComponent]
public sealed partial class Scp096ProtectionComponent : Component
{
    /// <summary>
    /// Шанс не сработать во время проверки видимости.
    /// </summary>
    [DataField]
    public float ProblemChance
    {
        get;
        set => field = Math.Clamp(value, 0f, 1f);
    }
}

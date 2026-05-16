using Robust.Shared.GameStates;

namespace Content.Shared.Chemistry.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PillComponent : Component
{
    /// <summary>
    /// The pill id. Used for networking & serializing pill visuals.
    /// </summary>
    [AutoNetworkedField]
    [DataField("pillType")]
    [ViewVariables(VVAccess.ReadWrite)]
    public uint PillType;

    // Scp edit start - поддержка нестандартных спрайтов пилюль
    [DataField]
    public bool UseStandardVisuals = true;
    // Scp edit end
}

using Robust.Shared.GameStates;

namespace Content.Server._Scp.Facility.ComplexElevator;

[RegisterComponent]
public sealed partial class ElevatorPointComponent : Component
{
    [DataField]
    public string FloorId = "";
}

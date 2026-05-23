namespace Content.Server._Scp.Facility.ComplexElevator;

[RegisterComponent]
public sealed partial class ElevatorDoorComponent : Component
{
    [DataField]
    public string ElevatorId = "";

    [DataField]
    public string Floor = "";
}

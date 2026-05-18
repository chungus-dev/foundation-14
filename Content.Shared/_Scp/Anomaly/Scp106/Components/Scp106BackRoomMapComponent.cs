using Content.Shared.Procedural;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Anomaly.Scp106.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class Scp106BackRoomMapComponent : Component
{
    [DataField]
    public ProtoId<DungeonConfigPrototype> Dungeon = "Backrooms";
}

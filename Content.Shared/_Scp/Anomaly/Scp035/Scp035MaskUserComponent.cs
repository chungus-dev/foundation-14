using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Anomaly.Scp035;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class Scp035MaskUserComponent : Component
{
    [AutoNetworkedField, ViewVariables]
    public EntityUid? Mask;

    [AutoNetworkedField, ViewVariables]
    public HashSet<EntityUid> Servants = [];

    [DataField]
    public EntProtoId ServantsProto = "MobServant035";

    [DataField]
    public int MaxServants = 3;

    [DataField]
    public EntProtoId DeadSpawnProto = "Ash";

    [DataField]
    public float MeleeDamageModificator = 4;

    [DataField]
    public TimeSpan ActionStunDuration = TimeSpan.FromSeconds(10);

    [AutoNetworkedField, ViewVariables]
    public MaskOrderType CurrentOrder = MaskOrderType.Follow;

    [AutoNetworkedField]
    public List<EntityUid> Actions = [];

    [AutoNetworkedField, ViewVariables]
    public Dictionary<MaskOrderType, EntityUid> OrderActions = new();
}

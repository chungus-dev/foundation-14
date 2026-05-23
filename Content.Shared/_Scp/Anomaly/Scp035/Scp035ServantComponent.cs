using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Anomaly.Scp035;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class Scp035ServantComponent : Component
{
    [AutoNetworkedField, ViewVariables]
    public EntityUid? User;
}

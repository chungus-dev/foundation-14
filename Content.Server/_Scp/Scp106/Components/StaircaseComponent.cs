using Content.Shared.Whitelist;

namespace Content.Server._Scp.Scp106.Components;

[RegisterComponent]
public sealed partial class StaircaseComponent : Component
{
    [DataField]
    public EntityWhitelist? Whitelist = new()
    {
        Tags = [ "UpStairs106" ],
    };

    [DataField]
    public EntityWhitelist? Blacklist;

    [ViewVariables]
    public EntityUid? LinkedStair;

    [ViewVariables]
    public bool Generating = false;
}

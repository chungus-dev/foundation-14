using Content.Shared.Whitelist;
using Robust.Shared.Audio;

namespace Content.Server._Scp.Other.KillGlobalSound;

[RegisterComponent]
public sealed partial class KillGlobalSoundComponent : Component
{
    [DataField(required: true)]
    public SoundSpecifier Sound = default!;

    [DataField(required: true)]
    public EntityWhitelist OriginWhitelist = default!;

    [DataField]
    public float Chance = 1f;

    [DataField]
    public float MaxRadius = 30f;
}

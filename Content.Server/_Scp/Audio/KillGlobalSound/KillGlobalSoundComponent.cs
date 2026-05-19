using Content.Shared.Whitelist;
using Robust.Shared.Audio;

namespace Content.Server._Scp.Audio.KillGlobalSound;

[RegisterComponent]
public sealed partial class KillGlobalSoundComponent : Component
{
    [DataField(required: true)]
    public SoundSpecifier Sound;

    [DataField(required: true)]
    public EntityWhitelist OriginWhitelist;

    [DataField]
    public float Chance = 1f;

    [DataField]
    public float MaxRadius = 30f;
}

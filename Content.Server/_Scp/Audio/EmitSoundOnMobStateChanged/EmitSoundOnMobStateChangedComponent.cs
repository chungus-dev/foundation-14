using Content.Shared.Mobs;
using Robust.Shared.Audio;

namespace Content.Server._Scp.Audio.EmitSoundOnMobStateChanged;

[RegisterComponent]
public sealed partial class EmitSoundOnMobStateChangedComponent : Component
{
    [DataField(required: true)]
    public SoundSpecifier Sound;

    [DataField]
    public MobState State = MobState.Dead;
}

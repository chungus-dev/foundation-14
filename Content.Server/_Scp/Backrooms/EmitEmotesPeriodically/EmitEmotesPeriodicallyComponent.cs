using Content.Shared.Chat.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._Scp.Backrooms.EmitEmotesPeriodically;

[RegisterComponent]
public sealed partial class EmitEmotesPeriodicallyComponent : Component
{
    [DataField(required: true)]
    public HashSet<ProtoId<EmotePrototype>> Emotes = [];

    [DataField]
    public EmitMode Mode = EmitMode.All;

    #region Timings

    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    [DataField]
    public int CooldownVariations;

    [ViewVariables]
    public TimeSpan CooldownAddition = TimeSpan.Zero;

    [ViewVariables]
    public TimeSpan LastTimeEmit = TimeSpan.Zero;

    #endregion
}

public enum EmitMode : byte
{
    All,
    Random,
}


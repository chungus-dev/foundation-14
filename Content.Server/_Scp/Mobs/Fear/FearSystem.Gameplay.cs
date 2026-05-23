using Content.Server.Chat.Systems;
using Content.Shared._Scp.Mobs.Fear.Components;
using Content.Shared.Chat.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._Scp.Mobs.Fear;

public sealed partial class FearSystem
{
    [Dependency] private readonly ChatSystem _chat = default!;

    private static readonly ProtoId<EmotePrototype> ScreamProtoId = "Scream";

    /// <summary>
    /// Пытается закричать, если увиденный объект настолько страшный.
    /// </summary>
    protected override void TryScream(Entity<FearComponent> ent)
    {
        base.TryScream(ent);

        if (ent.Comp.State < ent.Comp.ScreamRequiredState)
            return;

        _chat.TryEmoteWithChat(ent, ScreamProtoId);
    }
}

using Content.Shared._Scp.EntityEffects;
using Content.Shared._Scp.Mobs.Fear.Components;
using Content.Shared.EntityEffects;

namespace Content.Server._Scp.Mobs.Fear;

public sealed partial class FearSystem
{
    private void InitializeEntityEffects()
    {
        SubscribeLocalEvent<FearComponent, EntityEffectEvent<CalmDownEffect>>(OnExecuteCalmDown);
    }

    private void OnExecuteCalmDown(Entity<FearComponent> ent, ref EntityEffectEvent<CalmDownEffect> args)
    {
        ent.Comp.NextTimeDecreaseFearLevel -= args.Effect.SpeedUpBy;
    }
}

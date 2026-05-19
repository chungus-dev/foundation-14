using Content.Shared.Administration.Systems;
using Content.Shared.EntityEffects;
using Content.Shared.Mobs.Components;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.EntityEffects;

public sealed class RejuvenateEntityEffectSystem : EntityEffectSystem<MobStateComponent, RejuvenateEffect>
{
    [Dependency] private readonly RejuvenateSystem _rejuvenate = default!;

    protected override void Effect(Entity<MobStateComponent> entity, ref EntityEffectEvent<RejuvenateEffect> args)
    {
        _rejuvenate.PerformRejuvenate(entity);
    }
}

[UsedImplicitly]
public sealed partial class RejuvenateEffect : EntityEffectBase<RejuvenateEffect>
{
    public override string EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) =>
        Loc.GetString("reagent-effect-guidebook-scp500");
}

